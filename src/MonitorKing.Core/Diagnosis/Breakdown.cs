using MonitorKing.Core.Storage;

namespace MonitorKing.Core.Diagnosis;

/// <summary>Une application de la répartition : sa consommation moyenne sur chaque tranche de temps.</summary>
public sealed record BreakdownApp(string Name, string? Description, string? Via, double Average, double[] Values);

/// <summary>
/// Répartition d'une ressource entre les applications au fil du temps, pour un graphique en aires empilées :
/// les plus gourmandes une par une, les suivantes regroupées (<see cref="Others"/>), et le reste
/// (<see cref="Rest"/> : Windows, pilotes, petites applications) quand le PC mesure un total pour cette ressource.
/// </summary>
public sealed record BreakdownResult(
    string Resource, string Unit, long From, long To, long Step, long[] Ts, List<BreakdownApp> Apps, double[] Others, double[]? Rest);

public static class Breakdown
{
    // Ressource → unité, et métrique totale du PC convertie dans la même unité (les totaux mémoire sont en Go).
    // Pas de total pour le GPU (moteur le plus chargé, pas une somme) ni pour les E/S (réseau compris par application).
    private static readonly Dictionary<string, (string Unit, string? TotalKey, double TotalFactor)> Resources = new()
    {
        ["cpu"] = ("%", "cpu.total", 1),
        ["ram"] = ("Mo", "mem.used", 1024),
        ["gpu"] = ("%", null, 1),
        ["vram"] = ("Mo", "gpu.vram", 1024),
        ["io"] = ("o/s", null, 1),
    };

    public static bool IsKnown(string resource) => Resources.ContainsKey(resource);

    public static BreakdownResult Build(Database db, string resource, long from, long to, int maxPoints, int top = 8)
    {
        var (unit, totalKey, factor) = Resources[resource];
        var step = Database.BucketStep(from, to, maxPoints);
        var (windows, rows) = db.ProcessBuckets(resource, from, to, step);
        var buckets = windows.Keys.Order().ToArray();
        var index = buckets.Select((b, i) => (b, i)).ToDictionary(x => x.b, x => x.i);

        // Moyenne par tranche : une application absente d'une fenêtre (hors des plus gourmandes) y compte pour 0.
        var apps = new Dictionary<string, (string? Description, string? Via, double[] Values)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (bucket, name, description, via, sum) in rows)
        {
            if (!apps.TryGetValue(name, out var app)) apps[name] = app = (description, via, new double[buckets.Length]);
            else if (app.Description is null && description is not null) apps[name] = app = (description, app.Via ?? via, app.Values);
            app.Values[index[bucket]] = sum / windows[bucket];
        }

        var ranked = apps
            .Select(a => (a.Key, a.Value.Description, a.Value.Via, a.Value.Values, Average: buckets.Length == 0 ? 0 : a.Value.Values.Average()))
            .Where(a => a.Average > 0)
            .OrderByDescending(a => a.Average)
            .ToList();
        var shown = ranked.Take(top).ToList();

        var others = new double[buckets.Length];
        foreach (var app in ranked.Skip(top))
            for (var i = 0; i < buckets.Length; i++) others[i] += app.Values[i];

        double[]? rest = null;
        if (totalKey is not null)
        {
            var totals = db.BucketAverages(totalKey, from, to, step);
            rest = new double[buckets.Length];
            for (var i = 0; i < buckets.Length; i++)
            {
                if (!totals.TryGetValue(buckets[i], out var total)) continue;
                var recorded = ranked.Sum(a => a.Values[i]);
                rest[i] = Math.Max(0, total * factor - recorded);
            }
        }

        return new BreakdownResult(
            resource,
            unit,
            from,
            to,
            step,
            buckets.Select(b => b + step / 2).ToArray(),
            shown.Select(a => new BreakdownApp(a.Key, a.Description, a.Via, Math.Round(a.Average, 3), Round(a.Values))).ToList(),
            Round(others),
            rest is null ? null : Round(rest));
    }

    private static double[] Round(double[] values) => values.Select(v => Math.Round(v, 3)).ToArray();
}
