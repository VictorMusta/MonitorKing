using MonitorKing.Core.Diagnosis;

namespace MonitorKing.Core.Tests;

/// <summary>
/// Erreurs de stockage dans le diagnostic : chacune est attribuée à son périphérique, et l'alerte
/// « sauvegarde tes données » est réservée aux disques internes en service.
/// </summary>
public class StorageErrorsDiagnosisTests
{
    private const long Now = 1_790_000_000_000;
    private const long Day = 24 * 3600_000L;
    private const string Hdd = "Disque D: (HDD EXEMPLE 2000)";
    private const string Nvme = "Disque C: (SSD NVMe EXEMPLE 500)";
    private const string Backup = "Sauvegarde tes données importantes";

    private static readonly (int Index, string Label)[] InternalDisks = { (0, Hdd), (1, Nvme) };

    private static EventCount Error(string? device, int count, int eventId = 51, string provider = "disk") =>
        new("disk", provider, eventId, "Erreur d'écriture disque pendant la pagination", device, count, count, Now - 3600_000);

    /// <summary>Diagnostic d'un PC dont les disques donnés sont en service (mesurés pendant la fenêtre).</summary>
    private static DiagnosisResult Analyze(IEnumerable<EventCount> events, (int Index, string Label)[]? disks = null, bool live = true)
    {
        disks ??= InternalDisks;
        return new DiagnosisEngine().Analyze(new WindowData
        {
            From = Now - 120_000,
            To = Now,
            Live = live,
            Metrics = disks.ToDictionary(d => $"disk.{d.Index}.active", _ => (Avg: 1.0, Max: 2.0, Last: 1.0)),
            Labels = disks.ToDictionary(d => $"disk.{d.Index}.active", d => $"{d.Label} · activité"),
            Processes = new List<ProcRow>(),
            EventCounts = events.ToList(),
            Hangs = new List<HangItem>(),
        });
    }

    private static List<Finding> Storage(DiagnosisResult result) => result.Findings.Where(f => f.Resource == "hardware").ToList();

    [Fact]
    public void Des_erreurs_sur_un_disque_interne_donnent_une_alerte_critique_qui_nomme_le_disque()
    {
        var result = Analyze(new[] { Error("1", 11), Error("1", 1, eventId: 153) });

        var finding = Assert.Single(Storage(result));
        Assert.Equal("critical", finding.Severity);
        Assert.Equal("Le disque C: (SSD NVMe EXEMPLE 500) signale des erreurs (12 fois en 7 jours)", finding.Title);
        Assert.Contains(Backup, finding.Detail);
        Assert.Contains("« disque 1 »", finding.Detail);
        Assert.Equal("critical", result.Severity);
        Assert.Equal(finding.Title, result.Verdict);
    }

    [Fact]
    public void Des_erreurs_sur_un_support_amovible_seulement_ne_donnent_plus_d_alerte_critique()
    {
        // Le cas constaté : un lecteur de cartes qui prend tour à tour trois numéros de disque, aucun disque interne touché.
        var result = Analyze(new[] { Error("3", 3000), Error("2", 400), Error("5", 34), Error("3", 1, eventId: 153) });

        var finding = Assert.Single(Storage(result));
        Assert.Equal("warning", finding.Severity);
        Assert.Equal("Un support amovible ou débranché signale des erreurs (3435 fois en 7 jours)", finding.Title);
        Assert.Contains("ne viennent pas d'un disque interne en service sur ce PC mais de : " +
            "disque n° 3, disque n° 2 et disque n° 5 (numéros de la Gestion des disques de Windows).", finding.Detail);
        Assert.DoesNotContain(Backup, finding.Detail);
        Assert.DoesNotContain("EXEMPLE", finding.Detail); // aucun disque interne n'est cité
        Assert.Equal("warning", result.Severity);
        Assert.DoesNotContain(result.Findings, f => f.Severity == "critical");
        Assert.StartsWith("Rien ne sature", result.Verdict);
    }

