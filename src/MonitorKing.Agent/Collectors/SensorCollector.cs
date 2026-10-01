using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using LibreHardwareMonitor.Hardware;

namespace MonitorKing.Agent.Collectors;

/// <summary>
/// Capteurs matériels via LibreHardwareMonitorLib : températures, ventilateurs, fréquences, puissance.
/// Sans droits administrateur, seuls les capteurs accessibles en mode utilisateur remontent
/// (GPU, refroidisseurs USB…). Les températures CPU et carte mère demandent l'administrateur
/// et le pilote PawnIO, que l'agent n'installe jamais lui-même.
/// La lecture (Super I/O, SMU, SMART des disques) prend plusieurs centaines de millisecondes :
/// elle tourne sur son propre fil, à son rythme, et chaque tick reprend la dernière lecture sans attendre.
/// </summary>
public sealed class SensorCollector : ICollector
{
    private readonly ConcurrentDictionary<string, MetricDef> _defs = new();
    private readonly bool _isAdmin;
    private readonly TimeSpan _interval;
    private readonly string _boardLabel;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly ManualResetEventSlim _stop = new(false);
    private List<SensorReading> _readings = new();
    private Dictionary<string, double> _metrics = new();
    private Thread? _thread;
    private Computer? _computer;

    public SensorCollector(bool isAdmin, TimeSpan interval, string boardLabel, ILogger logger)
    {
        _isAdmin = isAdmin;
        _interval = interval < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : interval;
        _boardLabel = boardLabel;
        _logger = logger;
    }

    private static bool IsBoard(HardwareType type) =>
        type is HardwareType.Motherboard or HardwareType.SuperIO or HardwareType.EmbeddedController;

    public string Id => "sensors";
    public string Label => "Capteurs matériels";
    public string? Hint { get; private set; }
    public string? Detail { get; private set; } = "Première lecture des capteurs en cours…";
    public IEnumerable<MetricDef> Metrics => _defs.Values;

    public void Collect(Snapshot snapshot, CollectContext context)
    {
        if (_thread is null)
        {
            _thread = new Thread(Loop) { IsBackground = true, Name = "MonitorKing.Sensors", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }

        lock (_gate)
        {
            snapshot.Sensors.AddRange(_readings);
            foreach (var (key, value) in _metrics) snapshot.Metrics[key] = value;
        }
    }

    private void Loop()
    {
        try
        {
            _computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMotherboardEnabled = true,
                IsStorageEnabled = true,
                IsControllerEnabled = true,
                IsBatteryEnabled = true,
            };
            _computer.Open();
        }
        catch (Exception ex)
        {
            Hint = "Capteurs indisponibles : " + ex.Message;
            _logger.LogWarning(ex, "Ouverture de LibreHardwareMonitor impossible");
            return;
        }

        do
        {
            var started = Stopwatch.GetTimestamp();
            var readings = new List<SensorReading>();
            var metrics = new Dictionary<string, double>();
            var timings = new List<(string Name, double Ms)>();
            var cpuTemperature = false;

            foreach (var hardware in _computer.Hardware)
            {
                var t0 = Stopwatch.GetTimestamp();
                Visit(hardware, readings, metrics, ref cpuTemperature);
                timings.Add((hardware.Name, Stopwatch.GetElapsedTime(t0).TotalMilliseconds));
            }

            // Les sondes de la carte mère passent après le processeur, la carte graphique et les disques.
            readings = readings.OrderBy(r => r.HardwareType == nameof(HardwareType.Motherboard) ? 1 : 0).ToList();

            lock (_gate)
            {
                _readings = readings;
                _metrics = metrics;
            }

            var elapsed = Stopwatch.GetElapsedTime(started);
            var slowest = timings.OrderByDescending(t => t.Ms).FirstOrDefault();
            var fr = CultureInfo.GetCultureInfo("fr-FR");
            var slowestText = slowest.Name is null ? "." : string.Create(fr, $" (le plus long : {slowest.Name}, {slowest.Ms:0} ms).");
            Detail = string.Create(fr, $"Lecture en {elapsed.TotalMilliseconds:0} ms toutes les {_interval.TotalSeconds:0} s, sur un fil séparé{slowestText}");
            Hint = cpuTemperature
                ? null
                : _isAdmin
                    ? "Températures CPU et carte mère illisibles : installe le pilote signé PawnIO (pawnio.eu) puis relance l'agent."
                    : "Températures CPU et carte mère : relance l'agent en administrateur, avec le pilote PawnIO installé. Le GPU fonctionne déjà.";

            var wait = _interval - elapsed;
            if (wait < TimeSpan.FromMilliseconds(500)) wait = TimeSpan.FromMilliseconds(500);
            if (_stop.Wait(wait)) break;
        }
        while (true);
    }

