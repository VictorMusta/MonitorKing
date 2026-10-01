using System.Diagnostics;
using System.Text.RegularExpressions;

namespace MonitorKing.Agent.Collectors;

/// <summary>
/// Charge GPU totale et par processus, via les compteurs « GPU Engine » de Windows
/// (les mêmes que le Gestionnaire des tâches) : fonctionne sans droits administrateur, quel que soit le fabricant.
/// </summary>
public sealed partial class GpuCollector : ICollector
{
    private const double Gb = 1024d * 1024 * 1024;

    private PerformanceCounterCategory? _engines;
    private PerformanceCounterCategory? _adapterMemory;
    private PerformanceCounterCategory? _processMemory;
    private Dictionary<string, CounterSample> _previous = new();
    private bool _unavailable;

    public string Id => "gpu";
    public string Label => "Carte graphique";
    public string? Hint { get; private set; }

    public IEnumerable<MetricDef> Metrics { get; } = new MetricDef[]
    {
        new("gpu.total", "GPU (moteur le plus chargé)", "%", "gpu", 100),
        new("gpu.3d", "GPU · 3D", "%", "gpu", 100),
        new("gpu.video", "GPU · vidéo (encodage/décodage)", "%", "gpu", 100),
        new("gpu.compute", "GPU · calcul", "%", "gpu", 100),
        new("gpu.vram", "Mémoire vidéo utilisée", "Go", "gpu"),
    };

    [GeneratedRegex(@"^pid_(\d+)_luid_(0x[0-9a-f]+_0x[0-9a-f]+)_phys_(\d+)_eng_(\d+)_engtype_(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex EngineInstance();

    [GeneratedRegex(@"^pid_(\d+)_", RegexOptions.IgnoreCase)]
    private static partial Regex ProcessInstance();

    public void Collect(Snapshot snapshot, CollectContext context)
    {
        if (_unavailable) return;
        try
        {
            CollectEngines(snapshot, context);
            CollectMemory(snapshot, context);
        }
        catch (InvalidOperationException ex)
        {
            _unavailable = true;
            Hint = "Compteurs GPU indisponibles sur ce PC : " + ex.Message;
        }
    }

    private void CollectEngines(Snapshot snapshot, CollectContext context)
    {
        _engines ??= new PerformanceCounterCategory("GPU Engine");
        var utilization = _engines.ReadCategory()["Utilization Percentage"];
        if (utilization is null) return;

        var next = new Dictionary<string, CounterSample>(utilization.Count);
        var engines = new Dictionary<string, (double Sum, string Type)>();

        foreach (InstanceData instance in utilization.Values)
        {
            var name = instance.InstanceName;
            var sample = instance.Sample;
            next[name] = sample;
            if (!_previous.TryGetValue(name, out var prev)) continue;

            double value;
            try
            {
                value = CounterSample.Calculate(prev, sample);
            }
            catch (Exception)
            {
                continue;
            }

            if (value <= 0 || double.IsNaN(value)) continue;
            var match = EngineInstance().Match(name);
            if (!match.Success) continue;

            var pid = int.Parse(match.Groups[1].Value);
            var engineKey = $"{match.Groups[2].Value}_{match.Groups[3].Value}_{match.Groups[4].Value}";
            var type = match.Groups[5].Value;
            var engine = engines.GetValueOrDefault(engineKey);
            engines[engineKey] = (engine.Sum + value, type);

            if (!context.GpuByPid.TryGetValue(pid, out var current) || value > current.Utilization)
                context.GpuByPid[pid] = new GpuUsage(Math.Min(100, value), type);
        }

        _previous = next;

        double total = 0, threeD = 0, video = 0, compute = 0;
        foreach (var (_, (sum, type)) in engines)
        {
            var value = Math.Min(100, sum);
            total = Math.Max(total, value);
            if (type.Contains("3D", StringComparison.OrdinalIgnoreCase)) threeD = Math.Max(threeD, value);
            else if (type.Contains("Video", StringComparison.OrdinalIgnoreCase)) video = Math.Max(video, value);
            else if (type.Contains("Compute", StringComparison.OrdinalIgnoreCase)) compute = Math.Max(compute, value);
        }

        snapshot.Metrics["gpu.total"] = total;
        snapshot.Metrics["gpu.3d"] = threeD;
        snapshot.Metrics["gpu.video"] = video;
        snapshot.Metrics["gpu.compute"] = compute;
    }

    private void CollectMemory(Snapshot snapshot, CollectContext context)
    {
        _adapterMemory ??= new PerformanceCounterCategory("GPU Adapter Memory");
        var dedicated = _adapterMemory.ReadCategory()["Dedicated Usage"];
        if (dedicated is not null)
        {
            double bytes = 0;
            foreach (InstanceData instance in dedicated.Values) bytes += instance.RawValue;
            snapshot.Metrics["gpu.vram"] = bytes / Gb;
        }

        _processMemory ??= new PerformanceCounterCategory("GPU Process Memory");
        var perProcess = _processMemory.ReadCategory()["Dedicated Usage"];
        if (perProcess is null) return;
        foreach (InstanceData instance in perProcess.Values)
        {
            var match = ProcessInstance().Match(instance.InstanceName);
            if (!match.Success) continue;
            var pid = int.Parse(match.Groups[1].Value);
            context.VramBytesByPid[pid] = context.VramBytesByPid.GetValueOrDefault(pid) + instance.RawValue;
        }
    }

    public void Dispose()
    {
    }
}
