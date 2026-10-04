using System.Collections.Concurrent;
using System.Diagnostics;
using MonitorKing.Agent.Collectors;

namespace MonitorKing.Agent;

/// <summary>
/// Boucle d'échantillonnage : appelle chaque collecteur toutes les <see cref="AgentOptions.SampleIntervalMs"/>,
/// garde les 30 dernières minutes en mémoire (vue temps réel) et écrit une ligne agrégée toutes les 10 s dans SQLite.
/// </summary>
public sealed class CollectorHost : BackgroundService
{
    private const int LiveSeriesMinutes = 30;
    private const int RecentSnapshotMinutes = 5;

    private readonly IReadOnlyList<ICollector> _collectors;
    private readonly Database _database;
    private readonly AgentOptions _options;
    private readonly ILogger<CollectorHost> _logger;
    private readonly ConcurrentDictionary<string, MetricDef> _definitions = new();
    private readonly ConcurrentDictionary<string, CollectorStatus> _status = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, Queue<(long Ts, double Value)>> _series = new();
    private readonly Queue<Snapshot> _recent = new();
    private readonly WindowAccumulator _window = new();

    public CollectorHost(Database database, AgentOptions options, MachineInfo machine, ILogger<CollectorHost> logger, ILoggerFactory loggers)
    {
        _database = database;
        _options = options;
        _logger = logger;
        _collectors = new ICollector[]
        {
            new SystemCollector(),
            new GpuCollector(),
            new NetworkCollector(MachineInfo.IsAdministrator, loggers.CreateLogger<NetworkCollector>()),
            new ProcessCollector(database), // après le GPU et le réseau : les rattache aux applications
            new SensorCollector(MachineInfo.IsAdministrator, TimeSpan.FromMilliseconds(options.SensorIntervalMs), machine.BoardLabel, loggers.CreateLogger<SensorCollector>()),
            new WifiCollector(),
            new HangCollector(database), // après les processus : nomme l'application gelée
        };
    }

    public Snapshot? Latest { get; private set; }

    public IEnumerable<MetricDef> Definitions => _definitions.Values.OrderBy(d => d.Group).ThenBy(d => d.Key);

    public IEnumerable<CollectorStatus> Status => _status.Values.OrderBy(s => s.Id);

    public void ReportStatus(CollectorStatus status) => _status[status.Id] = status;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMilliseconds(Math.Max(500, _options.SampleIntervalMs));
        using var timer = new PeriodicTimer(interval);
        var clock = Stopwatch.StartNew();
        var last = TimeSpan.Zero;
        var tick = 0L;
        _logger.LogInformation("Échantillonnage toutes les {Interval} ms, base : {Path}", interval.TotalMilliseconds, _database.Path);
        var started = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // Un gel resté ouvert n'est plus suivi par personne : on le clôt, et le serveur doit l'apprendre.
        if (_database.CloseOrphanHangs(started) is { } earliest) UploadService.ResendHangsFrom(_database, earliest);
        _database.RecordAgentStart(started);

