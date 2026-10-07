using MonitorKing.Core.Storage;

namespace MonitorKing.Core.Tests;

/// <summary>
/// Les libellés des métriques, tels que le serveur les garde : le diagnostic y lit le nom de chaque disque,
/// et s'il s'agit d'un disque interne ou d'un support externe.
/// </summary>
public sealed class MetricLabelTests : IDisposable
{
    private const long Now = 1_790_000_000_000;
    private const string Key = "disk.2.active";
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private static MetricDef Disk(string label) => new(Key, $"{label} · activité", "%", "disk", 100);

    /// <summary>Un lot reçu d'un agent : une mesure du disque, avec ou sans sa définition.</summary>
    private static void Receive(Database server, long ts, params MetricDef[] definitions) =>
        server.Import(definitions, new[] { (Key, ts, 1.0, 2.0) }, Array.Empty<(long, ProcRow)>(), Array.Empty<EventItem>(), Array.Empty<HangItem>());

    private static string Label(Database db) => db.KnownMetrics().Single(d => d.Key == Key).Label;

    [Fact]
    public void Un_libelle_qui_change_est_reenregistre_sans_attendre_un_redemarrage()
    {
        // Le même numéro de disque d'un lot à l'autre : l'agent mis à jour annonce le bus USB, ou Windows a renuméroté les disques.
        var server = _folder.Open("serveur");
        Receive(server, Now, Disk("Disque E: (SSD EXEMPLE Portable 500)"));
        Receive(server, Now + 10_000, Disk("Disque E: (SSD USB EXEMPLE Portable 500)"));

        Assert.Equal("Disque E: (SSD USB EXEMPLE Portable 500) · activité", Label(server));
    }

    [Fact]
    public void Des_mesures_recues_sans_definition_gardent_le_libelle_deja_connu()
    {
        Receive(_folder.Open("serveur"), Now, Disk("Disque E: (USB Generic STORAGE DEVICE)"));

        // Serveur redémarré (plus rien en mémoire), puis un lot de rattrapage : l'agent, relancé sans ce disque,
        // envoie ses dernières mesures mais plus sa définition.
        var restarted = _folder.Open("serveur");
        Receive(restarted, Now + 10_000);
        Assert.Equal("Disque E: (USB Generic STORAGE DEVICE) · activité", Label(restarted));

        Receive(restarted, Now + 20_000);
        Assert.Equal("Disque E: (USB Generic STORAGE DEVICE) · activité", Label(restarted));
    }

    [Fact]
    public void Une_metrique_jamais_definie_porte_sa_cle_jusqu_a_ce_que_sa_definition_arrive()
    {
        var server = _folder.Open("serveur");
        Receive(server, Now);
        Assert.Equal(Key, Label(server));

        Receive(server, Now + 10_000, Disk("Disque E: (USB Generic STORAGE DEVICE)"));
        Assert.Equal("Disque E: (USB Generic STORAGE DEVICE) · activité", Label(server));
    }
}