    [Theory]
    [InlineData("Disque E: (USB Generic STORAGE DEVICE)")]
    [InlineData("Disque 2 (USB Generic STORAGE DEVICE)")]
    [InlineData("Disque E: (HDD USB EXEMPLE Externe 2000)")]
    [InlineData("Disque E: (SSD USB EXEMPLE Portable 500)")]
    [InlineData("Disque E: (Carte SD EXEMPLE 64)")]
    public void Un_support_externe_branche_n_est_ni_pris_pour_un_disque_interne_ni_accuse_par_son_nom(string label)
    {
        // Vu sur un vrai PC : les erreurs d'hier sur le « disque 2 » venaient d'un lecteur de cartes ; aujourd'hui,
        // Windows a donné ce numéro au SSD externe qu'on vient de brancher. Le citer par son nom l'accuserait à tort.
        var result = Analyze(new[] { Error("2", 7) }, InternalDisks.Append((2, label)).ToArray());

        var finding = Assert.Single(Storage(result));
        Assert.Equal("warning", finding.Severity);
        Assert.Equal("Un support amovible ou débranché signale des erreurs (7 fois en 7 jours)", finding.Title);
        Assert.Contains("mais de : disque n° 2 (numéro de la Gestion des disques de Windows).", finding.Detail);
        Assert.Contains("Windows redonne son numéro et sa lettre au prochain support branché, qui n'y est pour rien", finding.Detail);
        Assert.DoesNotContain(label, finding.Detail);
        Assert.DoesNotContain(Backup, finding.Detail);
    }

    [Theory]
    [InlineData("Disque 2")]              // lecteur de cartes dont la carte est illisible : ni lettre, ni modèle
    [InlineData("Disque E:")]
    [InlineData("disk.2.active")]         // serveur : mesures reçues sans leur libellé
    public void Un_disque_que_l_agent_n_a_pas_su_decrire_n_est_pas_suppose_interne(string label)
    {
        var result = Analyze(new[] { Error("2", 7) }, InternalDisks.Append((2, label)).ToArray());

        var finding = Assert.Single(Storage(result));
        Assert.Equal("warning", finding.Severity);
        Assert.Equal("Un périphérique de stockage non identifié signale des erreurs (7 fois en 7 jours)", finding.Title);
        Assert.Contains("Windows désigne : disque n° 2 (numéro de la Gestion des disques de Windows).", finding.Detail);
        Assert.Contains("impossible de dire s'il s'agit d'un disque interne", finding.Detail);
    }

    [Theory]
    [InlineData("Disque E: (EXEMPLE Virtual Disk)")] // machine virtuelle, volume RAID, eMMC : décrit par Windows, sans type connu
    [InlineData("Disque E: (SSD EXEMPLE 250)")]
    public void Un_disque_decrit_sans_bus_externe_est_un_disque_interne(string label)
    {
        var result = Analyze(new[] { Error("2", 7) }, InternalDisks.Append((2, label)).ToArray());

        var finding = Assert.Single(Storage(result));
        Assert.Equal("critical", finding.Severity);
        Assert.Equal($"Le d{label[1..]} signale des erreurs (7 fois en 7 jours)", finding.Title);
    }

    [Fact]
    public void Un_melange_separe_le_disque_interne_du_support_amovible()
    {
        var result = Analyze(new[] { Error("3", 3000), Error("0", 4), Error("0", 1, eventId: 7) });

        var findings = Storage(result);
        Assert.Equal(2, findings.Count);
        var onDisk = Assert.Single(findings, f => f.Severity == "critical");
        Assert.Equal("Le disque D: (HDD EXEMPLE 2000) signale des erreurs (5 fois en 7 jours)", onDisk.Title);
        Assert.Contains(Backup, onDisk.Detail);
        var removable = Assert.Single(findings, f => f.Severity == "warning");
        Assert.Equal("Un support amovible ou débranché signale des erreurs (3000 fois en 7 jours)", removable.Title);
        Assert.Contains("mais de : disque n° 3 (numéro de la Gestion des disques de Windows).", removable.Detail);
        Assert.DoesNotContain(Backup, removable.Detail);

        // Le verdict porte sur le disque interne, pas sur le support amovible, pourtant 600 fois plus bavard.
        Assert.Equal("critical", result.Severity);
        Assert.Equal(onDisk.Title, result.Verdict);
    }

