using System.Globalization;

namespace MonitorKing.Core.Diagnosis;

public sealed record Finding(string Severity, string Resource, string Title, string Detail, string? Culprit = null);

public sealed class DiagnosisResult
{
    public required string Verdict { get; init; }
    public required string Severity { get; init; }
    public required long From { get; init; }
    public required long To { get; init; }
    public required bool Live { get; init; }
    public required List<Finding> Findings { get; init; }
    public required Dictionary<string, List<ProcRow>> Culprits { get; init; }
}

/// <summary>Données d'une fenêtre de temps, en direct (mémoire) ou passée (SQLite).</summary>
public sealed class WindowData
{
    public required long From { get; init; }
    public required long To { get; init; }
    public required bool Live { get; init; }
    public required Dictionary<string, (double Avg, double Max, double Last)> Metrics { get; init; }
    public required List<ProcRow> Processes { get; init; }
    /// <summary>
    /// Ce que Windows a signalé, compté par nature, titre et périphérique visé :
    /// les 7 derniers jours en direct, la période elle-même sinon.
    /// </summary>
    public required List<EventCount> EventCounts { get; init; }
    /// <summary>Les événements de la période un par un, pour le rapport ; le diagnostic ne lit que les totaux.</summary>
    public List<EventItem> Events { get; init; } = new();
    public required List<HangItem> Hangs { get; init; }
    public List<HungWindow> ActiveHangs { get; init; } = new();
    public List<SensorReading> Sensors { get; init; } = new();
    public double RamTotalGb { get; init; }
    /// <summary>Libellés des métriques (pour nommer le disque concerné, par exemple).</summary>
    public Dictionary<string, string> Labels { get; init; } = new();
}

