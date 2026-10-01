using System.Text;
using MonitorKing.Core.Diagnosis;
using MonitorKing.Core.Storage;

namespace MonitorKing.Core.Reports;

/// <summary>
/// Rapport Markdown d'une période : de quoi comprendre ce qui s'est passé et conseiller,
/// sans accès à la machine. Pensé pour être collé dans une conversation avec Claude.
/// Il contient les pics détectés et, pour chacun, les applications qui s'activent pendant le pic
/// (corrélation) plutôt que celles qui consomment le plus en continu.
/// </summary>
public sealed class ReportBuilder
{
    private const long WindowMs = 10_000;
    private const long StartupNoiseMs = 90_000;

    private static readonly string[] MeasureOrder =
    {
        "cpu.total", "cpu.maxcore", "cpu.dpc", "mem.load", "mem.used", "mem.commitpct", "mem.hardfaults",
        "disk.active", "disk.latency", "disk.read", "disk.write",
        "gpu.total", "gpu.3d", "gpu.video", "gpu.compute", "gpu.vram",
        "net.down", "net.up", "wifi.signal", "wifi.rssi", "hang.count",
    };

    private static readonly Dictionary<string, string> SeverityLabels = new()
    {
        ["critical"] = "CRITIQUE",
        ["warning"] = "À SURVEILLER",
        ["info"] = "INFO",
        ["ok"] = "OK",
    };

    private readonly DiagnosisEngine _engine;

    public ReportBuilder(DiagnosisEngine engine) => _engine = engine;

    public string Build(IMachineContext machine, long from, long to)
    {
        var db = machine.Database;
        var defs = machine.Definitions.ToDictionary(d => d.Key);
        var window = WindowFactory.History(machine, from, to);
        var diagnosis = _engine.Analyze(window);
        var descriptions = window.Processes
            .Where(p => p.Description is not null)
            .ToDictionary(p => p.Name, p => p.Description!, StringComparer.OrdinalIgnoreCase);

        var md = new StringBuilder();
        Header(md, machine.Summary, db, from, to);
        Machine(md, machine.Summary, defs);
        Verdict(md, diagnosis);
        Spikes(md, db, from, to, defs, descriptions);
        Measures(md, window.Metrics, defs);
        Applications(md, window.Processes);
        Events(md, db, window, to);
        Hangs(md, window.Hangs);
        Interpretation(md, machine.Summary);
        return md.ToString();
    }

    private static void Header(StringBuilder md, MachineSummary machine, Database db, long from, long to)
    {
        var expected = Math.Max(1, (to - from) / WindowMs);
        var covered = db.Windows("cpu.total", from, to).Count;
        var offset = TimeZoneInfo.Local.GetUtcOffset(Fmt.Local(to));
        md.AppendLine($"# Rapport MonitorKing — {machine.Name}");
        md.AppendLine();
        md.AppendLine($"- **Période** : {Fmt.When(from)} → {Fmt.Time(to)} ({Fmt.Duration((to - from) / 1000.0)}), heure locale (UTC{(offset >= TimeSpan.Zero ? "+" : "-")}{offset:hh\\:mm})");
        md.AppendLine($"- **Généré le** : {Fmt.When(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())}");
        md.AppendLine($"- **Couverture** : {covered} fenêtres de 10 s enregistrées sur {expected} ({Math.Min(100, covered * 100 / expected)} %) ; le reste du temps, l'agent ne tournait pas ou n'avait pas encore envoyé ses données.");
        md.AppendLine($"- **Mesures** : toutes les {machine.SampleIntervalMs / 1000.0:0.#} s, agrégées par fenêtres de 10 s (moyenne + pic) ; capteurs matériels toutes les {machine.SensorIntervalMs / 1000.0:0.#} s.");
        md.AppendLine();
    }