    private void Visit(IHardware hardware, List<SensorReading> readings, Dictionary<string, double> metrics, ref bool cpuTemperature)
    {
        try
        {
            hardware.Update();
        }
        catch (Exception)
        {
            return;
        }

        // Sans accès bas niveau (PawnIO), le CPU renvoie des valeurs factices (0 °C, 0 W, tension fixe) : on les écarte.
        var blindCpu = hardware.HardwareType == HardwareType.Cpu
            && !hardware.Sensors.Any(s => s.SensorType == SensorType.Temperature && s.Value > 0);

        // Les SSD NVMe annoncent leurs seuils d'alerte comme des « capteurs » : ce sont des limites, pas des mesures.
        var warningLimit = hardware.Sensors
            .FirstOrDefault(s => s.SensorType == SensorType.Temperature && IsThreshold(s) && s.Name.StartsWith("Warning", StringComparison.OrdinalIgnoreCase))
            ?.Value;

        // La puce Super I/O (ex. Nuvoton NCT6797D) mesure pour la carte mère : on affiche la carte, la puce reste en détail.
        var board = IsBoard(hardware.HardwareType);
        var displayName = board ? _boardLabel : hardware.Name;
        var displayType = board ? nameof(HardwareType.Motherboard) : hardware.HardwareType.ToString();
        var chip = board && hardware.HardwareType != HardwareType.Motherboard ? hardware.Name : null;

        foreach (var sensor in hardware.Sensors)
        {
            if (sensor.Value is not { } value || float.IsNaN(value) || float.IsInfinity(value)) continue;
            if (blindCpu && sensor.SensorType is SensorType.Temperature or SensorType.Power or SensorType.Clock or SensorType.Voltage) continue;
            if (sensor.SensorType == SensorType.Temperature && IsThreshold(sensor)) continue;
            var unit = UnitOf(sensor.SensorType);
            if (unit is null) continue;

            var key = "hw" + sensor.Identifier;
            readings.Add(new SensorReading(
                displayName,
                displayType,
                sensor.Name,
                sensor.SensorType.ToString(),
                Math.Round(value, 2),
                unit,
                key,
                sensor.SensorType == SensorType.Temperature ? warningLimit : null,
                chip));

            // Seuls les capteurs utiles au diagnostic sont historisés ; les autres restent visibles en direct.
            if (Historize(hardware, sensor))
            {
                metrics[key] = value;
                _defs.TryAdd(key, new MetricDef(key, $"{displayName} · {sensor.Name}", unit, "sensors",
                    sensor.SensorType is SensorType.Load or SensorType.Control or SensorType.Level ? 100 : null));
            }

            if (hardware.HardwareType == HardwareType.Cpu && sensor.SensorType == SensorType.Temperature && value > 0)
                cpuTemperature = true;
        }

        foreach (var sub in hardware.SubHardware)
            Visit(sub, readings, metrics, ref cpuTemperature);
    }

    private static bool IsThreshold(ISensor sensor) =>
        sensor.Name.Contains("Warning Temperature", StringComparison.OrdinalIgnoreCase)
        || sensor.Name.Contains("Critical Temperature", StringComparison.OrdinalIgnoreCase);

    private static bool Historize(IHardware hardware, ISensor sensor)
    {
        var gpu = hardware.HardwareType is HardwareType.GpuAmd or HardwareType.GpuNvidia or HardwareType.GpuIntel;
        return sensor.SensorType switch
        {
            SensorType.Temperature or SensorType.Fan or SensorType.Control or SensorType.Power or SensorType.Level => true,
            SensorType.Load => sensor.Name.Contains("Total", StringComparison.OrdinalIgnoreCase)
                || sensor.Name.Contains("Max", StringComparison.OrdinalIgnoreCase)
                || (gpu && sensor.Name is "GPU Core" or "GPU Memory"),
            SensorType.Clock => gpu || sensor.Name.Contains("Average", StringComparison.OrdinalIgnoreCase),
            SensorType.SmallData => gpu && sensor.Name == "GPU Memory Used",
            _ => false,
        };
    }

    private static string? UnitOf(SensorType type) => type switch
    {
        SensorType.Temperature => "°C",
        SensorType.Load => "%",
        SensorType.Control => "%",
        SensorType.Level => "%",
        SensorType.Fan => "tr/min",
        SensorType.Clock => "MHz",
        SensorType.Power => "W",
        SensorType.Voltage => "V",
        SensorType.Current => "A",
        SensorType.SmallData => "Mo",
        SensorType.Data => "Go",
        SensorType.Throughput => "o/s",
        SensorType.Energy => "mWh",
        SensorType.Flow => "L/h",
        SensorType.Noise => "dBA",
        _ => null,
    };

    public void Dispose()
    {
        _stop.Set();
        _thread?.Join(TimeSpan.FromSeconds(3));
        try
        {
            _computer?.Close();
        }
        catch (Exception)
        {
        }

        _stop.Dispose();
    }
}