    [Fact]
    public void Chaque_disque_interne_touche_a_son_alerte_le_plus_atteint_d_abord()
    {
        var result = Analyze(new[] { Error("0", 2), Error("1", 9) });

        Assert.Equal(
            new[]
            {
                "Le disque C: (SSD NVMe EXEMPLE 500) signale des erreurs (9 fois en 7 jours)",
                "Le disque D: (HDD EXEMPLE 2000) signale des erreurs (2 fois en 7 jours)",
            },
            Storage(result).Select(f => f.Title));
        Assert.All(Storage(result), f => Assert.Equal("critical", f.Severity));
    }

    [Fact]
    public void Une_erreur_NTFS_est_rattachee_au_disque_qui_porte_le_volume()
    {
        var disks = new[] { (0, "Disque D: E: (HDD EXEMPLE 2000)"), (1, Nvme), (2, "Disque F: (USB Generic STORAGE DEVICE)") };
        var result = Analyze(
            new[]
            {
                Error("E:", 2, eventId: 55, provider: "Ntfs"),  // volume du disque dur interne
                Error("F:", 1, eventId: 98, provider: "Ntfs"),  // clé USB branchée
                Error("G:", 3, eventId: 137, provider: "Ntfs"), // plus aucun disque sous cette lettre
            },
            disks);

        var findings = Storage(result);
        Assert.Equal("Le disque D: E: (HDD EXEMPLE 2000) signale des erreurs (2 fois en 7 jours)", Assert.Single(findings, f => f.Severity == "critical").Title);
        var removable = Assert.Single(findings, f => f.Severity == "warning");
        Assert.Equal("Un support amovible ou débranché signale des erreurs (4 fois en 7 jours)", removable.Title);
        Assert.Contains("mais de : volume G: et volume F:.", removable.Detail);
    }

    [Fact]
    public void Au_dela_de_quatre_peripheriques_la_liste_est_abregee()
    {
        var result = Analyze(new[] { Error("2", 1), Error("3", 1), Error("4", 1), Error("5", 1), Error("6", 1), Error("7", 1) });

        Assert.Contains("mais de : disque n° 2, disque n° 3, disque n° 4, disque n° 5 et 2 autres (numéros de la Gestion des disques de Windows).",
            Assert.Single(Storage(result)).Detail);
    }

    [Fact]
    public void Sans_peripherique_transmis_le_diagnostic_le_dit_au_lieu_d_accuser_un_disque()
    {
        // Sur le serveur, un agent d'avant ce correctif en mode discret : ni message, ni périphérique.
        var result = Analyze(new[] { Error(null, 1000) });

        var finding = Assert.Single(Storage(result));
        Assert.Equal("warning", finding.Severity);
        Assert.Equal("Un périphérique de stockage non identifié signale des erreurs (1000 fois en 7 jours)", finding.Title);
        Assert.Contains("ce peut être un disque interne comme une carte SD ou une clé USB", finding.Detail);
        Assert.Equal("warning", result.Severity);
    }

    [Fact]
    public void Sans_disque_mesure_un_numero_ne_suffit_pas_a_conclure()
    {
        // Compteurs de disque indisponibles, ou période sans mesure : rien ne dit si le disque n° 1 est interne.
        var result = Analyze(new[] { Error("1", 4) }, disks: Array.Empty<(int, string)>());

        var finding = Assert.Single(Storage(result));
        Assert.Equal("warning", finding.Severity);
        Assert.Equal("Un périphérique de stockage non identifié signale des erreurs (4 fois en 7 jours)", finding.Title);
        Assert.Contains("Windows désigne : disque n° 1 (numéro de la Gestion des disques de Windows).", finding.Detail);
        Assert.Contains("S'il s'agit d'un disque interne, sauvegarde tes données importantes.", finding.Detail);
    }

