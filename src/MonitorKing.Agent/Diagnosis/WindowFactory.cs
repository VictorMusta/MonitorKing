using MonitorKing.Agent.Storage;

namespace MonitorKing.Agent.Diagnosis;

/// <summary>Construit les données d'une fenêtre de temps : en direct (mémoire) ou passée (SQLite).</summary>
public static class WindowFactory
{
    public static WindowData Live(int minutes, Database db, CollectorHost host, MachineInfo info)
    {
        var span = Math.Clamp(minutes, 1, 5);
        var (metrics, processes) = host.RecentWindow(span);
        var now = host.Latest?.Ts ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return new WindowData
        {
            From = now - span * 60_000L,
            To = now,
            Live = true,
            Metrics = metrics,
            Processes = processes,
            Events = db.Events(now - 7L * 24 * 3600_000, now, 1000),
            Hangs = db.Hangs(now - 24L * 3600_000, now),
            ActiveHangs = host.Latest?.Hung.ToList() ?? new List<HungWindow>(),
            Sensors = host.Latest?.Sensors.ToList() ?? new List<SensorReading>(),
            RamTotalGb = info.RamGb,
            Labels = host.Definitions.ToDictionary(d => d.Key, d => d.Label),
        };
    }

    public static WindowData History(long from, long to, Database db, CollectorHost host, MachineInfo info)
    {
        var stats = db.MetricStats(from, to);
        return new WindowData
        {
            From = from,
            To = to,
            Live = false,
            Metrics = stats,
            Processes = db.TopProcesses(from, to),
            Events = db.Events(from, to, 1000),
            Hangs = db.Hangs(from, to),
            Sensors = PeakTemperatures(stats, host.Definitions),
            RamTotalGb = info.RamGb,
            Labels = host.Definitions.ToDictionary(d => d.Key, d => d.Label),
        };
    }

    /// <summary>Pour une période passée : le pic de chaque capteur de température.</summary>
    public static List<SensorReading> PeakTemperatures(Dictionary<string, (double Avg, double Max, double Last)> stats, IEnumerable<MetricDef> definitions)
    {
        var list = new List<SensorReading>();
        foreach (var def in definitions.Where(d => d.Key.Contains("/temperature/", StringComparison.Ordinal)
            && !d.Label.Contains("Warning Temperature", StringComparison.OrdinalIgnoreCase)
            && !d.Label.Contains("Critical Temperature", StringComparison.OrdinalIgnoreCase)))
        {
            if (!stats.TryGetValue(def.Key, out var stat)) continue;
            var parts = def.Label.Split(" · ", 2);
            var segment = def.Key.Split('/', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1) ?? "";
            var type = segment.StartsWith("gpu", StringComparison.OrdinalIgnoreCase) ? "GpuAmd"
                : segment.Contains("cpu", StringComparison.OrdinalIgnoreCase) ? "Cpu"
                : segment is "nvme" or "hdd" or "ssd" or "ata" ? "Storage"
                : segment == "lpc" ? "Motherboard"
                : segment;
            list.Add(new SensorReading(parts[0], type, parts.Length > 1 ? parts[1] : def.Label, "Temperature", Math.Round(stat.Max, 1), def.Unit, def.Key));
        }

        return list;
    }
}
