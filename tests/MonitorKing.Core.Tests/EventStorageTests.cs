using System.Text.Json;
using Microsoft.Data.Sqlite;
using MonitorKing.Core.Diagnosis;
using MonitorKing.Core.Storage;

namespace MonitorKing.Core.Tests;

/// <summary>
/// Le périphérique des erreurs de stockage, de la base de l'agent à celle du serveur :
/// tiré du message de Windows sur le PC, seul à partir en mode discret, et compté sans plafond pour le diagnostic.
/// </summary>
public sealed class EventStorageTests : IDisposable
{
    private const long Now = 1_790_000_000_000;
    private const long Day = 24 * 3600_000L;
    private static readonly (int Index, string Label)[] InternalDisks =
    {
        (0, "Disque D: (HDD EXEMPLE 2000)"),
        (1, "Disque C: (SSD NVMe EXEMPLE 500)"),
    };

    private readonly TempFolder _folder = new();
    private long _records;

    public void Dispose() => _folder.Dispose();

    private Database NewDatabase(string name) => _folder.Open(name);

    // Messages tels que Windows (en français) les écrit dans le journal Système.
    private static string Paging(int disk) =>
        $@"Une erreur a été détectée sur le périphérique \Device\Harddisk{disk}\DR{disk} lors d'une opération de pagination.";

    private static string Retried(int disk) =>
        $@"L’opération d’E/S à l’adresse de bloc logique 0x0 pour le disque {disk} (nom d’objet périphérique physique : \Device\000000e6) a été tentée à nouveau.";

    private EventItem Disk(int eventId, string? message, long ts, string provider = "disk", string? device = null, string kind = "disk") => new()
    {
        Ts = ts,
        Log = "System",
        Provider = provider,
        EventId = eventId,
        Level = 3,
        Kind = kind,
        Title = "Erreur d'écriture disque pendant la pagination",
        Message = message,
        RecordId = ++_records,
        Device = device,
    };

    private EventItem BlueScreen(long ts) => new()
    {
        Ts = ts,
        Log = "System",
        Provider = "Microsoft-Windows-WER-SystemErrorReporting",
        EventId = 1001,
        Level = 2,
        Kind = "bsod",
        Title = "Écran bleu (BSOD) : 0x0000009f",
        RecordId = ++_records,
    };

    /// <summary>Une fenêtre de mesures : les disques donnés étaient en service à cet instant.</summary>
    private static void Measure(Database db, long ts, params (int Index, string Label)[] disks) =>
        db.PersistWindow(ts, disks.Select(d => (new MetricDef($"disk.{d.Index}.active", $"{d.Label} · activité", "%", "disk", 100), 1.0, 2.0)), Array.Empty<ProcRow>());

    private static void Receive(Database server, IReadOnlyCollection<EventItem> events) =>
        server.Import(Array.Empty<MetricDef>(), Array.Empty<(string, long, double, double)>(), Array.Empty<(long, ProcRow)>(), events, Array.Empty<HangItem>());

    private static DiagnosisResult Diagnose(Database db, long from) =>
        new DiagnosisEngine().Analyze(WindowFactory.History(new Machine(db), from, Now));

    private sealed class Machine : IMachineContext
    {
        public Machine(Database database) => Database = database;

        public MachineSummary Summary { get; } =
            new("PC de test", "Windows 11", "Processeur de test", 8, 16, Array.Empty<string>(), "Carte mère de test", false, "0.0.0", 0, 2000, 5000);

        public IReadOnlyCollection<MetricDef> Definitions => Database.KnownMetrics();

        public Database Database { get; }
    }

    [Fact]
    public void La_base_retient_le_peripherique_nomme_dans_le_message()
    {
        var db = NewDatabase("agent");
        db.InsertEvents(new[]
        {
            Disk(51, Paging(3), Now - 4000),
            Disk(153, Retried(3), Now - 3000),
            Disk(55, "Une altération a été découverte dans la structure du système de fichiers sur le volume E:.", Now - 2000, "Ntfs"),
            Disk(11, @"The driver detected a controller error on \Device\RaidPort0.", Now - 1000, "stornvme"),
            // Un chemin dans le message d'un plantage n'est pas un périphérique en panne.
            Disk(1000, @"Chemin d'accès de l'application défaillante : C:\Jeux\jeu.exe", Now, "Application Error", kind: "crash"),
        });

        var events = db.Events(Now - 10_000, Now, 100); // du plus récent au plus ancien
        Assert.Equal(new string?[] { null, null, "E:", "3", "3" }, events.Select(e => e.Device));
        Assert.All(events, e => Assert.NotNull(e.Message)); // sur le PC, le message reste entier
    }