    [Fact]
    public void Une_erreur_du_pilote_NVMe_vise_le_seul_SSD_NVMe_du_PC()
    {
        var result = Analyze(new[] { Error(null, 3, eventId: 11, provider: "stornvme") });

        var finding = Assert.Single(Storage(result));
        Assert.Equal("critical", finding.Severity);
        Assert.Equal("Le disque C: (SSD NVMe EXEMPLE 500) signale des erreurs (3 fois en 7 jours)", finding.Title);
    }

    [Fact]
    public void Avec_plusieurs_SSD_NVMe_l_alerte_reste_critique_sans_en_designer_un()
    {
        var disks = new[] { (0, "Disque D: (SSD NVMe EXEMPLE 1000)"), (1, Nvme) };
        var result = Analyze(new[] { Error(null, 3, eventId: 11, provider: "stornvme") }, disks);

        var finding = Assert.Single(Storage(result));
        Assert.Equal("critical", finding.Severity);
        Assert.Equal("Un SSD NVMe signale des erreurs (3 fois en 7 jours)", finding.Title);
        Assert.Contains(Backup, finding.Detail);
    }

    [Fact]
    public void La_reinitialisation_du_controleur_reste_un_signalement_a_part()
    {
        var result = Analyze(new[] { Error(null, 2, eventId: 129, provider: "storahci") });

        var finding = Assert.Single(Storage(result));
        Assert.Equal("warning", finding.Severity);
        Assert.Equal("Le contrôleur de stockage a été réinitialisé (2 fois en 7 jours)", finding.Title);
    }

    [Fact]
    public void Sur_une_periode_passee_le_compte_est_celui_de_la_periode()
    {
        var result = Analyze(new[] { Error("1", 2), Error("3", 40) }, live: false);

        Assert.Equal(
            new[]
            {
                "Le disque C: (SSD NVMe EXEMPLE 500) signale des erreurs (2 fois)",
                "Un support amovible ou débranché signale des erreurs (40 fois)",
            },
            Storage(result).Select(f => f.Title));
    }

    [Fact]
    public void Les_autres_signalements_se_lisent_dans_les_memes_totaux()
    {
        var events = new[]
        {
            new EventCount("crash", "Application Error", 1000, "Plantage : jeu.exe", null, Count: 5, LastDay: 2, Last: Now),
            new EventCount("crash", "Application Error", 1000, "Plantage : vieux.exe", null, Count: 3, LastDay: 0, Last: Now - 3 * Day),
            new EventCount("thermal", "Microsoft-Windows-Kernel-Processor-Power", 37, "Processeur bridé par le firmware (souvent la chaleur)", null, Count: 9, LastDay: 4, Last: Now),
            new EventCount("bsod", "Microsoft-Windows-WER-SystemErrorReporting", 1001, "Écran bleu (BSOD) : 0x0000009f", null, Count: 1, LastDay: 0, Last: Now - 2 * Day),
        };

        // En direct : 7 jours de signalements, mais seuls les plantages et bridages des dernières 24 h comptent.
        var live = Analyze(events);
        Assert.Contains(live.Findings, f => f.Title == "jeu.exe a planté 2 fois en 24 h");
        Assert.DoesNotContain(live.Findings, f => f.Title.StartsWith("vieux.exe", StringComparison.Ordinal));
        Assert.Contains(live.Findings, f => f.Title == "Le processeur a été bridé 4 fois en 24 h");
        Assert.Contains(live.Findings, f => f.Title == "Écran bleu (1 fois en 7 jours)");

        // Période passée : tout ce qui s'y trouve.
        var past = Analyze(events, live: false);
        Assert.Contains(past.Findings, f => f.Title == "jeu.exe a planté 5 fois");
        Assert.Contains(past.Findings, f => f.Title == "vieux.exe a planté 3 fois");
        Assert.Contains(past.Findings, f => f.Title == "Écran bleu (1 fois)");
    }
}
