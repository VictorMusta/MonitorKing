using MonitorKing.Core.Storage;

namespace MonitorKing.Core;

/// <summary>Fiche d'identité d'une machine, telle que l'agent la décrit (et l'envoie au serveur).</summary>
public sealed record MachineSummary(
    string Name,
    string Os,
    string Cpu,
    int LogicalCores,
    double RamGb,
    IReadOnlyList<string> Gpus,
    string Board,
    bool Administrator,
    string AgentVersion,
    long BootTime,
    int SampleIntervalMs,
    int SensorIntervalMs);

/// <summary>
/// Ce qu'il faut pour analyser une machine : sa fiche, ses métriques connues et sa base.
/// L'agent fournit sa propre machine ; le serveur en fournit une par PC inscrit.
/// </summary>
public interface IMachineContext
{
    MachineSummary Summary { get; }
    IReadOnlyCollection<MetricDef> Definitions { get; }
    Database Database { get; }
}
