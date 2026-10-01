namespace MonitorKing.Core;

/// <summary>Définition d'une métrique. Tout collecteur peut en déclarer, y compris à l'exécution.</summary>
public sealed record MetricDef(string Key, string Label, string Unit, string Group, double? Max = null);

public sealed class ProcessGroup
{
    public required string Name { get; init; }
    public string? Description { get; set; }
    /// <summary>Hôte par lequel la consommation est rattachée (ex. « WebView2 »).</summary>
    public string? Via { get; set; }
    public int Count { get; set; }
    public double Cpu { get; set; }
    public double RamMb { get; set; }
    public double CommitMb { get; set; }
    public double IoReadBps { get; set; }
    public double IoWriteBps { get; set; }
    public double HardFaultsPerSec { get; set; }
    public double Gpu { get; set; }
    public string? GpuEngine { get; set; }
    public double VramMb { get; set; }
    public List<int> Pids { get; } = new();
}

public sealed record HungWindow(int Pid, string Process, string Title, long Since);

/// <param name="Limit">Seuil d'alerte annoncé par le composant lui-même (ex. « Warning Temperature » d'un SSD NVMe).</param>
/// <param name="Chip">Puce qui fournit la mesure quand elle diffère du composant affiché (ex. « Nuvoton NCT6797D » pour la carte mère).</param>
public sealed record SensorReading(string Hardware, string HardwareType, string Name, string Type, double Value, string Unit, string Key, double? Limit = null, string? Chip = null);

public sealed record WifiInfo(
    string Adapter,
    string State,
    string? Ssid,
    int? SignalQuality,
    int? Rssi,
    double? RxMbps,
    double? TxMbps,
    int? Channel,
    string? Band);

public sealed class Snapshot
{
    public long Ts { get; init; }
    public Dictionary<string, double> Metrics { get; } = new();
    public List<ProcessGroup> Processes { get; set; } = new();
    public List<HungWindow> Hung { get; } = new();
    public List<SensorReading> Sensors { get; } = new();
    public WifiInfo? Wifi { get; set; }
}

public sealed record CollectorStatus(string Id, string Label, bool Ok, string? Error, string? Hint, double LastMs, string? Detail = null);

/// <summary>Consommation d'une application agrégée sur une fenêtre de temps.</summary>
public sealed class ProcRow
{
    public required string Name { get; init; }
    public string? Description { get; set; }
    public string? Via { get; set; }
    public double Cpu { get; set; }
    public double RamMb { get; set; }
    public double CommitMb { get; set; }
    public double IoReadBps { get; set; }
    public double IoWriteBps { get; set; }
    public double HardFaultsPerSec { get; set; }
    public double Gpu { get; set; }
    public double VramMb { get; set; }
    public int Count { get; set; }
    public double IoBps => IoReadBps + IoWriteBps;
}

public sealed class EventItem
{
    public long Ts { get; init; }
    public required string Log { get; init; }
    public required string Provider { get; init; }
    public int EventId { get; init; }
    public int Level { get; init; }
    public required string Kind { get; init; }
    public required string Title { get; init; }
    public string? Message { get; init; }
    public long RecordId { get; init; }
}

public sealed record HangItem(long Id, long Start, long? End, int Pid, string Process, string Title);
