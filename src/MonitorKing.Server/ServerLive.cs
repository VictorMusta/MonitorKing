using MonitorKing.Core;
using MonitorKing.Core.Diagnosis;

namespace MonitorKing.Server;

/// <summary>
/// « Direct » côté serveur : la dernière fenêtre de 10 s reçue d'un PC, mise en forme comme l'instantané
/// de l'agent pour que le même dashboard fonctionne à distance (avec 10 à 30 s de décalage).
/// </summary>
public static class ServerLive
{
    public static object? Snapshot(ServerMachine machine)
    {
        var db = machine.Database;
        if (db.LastSampleBefore(long.MaxValue) is not { } last) return null;

        var metrics = new Dictionary<string, double>();
        foreach (var sample in db.SamplesBetween(last - 1, last)) metrics[sample.Key] = sample.Avg;

        var processes = db.ProcessRowsBetween(last - 1, last)
            .Select(p => p.Row)
            .OrderByDescending(r => r.Cpu)
            .Select(r => new
            {
                r.Name, r.Description, r.Via, r.Count, r.Cpu, r.RamMb, r.CommitMb, r.IoReadBps, r.IoWriteBps,
                r.HardFaultsPerSec, r.Gpu, r.VramMb, r.NetSendBps, r.NetRecvBps, Pids = Array.Empty<int>(),
            })
            .ToList();

        WifiInfo? wifi = null;
        if (metrics.TryGetValue("wifi.signal", out var quality))
        {
            wifi = new WifiInfo("Wi-Fi", "Connecté", null, (int)Math.Round(quality),
                metrics.TryGetValue("wifi.rssi", out var rssi) ? (int)Math.Round(rssi) : null,
                metrics.TryGetValue("wifi.rx", out var rx) ? rx : null,
                metrics.TryGetValue("wifi.tx", out var tx) ? tx : null,
                null, null);
        }

        return new
        {
            Ts = last,
            Metrics = metrics,
            Processes = processes,
            Hung = ActiveHangs(machine, last),
            Sensors = Sensors(machine.Definitions, metrics),
            Wifi = wifi,
        };
    }

    /// <summary>Fenêtre « en ce moment » pour le diagnostic : les dernières minutes reçues, plus le contexte (7 jours d'événements).</summary>
    public static WindowData Window(ServerMachine machine, int minutes, long to)
    {
        var db = machine.Database;
        var from = to - minutes * 60_000L;
        var stats = db.MetricStats(from, to);
        return new WindowData
        {
            From = from,
            To = to,
            Live = true,
            Metrics = stats,
            Processes = db.TopProcesses(from, to),
            Events = db.Events(to - 7L * 24 * 3600_000, to, 1000),
            Hangs = db.Hangs(to - 24L * 3600_000, to),
            ActiveHangs = ActiveHangs(machine, to),
            Sensors = Sensors(machine.Definitions, stats.ToDictionary(s => s.Key, s => s.Value.Last)),
            RamTotalGb = machine.Summary.RamGb,
            Labels = machine.Definitions.ToDictionary(d => d.Key, d => d.Label),
        };
    }

    /// <summary>
    /// Gels encore en cours. L'absence de fin ne suffit pas : si l'agent s'arrête en plein gel (extinction du PC,
    /// mise à jour), la fin n'arrive jamais. Tant qu'une fenêtre est gelée, le PC en compte au moins une à chaque
    /// mesure ; dès qu'une mesure reçue depuis le début du gel n'en compte aucune, ce gel est terminé.
    /// </summary>
    private static List<HungWindow> ActiveHangs(ServerMachine machine, long at) =>
        machine.Database.Hangs(at - 10 * 60_000, at)
            .Where(h => h.End is null && !machine.Database.PeakBelow("hang.count", h.Start, at, 0.5))
            .Select(h => new HungWindow(h.Pid, h.Process, h.Title, h.Start))
            .ToList();

    /// <summary>Recompose la liste des capteurs à partir des métriques historisées (clés « hw/… »).</summary>
    public static List<SensorReading> Sensors(IEnumerable<MetricDef> definitions, Dictionary<string, double> values)
    {
        var list = new List<SensorReading>();
        foreach (var def in definitions)
        {
            if (!def.Key.StartsWith("hw/", StringComparison.Ordinal) || !values.TryGetValue(def.Key, out var value)) continue;
            var segments = def.Key.Split('/');
            var hardware = segments.ElementAtOrDefault(1) ?? "";
            var kind = segments.ElementAtOrDefault(3) ?? "";
            var parts = def.Label.Split(" · ", 2);
            var hardwareType = hardware.Contains("cpu", StringComparison.OrdinalIgnoreCase) ? "Cpu"
                : hardware.StartsWith("gpu", StringComparison.OrdinalIgnoreCase)
                    ? "Gpu" + (hardware.Contains("nvidia", StringComparison.OrdinalIgnoreCase) ? "Nvidia" : hardware.Contains("intel", StringComparison.OrdinalIgnoreCase) ? "Intel" : "Amd")
                : hardware == "lpc" ? "Motherboard"
                : hardware is "nvme" or "hdd" or "ssd" or "ata" ? "Storage"
                : hardware;
            var type = kind switch
            {
                "temperature" => "Temperature",
                "fan" => "Fan",
                "control" => "Control",
                "load" => "Load",
                "power" => "Power",
                "clock" => "Clock",
                "smalldata" => "SmallData",
                "data" => "Data",
                "level" => "Level",
                "voltage" => "Voltage",
                _ => kind,
            };
            list.Add(new SensorReading(parts[0], hardwareType, parts.Length > 1 ? parts[1] : def.Label, type, Math.Round(value, 2), def.Unit, def.Key));
        }

        return list;
    }
}
