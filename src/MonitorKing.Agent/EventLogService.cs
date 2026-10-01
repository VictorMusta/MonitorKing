using System.Diagnostics.Eventing.Reader;
using System.Text.RegularExpressions;
using MonitorKing.Agent.Storage;

namespace MonitorKing.Agent;

/// <summary>
/// Lit les journaux d'événements Windows utiles au diagnostic (plantages, gels, écrans bleus, erreurs disque,
/// erreurs matérielles, bridage thermique…). Au démarrage, importe les 30 derniers jours :
/// l'historique est donc utile dès l'installation. Ensuite, relit les nouveautés toutes les 30 s.
/// </summary>
public sealed partial class EventLogService : BackgroundService
{
    private static readonly Rule[] Rules =
    {
        new("Application", "Application Error", new[] { 1000 }, "crash"),
        new("Application", "Application Hang", new[] { 1002 }, "hang"),
        new("System", "Microsoft-Windows-Kernel-Power", new[] { 41 }, "power"),
        new("System", "EventLog", new[] { 6008 }, "power"),
        new("System", "Microsoft-Windows-WER-SystemErrorReporting", new[] { 1001 }, "bsod"),
        new("System", "Microsoft-Windows-Resource-Exhaustion-Detector", new[] { 2004 }, "memory"),
        new("System", "disk", new[] { 7, 11, 51, 153 }, "disk"),
        new("System", "Ntfs", new[] { 55, 98, 137 }, "disk"),
        new("System", "Microsoft-Windows-Ntfs", new[] { 55, 98, 137 }, "disk"),
        new("System", "stornvme", new[] { 11, 129 }, "disk"),
        new("System", "storahci", new[] { 129 }, "disk"),
        new("System", "Microsoft-Windows-WHEA-Logger", new[] { 1, 17, 18, 19, 47 }, "hardware"),
        new("System", "Microsoft-Windows-Kernel-Processor-Power", new[] { 37 }, "thermal"),
        new("System", "Display", new[] { 4101 }, "gpu"),
        new("System", "Service Control Manager", new[] { 7031, 7034 }, "service"),
        new("Microsoft-Windows-Diagnostics-Performance/Operational", "Microsoft-Windows-Diagnostics-Performance", new[] { 100 }, "boot"),
    };

    private readonly Database _database;
    private readonly AgentOptions _options;
    private readonly CollectorHost _host;
    private readonly ILogger<EventLogService> _logger;
    private readonly Dictionary<string, string> _ruleErrors = new();

    public EventLogService(Database database, AgentOptions options, CollectorHost host, ILogger<EventLogService> logger)
    {
        _database = database;
        _options = options;
        _host = host;
        _logger = logger;
    }

    [GeneratedRegex(@"<Data Name='BootTime'>(\d+)</Data>")]
    private static partial Regex BootTime();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        // Premier lancement : 30 jours d'historique. Ensuite, seulement la période où l'agent était arrêté
        // (les abonnements ont capté le reste), avec une marge.
        var maxBackfill = TimeSpan.FromDays(Math.Max(1, _options.EventBackfillDays));
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var backfill = _database.LastSampleBefore(now - 20_000) is { } lastAlive
            ? TimeSpan.FromMilliseconds(Math.Clamp(now - lastAlive + 5 * 60_000, 10 * 60_000, maxBackfill.TotalMilliseconds))
            : maxBackfill;
        var imported = Import(backfill, stoppingToken);
        var span = backfill.TotalDays >= 1 ? $"{backfill.TotalDays:0} jours" : backfill.TotalHours >= 1 ? $"{backfill.TotalHours:0} h" : $"{backfill.TotalMinutes:0} min";
        _logger.LogInformation("{Count} nouveaux événements Windows importés (période relue : {Span})", imported, span);