        do
        {
            var now = clock.Elapsed;
            var elapsed = tick == 0 ? interval.TotalSeconds : (now - last).TotalSeconds;
            last = now;

            try
            {
                Sample(elapsed);
                if (++tick % Math.Max(1, _options.PersistEveryTicks) == 0) Persist();
                if (tick % 1800 == 1) ApplyRetention();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Échec du tick d'échantillonnage");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private void Sample(double elapsed)
    {
        var snapshot = new Snapshot { Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
        var context = new CollectContext(elapsed);

        foreach (var collector in _collectors)
        {
            var started = Stopwatch.GetTimestamp();
            string? error = null;
            try
            {
                collector.Collect(snapshot, context);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                _logger.LogDebug(ex, "Collecteur {Collector} en échec", collector.Id);
            }

            var ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            _status[collector.Id] = new CollectorStatus(collector.Id, collector.Label, error is null, error, collector.Hint, Math.Round(ms, 1), collector.Detail);
            foreach (var definition in collector.Metrics)
                _definitions.TryAdd(definition.Key, definition);
        }

        foreach (var key in snapshot.Metrics.Keys.Where(k => double.IsNaN(snapshot.Metrics[k]) || double.IsInfinity(snapshot.Metrics[k])).ToList())
            snapshot.Metrics.Remove(key);

        lock (_gate)
        {
            var seriesCutoff = snapshot.Ts - LiveSeriesMinutes * 60_000L;
            foreach (var (key, value) in snapshot.Metrics)
            {
                if (!_series.TryGetValue(key, out var queue)) _series[key] = queue = new Queue<(long, double)>();
                queue.Enqueue((snapshot.Ts, value));
                while (queue.Count > 0 && queue.Peek().Ts < seriesCutoff) queue.Dequeue();
            }

            _recent.Enqueue(snapshot);
            var recentCutoff = snapshot.Ts - RecentSnapshotMinutes * 60_000L;
            while (_recent.Count > 0 && _recent.Peek().Ts < recentCutoff) _recent.Dequeue();
        }

        _window.Add(snapshot);
        Latest = snapshot;
    }

    private void Persist()
    {
        var (ts, metrics, processes) = _window.Flush();
        if (metrics.Count == 0) return;
        var samples = metrics
            .Where(m => _definitions.ContainsKey(m.Key))
            .Select(m => (_definitions[m.Key], m.Value.Avg, m.Value.Max));
        _database.PersistWindow(ts, samples, processes);
    }

    private void ApplyRetention()
    {
        var now = DateTimeOffset.UtcNow;
        _database.ApplyRetention(
            now.AddDays(-_options.RetentionDays).ToUnixTimeMilliseconds(),
            now.AddDays(-_options.EventRetentionDays).ToUnixTimeMilliseconds());
    }

    /// <summary>Séries temps réel (30 dernières minutes, une valeur toutes les 2 s).</summary>
    public Dictionary<string, List<double[]>> LiveSeries(IEnumerable<string> keys, long from)
    {
        var result = new Dictionary<string, List<double[]>>();
        lock (_gate)
        {
            foreach (var key in keys)
            {
                if (!_series.TryGetValue(key, out var queue)) continue;
                result[key] = queue.Where(p => p.Ts >= from).Select(p => new[] { p.Ts, p.Value, p.Value }).ToList();
            }
        }

        return result;
    }

    /// <summary>Statistiques temps réel sur les dernières minutes, pour le diagnostic.</summary>
    public (Dictionary<string, (double Avg, double Max, double Last)> Metrics, List<ProcRow> Processes) RecentWindow(int minutes)
    {
        lock (_gate)
        {
            var to = Latest?.Ts ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var from = to - minutes * 60_000L;
            var stats = new Dictionary<string, (double, double, double)>();
            foreach (var (key, queue) in _series)
            {
                var points = queue.Where(p => p.Ts >= from).ToList();
                if (points.Count == 0) continue;
                stats[key] = (points.Average(p => p.Value), points.Max(p => p.Value), points[^1].Value);
            }

            var snapshots = _recent.Where(s => s.Ts >= from).ToList();
            var accumulator = new WindowAccumulator();
            foreach (var snapshot in snapshots) accumulator.Add(snapshot);
            return (stats, accumulator.Averages());
        }
    }

    public override void Dispose()
    {
        foreach (var collector in _collectors) collector.Dispose();
        base.Dispose();
    }
}

/// <summary>Agrège plusieurs ticks : moyenne et pic par métrique, consommation moyenne par application.</summary>
internal sealed class WindowAccumulator
{
    private readonly Dictionary<string, (double Sum, double Max, int Count)> _metrics = new();
    private readonly Dictionary<string, ProcRow> _processes = new(StringComparer.OrdinalIgnoreCase);
    private int _ticks;
    private long _lastTs;

    public void Add(Snapshot snapshot)
    {
        _ticks++;
        _lastTs = snapshot.Ts;
        foreach (var (key, value) in snapshot.Metrics)
        {
            var current = _metrics.GetValueOrDefault(key, (0, double.MinValue, 0));
            _metrics[key] = (current.Sum + value, Math.Max(current.Max, value), current.Count + 1);
        }

        foreach (var group in snapshot.Processes)
        {
            if (!_processes.TryGetValue(group.Name, out var row))
                _processes[group.Name] = row = new ProcRow { Name = group.Name };
            row.Description ??= group.Description;
            row.Via ??= group.Via;
            row.Cpu += group.Cpu;
            row.IoReadBps += group.IoReadBps;
            row.IoWriteBps += group.IoWriteBps;
            row.HardFaultsPerSec += group.HardFaultsPerSec;
            row.Gpu += group.Gpu;
            row.NetSendBps += group.NetSendBps;
            row.NetRecvBps += group.NetRecvBps;
            row.RamMb = Math.Max(row.RamMb, group.RamMb);
            row.CommitMb = Math.Max(row.CommitMb, group.CommitMb);
            row.VramMb = Math.Max(row.VramMb, group.VramMb);
            row.Count = Math.Max(row.Count, group.Count);
        }
    }

    /// <summary>Moyennes par application sur les ticks accumulés (sans vider).</summary>
    public List<ProcRow> Averages()
    {
        if (_ticks == 0) return new List<ProcRow>();
        return _processes.Values.Select(p => new ProcRow
        {
            Name = p.Name,
            Description = p.Description,
            Via = p.Via,
            Cpu = p.Cpu / _ticks,
            IoReadBps = p.IoReadBps / _ticks,
            IoWriteBps = p.IoWriteBps / _ticks,
            HardFaultsPerSec = p.HardFaultsPerSec / _ticks,
            Gpu = p.Gpu / _ticks,
            NetSendBps = p.NetSendBps / _ticks,
            NetRecvBps = p.NetRecvBps / _ticks,
            RamMb = p.RamMb,
            CommitMb = p.CommitMb,
            VramMb = p.VramMb,
            Count = p.Count,
        }).ToList();
    }

    public (long Ts, Dictionary<string, (double Avg, double Max)> Metrics, List<ProcRow> Processes) Flush()
    {
        var metrics = _metrics.ToDictionary(m => m.Key, m => (m.Value.Sum / m.Value.Count, m.Value.Max));
        var averages = Averages();

        // On ne garde que les applications qui comptent dans au moins une ressource.
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Top(Func<ProcRow, double> by, int count, double minimum)
        {
            foreach (var p in averages.Where(p => by(p) > minimum).OrderByDescending(by).Take(count)) keep.Add(p.Name);
        }

        Top(p => p.Cpu, 10, 0.1);
        Top(p => p.RamMb, 10, 0);
        Top(p => p.IoBps, 8, 1024);
        Top(p => p.HardFaultsPerSec, 5, 1);
        Top(p => p.Gpu, 5, 0.5);
        Top(p => p.VramMb, 5, 50);
        Top(p => p.NetBps, 8, 1024);

        var result = (_lastTs, metrics, averages.Where(p => keep.Contains(p.Name)).ToList());
        _metrics.Clear();
        _processes.Clear();
        _ticks = 0;
        return result;
    }
}