    private static void Machine(StringBuilder md, MachineSummary machine, Dictionary<string, MetricDef> defs)
    {
        var disks = defs.Keys
            .Where(k => k.StartsWith("disk.", StringComparison.Ordinal) && k.EndsWith(".active", StringComparison.Ordinal) && k != "disk.active")
            .OrderBy(k => k)
            .Select(k => defs[k].Label.Replace(" · activité", ""));
        md.AppendLine("## Machine");
        md.AppendLine();
        md.AppendLine($"- **Système** : {machine.Os}");
        md.AppendLine($"- **Processeur** : {machine.Cpu} ({machine.LogicalCores} threads)");
        md.AppendLine($"- **Mémoire** : {Fmt.Value(machine.RamGb, "Go")}");
        md.AppendLine($"- **Carte graphique** : {string.Join(", ", machine.Gpus)}");
        md.AppendLine($"- **Carte mère** : {machine.Board}");
        md.AppendLine($"- **Disques** : {string.Join(" ; ", disks)}");
        md.AppendLine($"- **Allumé depuis** : {Fmt.Duration((DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - machine.BootTime) / 1000.0)}");
        md.AppendLine($"- **Agent** : MonitorKing v{machine.AgentVersion}, droits administrateur : {(machine.Administrator ? "oui" : "non")}");
        md.AppendLine();
    }

    private static void Verdict(StringBuilder md, DiagnosisResult diagnosis)
    {
        md.AppendLine("## Diagnostic automatique");
        md.AppendLine();
        md.AppendLine($"**{diagnosis.Verdict}**");
        md.AppendLine();
        foreach (var finding in diagnosis.Findings)
            md.AppendLine($"- [{SeverityLabels.GetValueOrDefault(finding.Severity, finding.Severity)}] **{finding.Title}** — {finding.Detail}");
        md.AppendLine();
    }

    private static void Spikes(StringBuilder md, Database db, long from, long to, Dictionary<string, MetricDef> defs, Dictionary<string, string> descriptions)
    {
        var targets = new List<(string Key, double Threshold, Func<(double Cpu, double Io, double Gpu), double> Value, Func<double, string> Format, double Floor, double Minimum)>
        {
            ("cpu.total", 80, v => v.Cpu, Fmt.Pct, 0.2, 1),
        };
        targets.AddRange(defs.Keys
            .Where(k => k.StartsWith("disk.", StringComparison.Ordinal) && k.EndsWith(".active", StringComparison.Ordinal) && k != "disk.active")
            .OrderBy(k => k)
            .Select(k => (k, 80.0, (Func<(double Cpu, double Io, double Gpu), double>)(v => v.Io), (Func<double, string>)Fmt.Rate, 10.0 * 1024, 50.0 * 1024)));
        targets.Add(("gpu.total", 90, v => v.Gpu, Fmt.Pct, 0.5, 2));

        // La minute qui suit un démarrage de l'agent (import des journaux, inventaire matériel) est écartée :
        // ce sont des pics que l'agent provoque lui-même.
        var starts = db.AgentStarts(from - StartupNoiseMs, to);
        bool AfterStart(long ts) => starts.Any(s => ts >= s && ts <= s + StartupNoiseMs);
        var processWindows = db.ProcessWindows(from, to).Where(p => !AfterStart(p.Ts)).ToList();
        var sections = new StringBuilder();
        var found = 0;
        if (starts.Count > 0)
        {
            sections.AppendLine($"_Démarrages de l'agent pendant la période : {string.Join(", ", starts.Where(s => s >= from).Select(Fmt.Time))}. La minute et demie qui suit chaque démarrage est exclue de l'analyse des pics (l'agent y provoque lui-même de l'activité)._");
            sections.AppendLine();
        }

        foreach (var target in targets)
        {
            var windows = db.Windows(target.Key, from, to).Where(w => !AfterStart(w.Ts)).ToList();
            var spikes = windows.Where(w => w.Max >= target.Threshold).Select(w => w.Ts).ToHashSet();
            if (spikes.Count == 0) continue;
            found++;

            var label = defs.TryGetValue(target.Key, out var def) ? def.Label : target.Key;
            var bursts = Bursts(windows, target.Threshold);
            sections.AppendLine($"### {label} ≥ {target.Threshold:0} %");
            sections.AppendLine();
            sections.AppendLine($"- {Rhythm(bursts, windows.Count)}");

            if (target.Key.EndsWith(".active", StringComparison.Ordinal) && target.Key.StartsWith("disk.", StringComparison.Ordinal))
            {
                var latency = db.Windows(target.Key.Replace(".active", ".latency"), from, to).ToDictionary(w => w.Ts, w => w.Avg);
                var during = spikes.Where(latency.ContainsKey).Select(ts => latency[ts]).ToList();
                var calmLatency = windows.Where(w => w.Max < target.Threshold * 0.6 && latency.ContainsKey(w.Ts)).Select(w => latency[w.Ts]).ToList();
                if (during.Count > 0)
                {
                    sections.AppendLine($"- Temps de réponse du disque pendant les pics : {Fmt.Value(during.Average(), "ms")} en moyenne" +
                        (calmLatency.Count > 0 ? $" (au calme : {Fmt.Value(calmLatency.Average(), "ms")})." : "."));
                }
            }

            var calm = windows.Where(w => w.Max < target.Threshold * 0.6).Select(w => w.Ts).ToHashSet();
            var risers = processWindows
                .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    var duringValue = g.Where(p => spikes.Contains(p.Ts)).Sum(p => target.Value((p.Cpu, p.Io, p.Gpu))) / spikes.Count;
                    var calmValue = calm.Count == 0 ? 0 : g.Where(p => calm.Contains(p.Ts)).Sum(p => target.Value((p.Cpu, p.Io, p.Gpu))) / calm.Count;
                    return (Name: g.Key, During: duringValue, Calm: calmValue, Rise: duringValue / Math.Max(calmValue, target.Floor));
                })
                .Where(r => r.During >= target.Minimum && r.During > r.Calm)
                .OrderByDescending(r => r.During - r.Calm)
                .Take(6)
                .ToList();