/// <summary>
/// Répond à « pourquoi ça rame ? » : quelle ressource sature, quelle application en est responsable,
/// et ce que Windows a signalé à côté (plantages, gels, erreurs disque, bridage thermique…).
/// Règles simples et explicables : chaque conclusion cite les chiffres qui la justifient.
/// </summary>
public sealed class DiagnosisEngine
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");
    private static readonly string[] SeverityOrder = { "critical", "warning", "info", "ok" };
    private static readonly string[] ResourceOrder = { "hang", "memory", "cpu", "disk", "gpu", "thermal", "network", "crash", "system", "hardware" };

    public DiagnosisResult Analyze(WindowData w)
    {
        var findings = new List<Finding>();
        Memory(w, findings);
        Cpu(w, findings);
        Disk(w, findings);
        Gpu(w, findings);
        Thermal(w, findings);
        Hangs(w, findings);
        Events(w, findings);
        Wifi(w, findings);

        findings = findings
            .OrderBy(f => Array.IndexOf(SeverityOrder, f.Severity))
            .ThenBy(f => ResourceRank(f.Resource))
            .ToList();

        var worst = findings.FirstOrDefault()?.Severity ?? "ok";
        string verdict;
        if (worst == "critical")
        {
            var top = findings[0];
            verdict = top.Culprit is null ? top.Title : $"{top.Title} — principal responsable : {top.Culprit}";
        }
        else if (worst == "warning")
        {
            var count = findings.Count(f => f.Severity == "warning");
            verdict = w.Live
                ? $"Rien ne sature en ce moment, mais {count} point{(count > 1 ? "s" : "")} à surveiller."
                : $"Pas de saturation sur cette période, mais {count} point{(count > 1 ? "s" : "")} à surveiller.";
        }
        else
        {
            verdict = w.Live ? "Rien d'anormal : ton PC n'est saturé nulle part en ce moment." : "Rien d'anormal sur cette période.";
        }

        if (findings.Count == 0 || findings.All(f => f.Severity is "info"))
        {
            var busiest = w.Processes.OrderByDescending(p => p.Cpu).FirstOrDefault();
            findings.Add(new Finding("ok", "system", "Aucune ressource saturée",
                busiest is null
                    ? "Processeur, mémoire, disque et carte graphique ont de la marge."
                    : $"Processeur, mémoire, disque et carte graphique ont de la marge. Application la plus active : {Display(busiest)} ({Pct(busiest.Cpu)} du processeur)."));
        }

        return new DiagnosisResult
        {
            Verdict = verdict,
            Severity = worst,
            From = w.From,
            To = w.To,
            Live = w.Live,
            Findings = findings,
            Culprits = new Dictionary<string, List<ProcRow>>
            {
                ["cpu"] = w.Processes.Where(p => p.Cpu >= 0.5).OrderByDescending(p => p.Cpu).Take(5).ToList(),
                ["memory"] = w.Processes.OrderByDescending(p => p.RamMb).Take(5).ToList(),
                ["disk"] = w.Processes.Where(p => p.IoBps >= 100 * 1024).OrderByDescending(p => p.IoBps).Take(5).ToList(),
                ["gpu"] = w.Processes.Where(p => p.Gpu >= 1).OrderByDescending(p => p.Gpu).Take(5).ToList(),
            },
        };
    }

    private static void Memory(WindowData w, List<Finding> findings)
    {
        if (Stat(w, "mem.load") is not { } load) return;
        var hardFaults = Stat(w, "mem.hardfaults")?.Avg ?? 0;
        var paging = hardFaults >= 300;
        var top = w.Processes.OrderByDescending(p => p.RamMb).FirstOrDefault();

        if (load.Avg >= 90 || (load.Avg >= 80 && paging))
        {
            var detail = top is null ? "" : $"{Display(top)} occupe {Gb(top.RamMb)} de mémoire vive{Share(top.RamMb, w.RamTotalGb)}. ";
            detail += paging
                ? $"Windows doit relire {Num(hardFaults)} pages par seconde sur le disque pour compenser : c'est ce qui rend tout le PC lent."
                : "Si elle atteint 100 %, Windows commencera à utiliser le disque à la place de la RAM, ce qui ralentira tout.";
            findings.Add(new Finding("critical", "memory", $"La mémoire vive est saturée ({Pct(load.Avg)})", detail, top is null ? null : Display(top)));
        }
        else if (load.Avg >= 80)
        {
            findings.Add(new Finding("warning", "memory", $"La mémoire vive est presque pleine ({Pct(load.Avg)})",
                top is null ? "Encore un peu de marge." : $"Plus gros consommateur : {Display(top)} ({Gb(top.RamMb)}).",
                top is null ? null : Display(top)));
        }

        if (Stat(w, "mem.commitpct") is { Avg: >= 90 } commit)
        {
            var heaviest = w.Processes.OrderByDescending(p => p.CommitMb).FirstOrDefault();
            findings.Add(new Finding("warning", "memory", $"La mémoire virtuelle est presque épuisée ({Pct(commit.Avg)} de la limite)",
                "Au-delà de la limite, des applications refusent de s'ouvrir ou plantent." +
                (heaviest is null ? "" : $" Plus gros engagement : {Display(heaviest)} ({Gb(heaviest.CommitMb)})."),
                heaviest is null ? null : Display(heaviest)));
        }
    }

    private static void Cpu(WindowData w, List<Finding> findings)
    {
        if (Stat(w, "cpu.total") is not { } total) return;
        var top = w.Processes.OrderByDescending(p => p.Cpu).FirstOrDefault();

        if (total.Avg >= 85)
        {
            findings.Add(new Finding("critical", "cpu", $"Le processeur est saturé ({Pct(total.Avg)})",
                top is null ? "Toutes les applications doivent attendre leur tour." : $"{Display(top)} utilise à lui seul {Pct(top.Cpu)} du processeur.",
                top is null ? null : Display(top)));
        }
        else if (total.Avg >= 70)
        {
            findings.Add(new Finding("warning", "cpu", $"Le processeur est très sollicité ({Pct(total.Avg)})",
                top is null ? "" : $"Application la plus active : {Display(top)} ({Pct(top.Cpu)}).", top is null ? null : Display(top)));
        }
        else if (Stat(w, "cpu.maxcore") is { Avg: >= 90 } core && total.Avg < 50 && top is not null && top.Cpu >= 100.0 / Environment.ProcessorCount * 0.7)
        {
            findings.Add(new Finding("info", "cpu", "Un cœur du processeur tourne à fond",
                $"{Display(top)} semble limité par un seul cœur ({Pct(core.Avg)}) alors que le processeur global est à {Pct(total.Avg)}. " +
                "Fréquent dans les jeux et certaines applications : ça peut causer des saccades sans que le PC paraisse chargé.",
                Display(top)));
        }

        if (Stat(w, "cpu.dpc") is { Avg: >= 5 } dpc)
        {
            findings.Add(new Finding("warning", "cpu", $"Les pilotes occupent beaucoup le processeur ({Pct(dpc.Avg)} en interruptions)",
                "Souvent un pilote réseau, audio, USB ou de stockage qui se comporte mal. Symptômes typiques : son qui grésille, saccades, souris qui accroche."));
        }
    }

    private static void Disk(WindowData w, List<Finding> findings)
    {
        var top = w.Processes.OrderByDescending(p => p.IoBps).FirstOrDefault(p => p.IoBps > 0);
        // Le disque le plus actif, avec son nom (« Disque D: (HDD …) »).
        var busiest = w.Metrics
            .Where(m => m.Key.StartsWith("disk.", StringComparison.Ordinal) && m.Key.EndsWith(".active", StringComparison.Ordinal) && m.Key != "disk.active")
            .OrderByDescending(m => m.Value.Avg)
            .Select(m => (Key: m.Key, Stats: m.Value))
            .FirstOrDefault();
        var diskName = busiest.Key is null ? "Le disque" : (w.Labels.GetValueOrDefault(busiest.Key) ?? "Le disque").Replace(" · activité", "");
        var latency = busiest.Key is null ? Stat(w, "disk.latency") : Stat(w, busiest.Key.Replace(".active", ".latency"));
        var hdd = diskName.Contains("HDD", StringComparison.Ordinal);
        var culprit = top is null
            ? ""
            : $" Plus gros volume de lectures/écritures : {Display(top)} ({Rate(top.IoBps)}, réseau inclus).";

        if (Stat(w, "disk.active") is { Avg: >= 90 } active)
        {
            var detail = $"{diskName} est occupé {Pct(active.Avg)} du temps.";
            if (latency is { Avg: >= 20 }) detail += $" Chaque accès prend {Num(latency.Value.Avg)} ms en moyenne : les applications attendent le disque.";
            if (hdd) detail += " C'est un disque dur mécanique : il sature dès que plusieurs programmes y lisent en même temps (mise à jour de jeu, indexation, antivirus…).";
            findings.Add(new Finding("critical", "disk", $"Le disque est saturé ({Pct(active.Avg)} d'activité)", detail + culprit, top is null ? null : Display(top)));
        }
        else if (latency is { Avg: >= 50 })
        {
            findings.Add(new Finding("warning", "disk", $"Le disque répond lentement ({Num(latency.Value.Avg)} ms par accès)",
                $"{diskName} : un SSD répond normalement en moins de 5 ms, un disque dur en moins de 20 ms." + culprit,
                top is null ? null : Display(top)));
        }
    }

    private static void Gpu(WindowData w, List<Finding> findings)
    {
        var top = w.Processes.OrderByDescending(p => p.Gpu).FirstOrDefault(p => p.Gpu > 0);
        if (Stat(w, "gpu.total") is { Avg: >= 95 } total)
        {
            findings.Add(new Finding("info", "gpu", $"La carte graphique tourne à fond ({Pct(total.Avg)})",
                (top is null ? "" : $"{Display(top)} l'utilise à {Pct(top.Gpu)}. ") + "Normal en jeu ou pendant un rendu vidéo ; suspect sinon.",
                top is null ? null : Display(top)));
        }

        if (Stat(w, "gpu.video") is { Avg: >= 50 } video)
        {
            findings.Add(new Finding("info", "gpu", $"Encodage ou décodage vidéo en cours ({Pct(video.Avg)} du moteur vidéo)",
                top is null ? "Une application traite de la vidéo." : $"Probablement {Display(top)} : export, montage ou lecture vidéo.",
                top is null ? null : Display(top)));
        }
    }

    private static void Thermal(WindowData w, List<Finding> findings)
    {
        foreach (var sensor in w.Sensors.Where(s => s.Type == "Temperature"))
        {
            // Priorité au seuil annoncé par le composant ; sinon, des seuils prudents par type.
            var limit = sensor.Limit
                ?? (sensor.Name.Contains("Hot Spot", StringComparison.OrdinalIgnoreCase) || sensor.Name.Contains("Junction", StringComparison.OrdinalIgnoreCase) ? 100
                : sensor.HardwareType.StartsWith("Gpu", StringComparison.Ordinal) ? 85
                : sensor.HardwareType == "Cpu" ? 85
                : sensor.Key.Contains("/hdd/", StringComparison.Ordinal) ? 55
                : sensor.Key.Contains("/nvme/", StringComparison.Ordinal) ? 75
                : sensor.HardwareType == "Storage" ? 65
                : 85);
            if (sensor.Value >= limit)
            {
                findings.Add(new Finding("warning", "thermal", $"Température élevée : {sensor.Hardware} · {sensor.Name} à {Num(sensor.Value)} °C",
                    $"Au-delà de {limit} °C, le composant se bride pour se protéger, ce qui fait baisser les performances. Vérifie la poussière et la ventilation."));
            }
        }

        var throttling = w.EventCounts.Where(e => e.Kind == "thermal").Sum(e => e.LastDay);
        if (throttling > 0)
        {
            findings.Add(new Finding("warning", "thermal", $"Le processeur a été bridé {Times(throttling)} en 24 h",
                "Le firmware a réduit la fréquence du processeur, le plus souvent à cause de la chaleur ou d'une limite d'alimentation."));
        }
    }

    private static void Hangs(WindowData w, List<Finding> findings)
    {
        foreach (var hang in w.ActiveHangs)
        {
            var seconds = (w.To - hang.Since) / 1000;
            findings.Add(new Finding("critical", "hang", $"{hang.Process} ne répond plus depuis {Duration(seconds)}",
                string.IsNullOrWhiteSpace(hang.Title) ? "La fenêtre est gelée." : $"Fenêtre gelée : « {hang.Title} ».", hang.Process));
        }

        var past = w.Hangs.Where(h => h.End is not null).GroupBy(h => h.Process).OrderByDescending(g => g.Count());
        foreach (var group in past.Take(3))
        {
            var longest = group.Max(h => (h.End!.Value - h.Start) / 1000);
            findings.Add(new Finding("warning", "hang", $"{group.Key} a gelé {Times(group.Count())}",
                $"Plus long gel : {Duration(longest)}. Pendant un gel, l'application ne répond plus du tout.", group.Key));
        }
    }

    private static void Events(WindowData w, List<Finding> findings)
    {
        // En direct, les signalements couvrent 7 jours, mais seuls les plantages des dernières 24 h comptent.
        var crashes = w.EventCounts.Where(e => e.Kind == "crash")
            .GroupBy(e => e.Title)
            .Select(g => (Title: g.Key, Count: g.Sum(e => w.Live ? e.LastDay : e.Count), Last: g.Max(e => e.Last)))
            .Where(c => c.Count > 0)
            .OrderByDescending(c => c.Count)
            .ThenByDescending(c => c.Last)
            .Take(3);
        foreach (var crash in crashes)
        {
            var app = crash.Title.Replace("Plantage : ", "");
            findings.Add(new Finding("warning", "crash", $"{app} a planté {Times(crash.Count)}{(w.Live ? " en 24 h" : "")}",
                "Détail dans l'onglet Journal.", app));
        }

        void Recent(string kind, string severity, string title, string detail, Func<EventCount, bool>? filter = null)
        {
            var count = w.EventCounts.Where(e => e.Kind == kind && (filter is null || filter(e))).Sum(e => e.Count);
            if (count == 0) return;
            findings.Add(new Finding(severity, kind is "disk" or "hardware" ? "hardware" : "system",
                $"{title} ({Times(count)}{(w.Live ? " en 7 jours" : "")})", detail));
        }

        Recent("bsod", "critical", "Écran bleu", "Le système a planté. Le code d'arrêt est dans le Journal : il oriente vers un pilote ou un composant.");
        Recent("power", "warning", "Redémarrage inattendu", "Coupure de courant, plantage complet ou appui long sur le bouton d'alimentation.");
        StorageErrors(w, findings);
        Recent("disk", "warning", "Le contrôleur de stockage a été réinitialisé", "Le disque a cessé de répondre quelques secondes. Causes fréquentes : câble SATA, pilote de stockage, disque fatigué.",
            e => e.EventId == 129);
        Recent("hardware", "warning", "Erreur matérielle signalée", "Le processeur, la mémoire ou le bus PCIe a corrigé une erreur. Isolée, ce n'est pas grave ; répétée, c'est un signal.");
        Recent("memory", "warning", "Windows a manqué de mémoire virtuelle", "Des applications ont pu planter ou refuser de s'ouvrir. Le Journal indique les plus gros consommateurs à ce moment-là.");
        Recent("gpu", "warning", "Le pilote graphique a planté", "Écran noir quelques secondes puis retour : pilote instable, surchauffe ou overclocking trop poussé.");
    }

    /// <summary>
    /// Erreurs de stockage signalées par Windows, attribuées à leur périphérique. Seul un disque interne en service
    /// justifie l'alerte « sauvegarde tes données » : une carte SD ou une clé USB défaillante remplit le journal
    /// des mêmes événements sans que les disques du PC soient en cause.
    /// </summary>
    private static void StorageErrors(WindowData w, List<Finding> findings)
    {
        var errors = w.EventCounts.Where(e => e.Kind == "disk" && e.EventId != 129).ToList();
        if (errors.Count == 0) return;

        var disks = MeasuredDisks(w);
        var nvme = disks.Where(d => IsInternal(d.Value) == true && d.Value.Contains("NVMe", StringComparison.Ordinal)).Select(d => d.Key).ToList();
        var onInternal = new Dictionary<int, int>();   // numéro du disque interne → erreurs
        var elsewhere = new Dictionary<string, int>(); // périphérique qui n'est pas un disque interne en service → erreurs
        var unplaced = new Dictionary<string, int>();  // périphérique nommé par Windows, mais de nature inconnue → erreurs
        var ongoing = new SortedSet<string>(StringComparer.Ordinal); // supports externes branchés dont les erreurs datent des dernières minutes
        var onSomeNvme = 0;
        var unnamed = 0;
        static void Add<TKey>(Dictionary<TKey, int> counts, TKey key, int count) where TKey : notnull =>
            counts[key] = counts.GetValueOrDefault(key) + count;

        foreach (var e in errors)
        {
            if (e.Device is null)
            {
                // Le pilote NVMe ne nomme que le port du contrôleur (\Device\RaidPort0) : l'erreur vise forcément
                // un SSD NVMe du PC, et on sait lequel quand il n'y en a qu'un.
                if (!e.Provider.Equals("stornvme", StringComparison.OrdinalIgnoreCase)) unnamed += e.Count;
                else if (nvme is [var only]) Add(onInternal, only, e.Count);
                else onSomeNvme += e.Count;
            }
            else if (MeasuredDisk(e.Device, disks) is not { } index)
            {
                // Aucun disque en service sous ce numéro ou cette lettre. Sans aucun disque mesuré, on ne peut rien en conclure.
                Add(disks.Count > 0 ? elsewhere : unplaced, e.Device, e.Count);
            }
            else
            {
                switch (IsInternal(disks[index]))
                {
                    case true:
                        Add(onInternal, index, e.Count);
                        break;
                    // Un support externe n'est désigné que par son numéro : Windows le redonne au prochain support branché,
                    // et le nom affiché aujourd'hui sous ce numéro n'est pas forcément celui du fautif. Sauf si les erreurs
                    // datent des minutes analysées : elles viennent alors bien du support branché en ce moment.
                    case false:
                        Add(elsewhere, e.Device, e.Count);
                        if (w.Live && e.Last >= w.From) ongoing.Add(e.Device);
                        break;
                    default:
                        Add(unplaced, e.Device, e.Count);
                        break;
                }
            }
        }

        var span = w.Live ? " en 7 jours" : "";
        const string backup = "Sauvegarde tes données importantes : un disque qui signale des erreurs peut lâcher sans prévenir.";

        foreach (var (index, count) in onInternal.OrderByDescending(d => d.Value).ThenBy(d => d.Key))
        {
            // « Disque C: (SSD NVMe …) » devient le sujet de la phrase.
            findings.Add(new Finding("critical", "hardware", $"Le d{disks[index][1..]} signale des erreurs ({Times(count)}{span})",
                $"{backup} Windows le numérote « disque {index} » (détail dans l'onglet Journal)."));
        }

        if (onSomeNvme > 0)
        {
            findings.Add(new Finding("critical", "hardware", $"Un SSD NVMe signale des erreurs ({Times(onSomeNvme)}{span})",
                $"{backup} Le pilote NVMe de Windows ne dit pas lequel."));
        }

        if (elsewhere.Count > 0)
        {
            var which = ongoing.Count > 0
                ? $"Sans doute une carte SD, une clé USB ou un disque externe. Elles sont encore en cours sur {string.Join(" et ", ongoing.Select(d => "le " + StorageDevice.Describe(d)))} : le support externe branché en ce moment est donc en cause. "
                : "Sans doute une carte SD, une clé USB ou un disque externe, peut-être retiré depuis : Windows redonne son numéro et sa lettre au prochain support branché, qui n'y est pour rien. ";
            findings.Add(new Finding("warning", "hardware",
                $"Un support amovible ou débranché signale des erreurs ({Times(elsewhere.Values.Sum())}{span})",
                $"Ces erreurs ne viennent pas d'un disque interne en service sur ce PC mais de : {Devices(elsewhere)}. " + which +
                "Causes fréquentes : support retiré pendant une écriture, mauvais contact, carte ou clé en fin de vie. " +
                "Si elles reviennent avec le même support, copie ses données ailleurs et remplace-le."));
        }

        if (unnamed + unplaced.Count > 0)
        {
            var which = unplaced.Count > 0
                ? $"Windows désigne : {Devices(unplaced)}. MonitorKing n'en a pas de description sur cette période : impossible de dire s'il s'agit d'un disque interne. "
                : "Le périphérique n'est pas connu ici (Windows ne le nomme pas, ou l'agent de ce PC ne l'a pas transmis) : ce peut être un disque interne comme une carte SD ou une clé USB. ";
            findings.Add(new Finding("warning", "hardware",
                $"Un périphérique de stockage non identifié signale des erreurs ({Times(unnamed + unplaced.Values.Sum())}{span})",
                which + "Le message complet de Windows est dans le Journal, sur le PC lui-même. S'il s'agit d'un disque interne, sauvegarde tes données importantes."));
        }
    }

    /// <summary>
    /// Vrai pour un disque interne, faux pour un support externe (USB, carte SD), null quand l'agent n'a pas su décrire
    /// le disque (« Disque 3 » sans modèle, ou mesures reçues sans leur libellé) : rien ne dit alors ce que c'est.
    /// </summary>
    private static bool? IsInternal(string label) =>
        label.StartsWith("Disque ", StringComparison.Ordinal) && label.Contains('(') ? !StorageDevice.IsExternalDisk(label) : null;

    /// <summary>Disques mesurés pendant la fenêtre, donc en service : numéro Windows → nom (« Disque C: (SSD NVMe …) »).</summary>
    private static Dictionary<int, string> MeasuredDisks(WindowData w)
    {
        var disks = new Dictionary<int, string>();
        foreach (var key in w.Metrics.Keys)
        {
            if (key.Split('.') is not ["disk", var number, "active"] || !int.TryParse(number, out var index)) continue;
            disks[index] = (w.Labels.GetValueOrDefault(key) ?? $"Disque {index}").Replace(" · activité", "");
        }

        return disks;
    }

    /// <summary>Le disque en service que désigne un périphérique : par son numéro, ou par la lettre d'un de ses volumes.</summary>
    private static int? MeasuredDisk(string device, Dictionary<int, string> disks)
    {
        if (StorageDevice.DiskNumber(device) is { } number) return disks.ContainsKey(number) ? number : null;
        foreach (var (index, label) in disks)
        {
            // « Disque C: D: (HDD …) » : les lettres des volumes suivent le mot « Disque ».
            if (label.Split(' ').Skip(1).TakeWhile(part => part.Length == 2 && part[1] == ':').Contains(device)) return index;
        }

        return null;
    }

    /// <summary>« disque n° 3, disque n° 2 et volume E: (numéros de la Gestion des disques de Windows) », les plus touchés d'abord.</summary>
    private static string Devices(Dictionary<string, int> errors)
    {
        var names = errors.OrderByDescending(d => d.Value)
            .ThenBy(d => d.Key, StringComparer.Ordinal)
            .Select(d => StorageDevice.Describe(d.Key))
            .ToList();
        var shown = names.Take(4).ToList();
        var text = names.Count > shown.Count ? $"{string.Join(", ", shown)} et {names.Count - shown.Count} autre{(names.Count - shown.Count > 1 ? "s" : "")}"
            : shown.Count > 1 ? $"{string.Join(", ", shown.Take(shown.Count - 1))} et {shown[^1]}"
            : shown[0];
        var numbers = errors.Keys.Count(d => StorageDevice.DiskNumber(d) is not null);
        return numbers == 0 ? text : $"{text} ({(numbers > 1 ? "numéros" : "numéro")} de la Gestion des disques de Windows)";
    }

    private static void Wifi(WindowData w, List<Finding> findings)
    {
        if (Stat(w, "wifi.signal") is { Avg: < 40 } signal)
        {
            findings.Add(new Finding("warning", "network", $"Signal Wi-Fi faible ({Pct(signal.Avg)})",
                "Peut expliquer un Internet lent ou instable, sans rapport avec les performances du PC."));
        }
    }

    private static int ResourceRank(string resource)
    {
        var index = Array.IndexOf(ResourceOrder, resource);
        return index < 0 ? ResourceOrder.Length : index;
    }

    private static (double Avg, double Max, double Last)? Stat(WindowData w, string key) =>
        w.Metrics.TryGetValue(key, out var value) ? value : null;

    private static string Display(ProcRow p)
    {
        var name = p.Description is { Length: > 0 } d && d.Length <= 40 ? d : p.Name;
        return p.Via is null ? name : $"{name} (via {p.Via})";
    }

    private static string Pct(double value) => value.ToString("0", Fr) + " %";
    private static string Num(double value) => value.ToString(value >= 100 ? "0" : "0.#", Fr);
    private static string Gb(double mb) => mb >= 1024 ? (mb / 1024).ToString("0.0", Fr) + " Go" : mb.ToString("0", Fr) + " Mo";
    private static string Rate(double bps) => bps >= 1024 * 1024 ? (bps / 1024 / 1024).ToString("0.0", Fr) + " Mo/s" : (bps / 1024).ToString("0", Fr) + " Ko/s";
    private static string Times(int count) => count == 1 ? "1 fois" : $"{count} fois";
    private static string Share(double mb, double totalGb) => totalGb <= 0 ? "" : $" ({mb / 1024 / totalGb * 100:0} % de la RAM)";

    private static string Duration(long seconds) => seconds switch
    {
        < 60 => $"{seconds} s",
        < 3600 => $"{seconds / 60} min {seconds % 60:00} s",
        _ => $"{seconds / 3600} h {seconds % 3600 / 60:00} min",
    };
}