    [Fact]
    public void Une_base_d_avant_ce_correctif_est_completee_a_l_ouverture()
    {
        var path = _folder.File("ancienne");
        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE event (
                    id INTEGER PRIMARY KEY, ts INTEGER NOT NULL, log TEXT NOT NULL, provider TEXT NOT NULL, event_id INTEGER NOT NULL,
                    level INTEGER NOT NULL, kind TEXT NOT NULL, title TEXT NOT NULL, message TEXT, record_id INTEGER NOT NULL,
                    UNIQUE (log, record_id));
                INSERT INTO event (ts, log, provider, event_id, level, kind, title, message, record_id) VALUES
                    ($ts, 'System', 'disk', 51, 3, 'disk', 'Erreur disque', $paging, 1),
                    ($ts, 'System', 'disk', 153, 3, 'disk', 'Erreur disque', $retried, 2),
                    ($ts, 'System', 'disk', 51, 3, 'disk', 'Erreur disque', NULL, 3),
                    ($ts, 'Application', 'Application Error', 1000, 2, 'crash', 'Plantage : jeu.exe', $crash, 4);
                """;
            command.Parameters.AddWithValue("$ts", Now);
            command.Parameters.AddWithValue("$paging", Paging(3));
            command.Parameters.AddWithValue("$retried", Retried(2));
            command.Parameters.AddWithValue("$crash", @"Chemin d'accès de l'application défaillante : C:\Jeux\jeu.exe");
            command.ExecuteNonQuery();
        }

        var db = new Database(path);

        Assert.Equal(new string?[] { "3", "2", null, null }, db.Events(0, Now, 10).OrderBy(e => e.RecordId).Select(e => e.Device));
        var counted = db.EventCounts(0, Now).Where(e => e.Kind == "disk").OrderBy(e => e.Device).Select(e => (e.Device, e.Count));
        Assert.Equal(new (string?, int)[] { (null, 1), ("2", 1), ("3", 1) }, counted);

        // La reprise est faite une fois pour toutes : rouvrir la base ne change plus rien.
        Assert.Equal(new string?[] { "3", "2", null, null }, new Database(path).Events(0, Now, 10).OrderBy(e => e.RecordId).Select(e => e.Device));
    }

    [Fact]
    public void Les_signalements_sont_comptes_par_peripherique_avec_ceux_des_dernieres_24_h()
    {
        var db = NewDatabase("agent");
        db.InsertEvents(new[]
        {
            Disk(51, Paging(1), Now - 8 * Day), // hors de la période
            Disk(51, Paging(3), Now - 2 * Day),
            Disk(51, Paging(3), Now - Day / 2),
            Disk(51, Paging(3), Now),
            Disk(51, Paging(0), Now - 3 * Day),
        });

        var counts = db.EventCounts(Now - 7 * Day, Now).OrderBy(e => e.Device).ToList();

        Assert.Equal(new[] { "0", "3" }, counts.Select(e => e.Device));
        Assert.Equal((1, 0, Now - 3 * Day), (counts[0].Count, counts[0].LastDay, counts[0].Last));
        Assert.Equal((3, 2, Now), (counts[1].Count, counts[1].LastDay, counts[1].Last));
    }

    [Fact]
    public void Une_rafale_sur_une_carte_SD_ne_masque_ni_le_disque_interne_ni_les_autres_signalements()
    {
        var db = NewDatabase("agent");
        Measure(db, Now - 60_000, InternalDisks);
        var events = new List<EventItem>
        {
            Disk(7, @"The device, \Device\Harddisk0\DR0, has a bad block.", Now - 3 * Day),
            Disk(51, Paging(0), Now - 3 * Day + 1000),
            BlueScreen(Now - 2 * Day),
        };
        // Plus d'erreurs de carte SD, et plus récentes, que la liste d'événements n'en garde (1000).
        events.AddRange(Enumerable.Range(0, 1500).Select(i => Disk(51, Paging(3), Now - Day + i * 1000)));
        db.InsertEvents(events);

        var result = Diagnose(db, Now - 7 * Day);

        Assert.Contains(result.Findings, f => f.Severity == "critical" && f.Title == "Le disque D: (HDD EXEMPLE 2000) signale des erreurs (2 fois)");
        Assert.Contains(result.Findings, f => f.Severity == "warning" && f.Title == "Un support amovible ou débranché signale des erreurs (1500 fois)");
        Assert.Contains(result.Findings, f => f.Severity == "critical" && f.Title == "Écran bleu (1 fois)");
    }

    [Fact]
    public void En_mode_discret_le_serveur_recoit_le_peripherique_sans_le_message()
    {
        var agent = NewDatabase("agent");
        agent.InsertEvents(new[] { Disk(51, Paging(3), Now - 2000), Disk(153, Retried(3), Now - 1500), Disk(51, Paging(0), Now - 1000) });

        // Ce que l'agent envoie en mode discret, tel que cela passe sur le réseau.
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var wire = JsonSerializer.Serialize(agent.EventsAfterId(0, 200).Select(e => e.Event.WithoutMessage(e.Event.Title)), web);
        Assert.DoesNotContain("Harddisk", wire); // rien du message de Windows ne part
        Assert.DoesNotContain("000000e6", wire);
        var received = JsonSerializer.Deserialize<List<EventItem>>(wire, web)!;
        Assert.All(received, e => Assert.Null(e.Message));
        Assert.Equal(new[] { "3", "3", "0" }, received.Select(e => e.Device));

        var server = NewDatabase("serveur");
        Receive(server, received);
        Measure(agent, Now - 60_000, InternalDisks);
        Measure(server, Now - 60_000, InternalDisks);

        // Le serveur conclut comme le PC, sans avoir lu un seul message.
        foreach (var result in new[] { Diagnose(agent, Now - Day), Diagnose(server, Now - Day) })
        {
            Assert.Contains(result.Findings, f => f.Severity == "critical" && f.Title == "Le disque D: (HDD EXEMPLE 2000) signale des erreurs (1 fois)");
            Assert.Contains(result.Findings, f => f.Severity == "warning" && f.Title == "Un support amovible ou débranché signale des erreurs (2 fois)");
        }
    }

    [Fact]
    public void Le_serveur_ne_garde_qu_un_peripherique_bien_forme()
    {
        var server = NewDatabase("serveur");
        Receive(server, new[]
        {
            Disk(51, null, Now, device: "3"),
            Disk(55, null, Now, "Ntfs", device: "e:"),
            Disk(51, null, Now, device: "<script>alert(1)</script>"),
            Disk(51, null, Now),                                         // agent d'avant ce correctif, mode discret
            Disk(51, Paging(5), Now),                                    // agent d'avant ce correctif, partage complet
            Disk(1000, null, Now, "Application Error", device: "3", kind: "crash"),
        });

        Assert.Equal(new string?[] { "3", "E:", null, null, "5", null }, server.Events(0, Now, 10).OrderBy(e => e.RecordId).Select(e => e.Device));
    }

    [Fact]
    public void Un_agent_d_avant_ce_correctif_ne_fait_plus_crier_au_disque_mort_sur_le_serveur()
    {
        var server = NewDatabase("serveur");
        Measure(server, Now - 60_000, InternalDisks);
        // Le lot d'un ancien agent n'a pas de champ « device » : le serveur ne sait pas quel périphérique est visé.
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var received = JsonSerializer.Deserialize<List<EventItem>>(
            """[{"ts":1789999999000,"log":"System","provider":"disk","eventId":51,"level":3,"kind":"disk","title":"Erreur d'écriture disque pendant la pagination","message":null,"recordId":1}]""", web)!;
        Receive(server, received);

        var result = Diagnose(server, Now - Day);

        Assert.DoesNotContain(result.Findings, f => f.Severity == "critical");
        Assert.Contains(result.Findings, f => f.Severity == "warning" && f.Title == "Un périphérique de stockage non identifié signale des erreurs (1 fois)");
    }
}