        // Ensuite, Windows nous pousse chaque nouvel événement : pas de relecture périodique des journaux,
        // qui coûterait du disque et du processeur (l'outil ne doit pas causer ce qu'il mesure).
        var watchers = StartWatchers();
        // Plus de coût récurrent : on affiche la durée de l'import initial dans l'explication, pas comme durée par tick.
        _host.ReportStatus(new CollectorStatus("events", Label, true, null, UnreadLogs(), 0,
            $"Import au démarrage ({span} relus) en {_importMs:0} ms, puis abonnement : Windows prévient l'agent à chaque nouvel événement."));
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            foreach (var watcher in watchers)
            {
                watcher.Enabled = false;
                watcher.Dispose();
            }
        }
    }

    private List<EventLogWatcher> StartWatchers()
    {
        var watchers = new List<EventLogWatcher>();
        foreach (var log in Rules.Select(r => r.Log).Distinct())
        {
            if (_ruleErrors.ContainsKey(log)) continue; // journal illisible : déjà signalé
            try
            {
                var filter = Rules.Any(r => r.Log == log && r.Kind == "boot") ? "*" : "*[System[(Level=1 or Level=2 or Level=3)]]";
                var watcher = new EventLogWatcher(new EventLogQuery(log, PathType.LogName, filter));
                watcher.EventRecordWritten += (_, e) => OnEventWritten(log, e);
                watcher.Enabled = true;
                watchers.Add(watcher);
            }
            catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException)
            {
                _logger.LogWarning("Abonnement au journal {Log} impossible : {Message}", log, ex.Message);
            }
        }

        return watchers;
    }

    private void OnEventWritten(string log, EventRecordWrittenEventArgs args)
    {
        using var record = args.EventRecord;
        if (record is null) return;
        try
        {
            var rule = Rules.FirstOrDefault(r => r.Log == log
                && r.Ids.Contains(record.Id)
                && string.Equals(r.Provider, record.ProviderName, StringComparison.OrdinalIgnoreCase));
            if (rule is null) return;
            if (rule.Kind != "boot" && record.Level is not (1 or 2 or 3)) return;
            if (ToItem(rule, record) is { } item) _database.InsertEvents(new[] { item });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Événement ignoré");
        }
    }

    private int Import(TimeSpan window, CancellationToken token)
    {
        var started = DateTime.UtcNow;
        var items = new List<EventItem>();
        // Les événements déjà en base ne sont ni relus en détail ni reformatés : c'est le formatage des messages
        // qui fait lire au service Journal d'événements des Mo de fichiers à chaque démarrage.
        var known = _database.KnownEventRecords(DateTimeOffset.UtcNow.Subtract(window).ToUnixTimeMilliseconds());
        foreach (var rule in Rules)
        {
            if (token.IsCancellationRequested) break;
            try
            {
                items.AddRange(Read(rule, window, known));
                _ruleErrors.Remove(rule.Log);
            }
            catch (UnauthorizedAccessException)
            {
                _ruleErrors[rule.Log] = $"journal « {rule.Log} » réservé aux administrateurs";
            }
            catch (EventLogNotFoundException)
            {
                _ruleErrors[rule.Log] = $"journal « {rule.Log} » absent";
            }
            catch (EventLogException ex)
            {
                _ruleErrors[rule.Log] = $"journal « {rule.Log} » : {ex.Message}";
            }
        }

        var inserted = items.Count > 0 ? _database.InsertEvents(items) : 0;
        _importMs = (DateTime.UtcNow - started).TotalMilliseconds;
        _host.ReportStatus(new CollectorStatus("events", Label, true, null, UnreadLogs(), Math.Round(_importMs, 1)));
        return inserted;
    }

    private const string Label = "Journaux d'événements Windows";
    private double _importMs;

    private string? UnreadLogs() =>
        _ruleErrors.Count == 0 ? null : "Non lu : " + string.Join(" ; ", _ruleErrors.Values.Distinct()) + ".";

    private static IEnumerable<EventItem> Read(Rule rule, TimeSpan window, HashSet<(string Log, long RecordId)> known)
    {
        var ids = string.Join(" or ", rule.Ids.Select(id => $"EventID={id}"));
        // Certains identifiants servent aussi aux bonnes nouvelles (NTFS 98 : « le volume est sain ») :
        // on ne garde que critique, erreur et avertissement, sauf pour la durée de démarrage.
        var levels = rule.Kind == "boot" ? "" : " and (Level=1 or Level=2 or Level=3)";
        var xpath = $"*[System[Provider[@Name='{rule.Provider}'] and ({ids}){levels} and TimeCreated[timediff(@SystemTime) <= {(long)window.TotalMilliseconds}]]]";
        var query = new EventLogQuery(rule.Log, PathType.LogName, xpath) { ReverseDirection = true };
        using var reader = new EventLogReader(query);
        var count = 0;
        for (var record = reader.ReadEvent(); record is not null && count < 500; record = reader.ReadEvent(), count++)
        {
            using (record)
            {
                if (record.RecordId is { } id && known.Contains((rule.Log, id))) continue;
                var item = ToItem(rule, record);
                if (item is not null) yield return item;
            }
        }
    }

    private static EventItem? ToItem(Rule rule, EventRecord record)
    {
        if (record.TimeCreated is not { } created || record.RecordId is not { } recordId) return null;

        string? message;
        try
        {
            message = record.FormatDescription();
        }
        catch (Exception)
        {
            message = null;
        }

        if (message is { Length: > 2000 }) message = message[..2000] + "…";

        return new EventItem
        {
            Ts = new DateTimeOffset(created.ToUniversalTime()).ToUnixTimeMilliseconds(),
            Log = rule.Log,
            Provider = record.ProviderName ?? rule.Provider,
            EventId = record.Id,
            Level = record.Level ?? 4,
            Kind = rule.Kind,
            Title = TitleOf(rule, record),
            Message = message,
            RecordId = recordId,
        };
    }

    private static string? Property(EventRecord record, int index)
    {
        try
        {
            return record.Properties.Count > index ? record.Properties[index].Value?.ToString() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string TitleOf(Rule rule, EventRecord record) => (rule.Kind, record.Id) switch
    {
        ("crash", _) => $"Plantage : {Property(record, 0) ?? "application inconnue"}",
        ("hang", _) => $"A cessé de répondre puis a été fermée : {Property(record, 0) ?? "application inconnue"}",
        ("power", 41) => "Redémarrage sans arrêt propre (coupure, plantage ou appui long sur le bouton)",
        ("power", _) => "Arrêt inattendu du système",
        ("bsod", _) => $"Écran bleu (BSOD) : {Property(record, 0) ?? "code inconnu"}",
        ("memory", _) => "Windows a manqué de mémoire virtuelle",
        ("disk", 7) => "Bloc défectueux détecté sur un disque",
        ("disk", 11) => "Erreur du contrôleur de disque",
        ("disk", 51) => "Erreur d'écriture disque pendant la pagination",
        ("disk", 153) => "Opérations disque relancées (disque lent ou défaillant)",
        ("disk", 129) => "Le contrôleur de stockage a dû être réinitialisé",
        ("disk", _) => "Problème de système de fichiers (NTFS)",
        ("hardware", _) => "Erreur matérielle signalée (WHEA)",
        ("thermal", _) => "Processeur bridé par le firmware (souvent la chaleur)",
        ("gpu", _) => "Le pilote graphique a cessé de répondre puis a récupéré",
        ("service", _) => $"Service arrêté brutalement : {Property(record, 0) ?? "inconnu"}",
        ("boot", _) => BootTitle(record),
        _ => record.ProviderName ?? rule.Provider,
    };

    private static string BootTitle(EventRecord record)
    {
        try
        {
            var match = BootTime().Match(record.ToXml());
            if (match.Success && long.TryParse(match.Groups[1].Value, out var ms))
                return $"Démarrage de Windows : {ms / 1000.0:0.#} s";
        }
        catch (Exception)
        {
        }

        return "Démarrage de Windows";
    }

    private sealed record Rule(string Log, string Provider, int[] Ids, string Kind);
}
