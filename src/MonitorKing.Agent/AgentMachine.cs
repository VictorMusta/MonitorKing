namespace MonitorKing.Agent;

/// <summary>La machine locale vue par le moteur commun (diagnostic, rapports).</summary>
public sealed class AgentMachine : IMachineContext
{
    private readonly MachineInfo _info;
    private readonly CollectorHost _host;
    private readonly AgentOptions _options;

    public AgentMachine(MachineInfo info, CollectorHost host, Database database, AgentOptions options)
    {
        _info = info;
        _host = host;
        _options = options;
        Database = database;
    }

    public MachineSummary Summary => new(
        _info.MachineName, _info.Os, _info.Cpu, _info.LogicalCores, _info.RamGb, _info.Gpus, _info.Board,
        _info.Administrator, _info.AgentVersion, _info.BootTime, _options.SampleIntervalMs, _options.SensorIntervalMs);

    public IReadOnlyCollection<MetricDef> Definitions => _host.Definitions.ToList();

    public Database Database { get; }

    /// <summary>Fenêtre « en direct » : les dernières minutes en mémoire (2 s de résolution), pour le diagnostic.</summary>
    public WindowData Live(int minutes)
    {
        var span = Math.Clamp(minutes, 1, 5);
        var (metrics, processes) = _host.RecentWindow(span);
        var now = _host.Latest?.Ts ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return new WindowData
        {
            From = now - span * 60_000L,
            To = now,
            Live = true,
            Metrics = metrics,
            Processes = processes,
            EventCounts = Database.EventCounts(now - 7L * 24 * 3600_000, now),
            Hangs = Database.Hangs(now - 24L * 3600_000, now),
            ActiveHangs = _host.Latest?.Hung.ToList() ?? new List<HungWindow>(),
            Sensors = _host.Latest?.Sensors.ToList() ?? new List<SensorReading>(),
            RamTotalGb = _info.RamGb,
            Labels = _host.Definitions.ToDictionary(d => d.Key, d => d.Label),
        };
    }

    /// <summary>Emplacement de la base locale : %LOCALAPPDATA%\MonitorKing\monitorking.db par défaut.</summary>
    public static string DatabasePath(AgentOptions options)
    {
        var directory = string.IsNullOrWhiteSpace(options.DataDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonitorKing")
            : Environment.ExpandEnvironmentVariables(options.DataDirectory);
        return Path.Combine(directory, "monitorking.db");
    }
}
