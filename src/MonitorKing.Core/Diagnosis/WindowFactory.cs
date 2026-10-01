namespace MonitorKing.Core.Diagnosis;

/// <summary>Construit les données d'une période passée, lues dans la base d'une machine.</summary>
public static class WindowFactory
{
    public static WindowData History(IMachineContext machine, long from, long to)
    {
        var db = machine.Database;
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
            Sensors = PeakTemperatures(stats, machine.Definitions),
            RamTotalGb = machine.Summary.RamGb,
            Labels = machine.Definitions.ToDictionary(d => d.Key, d => d.Label),
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
