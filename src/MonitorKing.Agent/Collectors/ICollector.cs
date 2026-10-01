namespace MonitorKing.Agent.Collectors;

/// <summary>
/// Un collecteur lit une famille de données à chaque tick. Ajouter une métrique = ajouter un collecteur
/// (ou étendre un existant) et déclarer ses <see cref="MetricDef"/> : le dashboard les découvre tout seul.
/// </summary>
public interface ICollector : IDisposable
{
    string Id { get; }
    string Label { get; }

    /// <summary>Métriques connues à ce stade. La liste peut grandir (capteurs découverts à l'exécution).</summary>
    IEnumerable<MetricDef> Metrics { get; }

    /// <summary>Explication affichée quand une partie des données est indisponible.</summary>
    string? Hint { get; }

    /// <summary>Information technique pour la fenêtre « État des collecteurs » (coût, rythme…).</summary>
    string? Detail => null;

    void Collect(Snapshot snapshot, CollectContext context);
}

/// <summary>Données partagées entre collecteurs pendant un tick (ex. GPU par processus).</summary>
public sealed class CollectContext
{
    public CollectContext(double elapsedSeconds) => ElapsedSeconds = elapsedSeconds;

    public double ElapsedSeconds { get; }
    public Dictionary<int, GpuUsage> GpuByPid { get; } = new();
    public Dictionary<int, double> VramBytesByPid { get; } = new();
    /// <summary>Débit réseau par processus pendant ce tick (o/s), quand l'agent peut écouter le réseau (administrateur).</summary>
    public Dictionary<int, (double Send, double Recv)> NetByPid { get; } = new();
}

public readonly record struct GpuUsage(double Utilization, string Engine);