            if (risers.Count == 0)
            {
                sections.AppendLine("- Aucune application ne s'active nettement pendant ces pics (cause possible : noyau, pilote, ou activité trop brève pour les fenêtres de 10 s).");
            }
            else
            {
                sections.AppendLine(calm.Count == 0
                    ? "- Pas de moment calme sur la période pour comparer : applications les plus actives pendant les pics :"
                    : "- Applications qui s'activent pendant les pics (moyenne par fenêtre de 10 s) :");
                sections.AppendLine();
                sections.AppendLine("| Application | Pendant les pics | Au calme | Hausse |");
                sections.AppendLine("|---|---|---|---|");
                foreach (var r in risers)
                {
                    var name = descriptions.TryGetValue(r.Name, out var d) ? $"{d} ({r.Name})" : r.Name;
                    sections.AppendLine($"| {Fmt.Cell(name)} | {target.Format(r.During)} | {target.Format(r.Calm)} | {(calm.Count == 0 ? "–" : Fmt.Ratio(r.Rise))} |");
                }
            }

            sections.AppendLine();
        }

        md.AppendLine("## Pics détectés");
        md.AppendLine();
        md.Append(sections);
        if (found == 0) md.Append("Aucun pic marqué (processeur ≥ 80 %, disque ≥ 80 %, GPU ≥ 90 %) sur la période.\n\n");
    }

    private sealed record Burst(long Start, long End, double Peak);

    private static List<Burst> Bursts(List<(long Ts, double Avg, double Max)> windows, double threshold)
    {
        var bursts = new List<Burst>();
        Burst? current = null;
        foreach (var w in windows)
        {
            if (w.Max < threshold) continue;
            if (current is not null && w.Ts - current.End <= WindowMs + 5_000)
            {
                current = current with { End = w.Ts, Peak = Math.Max(current.Peak, w.Max) };
            }
            else
            {
                if (current is not null) bursts.Add(current);
                current = new Burst(w.Ts, w.Ts, w.Max);
            }
        }

        if (current is not null) bursts.Add(current);
        return bursts;
    }

    /// <summary>Décrit le rythme des pics : nombre, régularité, durée typique.</summary>
    private static string Rhythm(List<Burst> bursts, int windowCount)
    {
        var durations = bursts.Select(b => (b.End - b.Start + WindowMs) / 1000.0).OrderBy(d => d).ToList();
        var typical = durations[durations.Count / 2];
        var text = $"{bursts.Count} pic{(bursts.Count > 1 ? "s" : "")} ({windowCount} fenêtres de 10 s analysées), durée typique ≤ {typical:0} s, maximum {bursts.Max(b => b.Peak):0} %";
        if (bursts.Count < 3) return text + ".";

        var gaps = bursts.Zip(bursts.Skip(1), (a, b) => (b.Start - a.Start) / 1000.0).OrderBy(g => g).ToList();
        var median = gaps[gaps.Count / 2];
        var regular = gaps.Count(g => Math.Abs(g - median) <= Math.Max(10, median * 0.3)) >= gaps.Count * 0.6;
        return text + (regular
            ? $". **Pics réguliers : environ toutes les {Fmt.Duration(median)}.**"
            : $". Pics irréguliers (écart médian {Fmt.Duration(median)}).");
    }

    private static void Measures(StringBuilder md, Dictionary<string, (double Avg, double Max, double Last)> stats, Dictionary<string, MetricDef> defs)
    {
        var keys = MeasureOrder.Where(stats.ContainsKey).ToList();
        keys.AddRange(stats.Keys.Where(k => k.StartsWith("disk.", StringComparison.Ordinal) && !MeasureOrder.Contains(k)).OrderBy(k => k));
        keys.AddRange(stats.Keys.Where(k => k.Contains("/temperature/", StringComparison.Ordinal)).OrderBy(k => k));
        // Les métriques que l'agent ne produit plus (ex. seuils d'alerte d'un SSD pris autrefois pour des mesures) sont ignorées.
        keys = keys.Where(defs.ContainsKey).ToList();

        md.AppendLine("## Mesures sur la période");
        md.AppendLine();
        if (keys.Count == 0)
        {
            md.AppendLine("Aucune mesure enregistrée sur cette période.");
            md.AppendLine();
            return;
        }

        md.AppendLine("| Mesure | Moyenne | Pic |");
        md.AppendLine("|---|---|---|");
        foreach (var key in keys)
        {
            var def = defs.GetValueOrDefault(key);
            var (avg, max, _) = stats[key];
            md.AppendLine($"| {Fmt.Cell(def?.Label ?? key)} | {Fmt.Value(avg, def?.Unit ?? "")} | {Fmt.Value(max, def?.Unit ?? "")} |");
        }

        md.AppendLine();
    }

    private static void Applications(StringBuilder md, List<ProcRow> processes)
    {
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Top(Func<ProcRow, double> by, int count)
        {
            foreach (var p in processes.Where(p => by(p) > 0).OrderByDescending(by).Take(count)) keep.Add(p.Name);
        }

        Top(p => p.Cpu, 8);
        Top(p => p.RamMb, 8);
        Top(p => p.IoBps, 8);
        Top(p => p.Gpu, 5);
        Top(p => p.HardFaultsPerSec, 3);

        md.AppendLine("## Applications sur la période");
        md.AppendLine();
        md.AppendLine("Moyennes sur la période (mémoire et VRAM : pic). Les svchost sont séparés par service ; les processus WebView2 sont rattachés à l'application qui les lance.");
        md.AppendLine();
        md.AppendLine("| Application | Proc. | Processeur | Mémoire | Lecture | Écriture | Défauts durs | GPU | VRAM |");
        md.AppendLine("|---|---|---|---|---|---|---|---|---|");
        foreach (var p in processes.Where(p => keep.Contains(p.Name)).OrderByDescending(p => p.Cpu).ThenByDescending(p => p.RamMb))
        {
            var name = p.Description is { Length: > 0 } d && !d.Equals(p.Name, StringComparison.OrdinalIgnoreCase) ? $"{d} ({p.Name})" : p.Name;
            if (p.Via is not null) name += $" via {p.Via}";
            md.AppendLine($"| {Fmt.Cell(name)} | {p.Count} | {Fmt.Pct(p.Cpu)} | {Fmt.Mb(p.RamMb)} | {Fmt.Rate(p.IoReadBps)} | {Fmt.Rate(p.IoWriteBps)} | {Fmt.Value(p.HardFaultsPerSec, "/s")} | {(p.Gpu > 0 ? Fmt.Pct(p.Gpu) : "–")} | {(p.VramMb > 0 ? Fmt.Mb(p.VramMb) : "–")} |");
        }

        md.AppendLine();
    }

    private static void Events(StringBuilder md, Database db, WindowData window, long to)
    {
        md.AppendLine("## Événements Windows pendant la période");
        md.AppendLine();
        if (window.Events.Count == 0)
        {
            md.AppendLine("Aucun.");
        }
        else
        {
            foreach (var e in window.Events.OrderBy(e => e.Ts).Take(60))
            {
                var message = e.Message is null ? "" : " — " + Fmt.Cell(e.Message.Length > 400 ? e.Message[..400] + "…" : e.Message);
                md.AppendLine($"- {Fmt.Time(e.Ts)} · **{Fmt.Cell(e.Title)}** ({e.Provider}, événement {e.EventId}){message}");
            }
        }

        md.AppendLine();
        md.AppendLine("## Contexte : signalements des 7 jours précédant la fin de la période");
        md.AppendLine();
        var summary = db.EventSummary(to - 7L * 24 * 3600_000, to);
        if (summary.Count == 0)
        {
            md.AppendLine("Aucun.");
        }
        else
        {
            md.AppendLine("| Signalement | Nombre | Dernier |");
            md.AppendLine("|---|---|---|");
            foreach (var (title, _, count, last) in summary)
                md.AppendLine($"| {Fmt.Cell(title)} | {count} | {Fmt.When(last)} |");
        }

        md.AppendLine();
    }

    private static void Hangs(StringBuilder md, List<HangItem> hangs)
    {
        md.AppendLine("## Fenêtres gelées (« Ne répond pas ») pendant la période");
        md.AppendLine();
        if (hangs.Count == 0)
        {
            md.AppendLine("Aucune.");
        }
        else
        {
            foreach (var h in hangs.OrderBy(h => h.Start))
            {
                var duration = h.End is { } end ? Fmt.Duration((end - h.Start) / 1000.0) : "durée inconnue";
                md.AppendLine($"- {Fmt.Time(h.Start)} · **{Fmt.Cell(h.Process)}** pendant {duration} — fenêtre « {Fmt.Cell(h.Title)} »");
            }
        }

        md.AppendLine();
    }

    private static void Interpretation(StringBuilder md, MachineSummary machine)
    {
        md.AppendLine("## Pour interpréter");
        md.AppendLine();
        md.AppendLine("- **Lecture/écriture par application** : compteurs d'E/S Windows par processus, réseau compris, et sans indication du disque visé : une application qui s'active pendant un pic du disque D: peut très bien écrire sur C:.");
        md.AppendLine("- **Activité d'un disque** : part du temps avec au moins une opération en cours. Sur un SSD, 100 % ne veut pas dire saturé : regarder le temps de réponse (SSD < 5 ms, disque dur < 20 ms).");
        md.AppendLine("- **Hausse** : moyenne de l'application pendant les pics divisée par sa moyenne au calme ; c'est elle qui désigne le coupable d'un pic, pas le volume total.");
        md.AppendLine("- **Défauts durs** : pages relues sur le disque ; beaucoup de défauts durs avec une RAM pleine = Windows pagine.");
        if (!machine.Administrator)
            md.AppendLine("- L'agent tournait **sans droits administrateur** : pas de températures CPU/carte mère, pas de journal des temps de démarrage.");
        md.AppendLine();
        md.AppendLine("## Ce que j'ai remarqué");
        md.AppendLine();
        md.AppendLine("_(À compléter : le symptôme, le moment, ce que tu faisais à ce moment-là.)_");
    }
}
