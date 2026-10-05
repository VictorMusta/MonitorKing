using System.Diagnostics;
using System.Text;
using MonitorKing.Updater;

namespace MonitorKing.Updater.Tests;

/// <summary>L'installation côte à côte : préparation interruptible, relais au démarrage, retour à la version précédente.</summary>
public class InstallTests
{
    private static readonly Version V2 = new(2, 0, 0);
    private static readonly Version V3 = new(3, 0, 0);

    // ---------------------------------------------------------------- Préparation d'une version

    [Fact]
    public void Une_panne_en_cours_d_extraction_ne_laisse_rien_d_installe()
    {
        using var sandbox = new Sandbox();
        var kept = sandbox.Install(V2);
        // La deuxième entrée est refusée : l'extraction s'arrête alors que le premier fichier est déjà écrit.
        var broken = Release.Zip(
            ("MonitorKing-Agent/MonitorKing.Agent.exe", new byte[] { 1 }),
            ("MonitorKing-Agent/C:/Windows/evasion.txt", new byte[] { 2 }));

        Assert.Throws<UpdateException>(() => UpdateStager.Stage(sandbox.Layout, Release.Published, broken));

        Assert.False(Directory.Exists(sandbox.Layout.VersionDir(Release.Published)));
        Assert.False(Directory.Exists(sandbox.Layout.StagingDir));
        Assert.True(File.Exists(Path.Combine(kept, InstallLayout.AgentExe)));
    }

    [Fact]
    public void Les_restes_d_une_preparation_interrompue_sont_effaces_avant_de_recommencer()
    {
        using var sandbox = new Sandbox();
        // Comme après une coupure de courant en pleine extraction : un dossier provisoire à moitié rempli.
        Directory.CreateDirectory(sandbox.Layout.StagingDir);
        File.WriteAllText(Path.Combine(sandbox.Layout.StagingDir, "vieux-reste.dll"), "incomplet");

        UpdateStager.Stage(sandbox.Layout, Release.Published, Release.Package());

        var installed = sandbox.Layout.VersionDir(Release.Published);
        Assert.True(File.Exists(Path.Combine(installed, InstallLayout.AgentExe)));
        Assert.False(File.Exists(Path.Combine(installed, "vieux-reste.dll")));
        Assert.False(Directory.Exists(sandbox.Layout.StagingDir));
    }

    [Fact]
    public void Une_preparation_interrompue_n_est_jamais_prise_pour_une_version_installee()
    {
        using var sandbox = new Sandbox();
        Directory.CreateDirectory(sandbox.Layout.StagingDir);
        File.WriteAllText(Path.Combine(sandbox.Layout.StagingDir, InstallLayout.AgentExe), "incomplet");
        var started = new List<string>();

        var handedOver = UpdateLauncher.TryHandOver(sandbox.Layout, sandbox.Running, Array.Empty<string>(), sandbox.Lines.Add, info => { started.Add(info.FileName); return true; });

        Assert.False(handedOver);
        Assert.Empty(started);
    }

    [Fact]
    public void Accepte_une_archive_aux_separateurs_Windows()
    {
        using var sandbox = new Sandbox();

        UpdateStager.Stage(sandbox.Layout, Release.Published, Release.Package(separator: '\\'));

        Assert.True(File.Exists(Path.Combine(sandbox.Layout.VersionDir(Release.Published), "wwwroot", "index.html")));
    }

    [Theory]
    [InlineData("MonitorKing-Agent/../evasion.txt")]
    [InlineData("MonitorKing-Agent/sous/../../evasion.txt")]
    [InlineData("/evasion.txt")]
    [InlineData("C:/evasion.txt")]
    [InlineData("MonitorKing-Agent/nom-piege. ")]
    public void Refuse_tout_nom_qui_peut_sortir_du_dossier(string name)
    {
        using var sandbox = new Sandbox();
        var package = Release.Package(extra: (name, Encoding.ASCII.GetBytes("x")));

        Assert.Throws<UpdateException>(() => UpdateStager.Stage(sandbox.Layout, Release.Published, package));

        Assert.False(Directory.Exists(sandbox.Layout.VersionDir(Release.Published)));
        Assert.False(Directory.Exists(sandbox.Layout.StagingDir));
        // Rien n'a été écrit là où ces noms auraient mené : à côté du dossier provisoire, ou au-dessus de l'installation.
        foreach (var escape in new[] { sandbox.Layout.UpdateDir, sandbox.Root, Path.GetDirectoryName(sandbox.Root)! })
            Assert.False(File.Exists(Path.Combine(escape, "evasion.txt")));
    }

    [Fact]
    public void Refuse_une_archive_sans_agent()
    {
        using var sandbox = new Sandbox();

        var error = Assert.Throws<UpdateException>(() => UpdateStager.Stage(sandbox.Layout, Release.Published, Release.Zip(("MonitorKing-Agent/lisez-moi.txt", new byte[] { 1 }))));

        Assert.Equal("l'archive ne contient pas l'agent", error.Message);
        Assert.False(Directory.Exists(sandbox.Layout.VersionDir(Release.Published)));
    }

    [Fact]
    public void Une_seule_preparation_a_la_fois()
    {
        using var sandbox = new Sandbox();
        Directory.CreateDirectory(sandbox.Layout.VersionsDir);
        using var other = new FileStream(sandbox.Layout.LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        var error = Assert.Throws<UpdateException>(() => UpdateStager.Stage(sandbox.Layout, Release.Published, Release.Package()));

        Assert.Contains("une autre instance", error.Message);
    }

    // ---------------------------------------------------------------- Relais au démarrage

    private static (bool HandedOver, List<ProcessStartInfo> Started) HandOver(Sandbox sandbox, Func<ProcessStartInfo, bool>? survives = null, params string[] args)
    {
        var started = new List<ProcessStartInfo>();
        var handedOver = UpdateLauncher.TryHandOver(sandbox.Layout, sandbox.Running, args, sandbox.Lines.Add, info =>
        {
            started.Add(info);
            return survives?.Invoke(info) ?? true;
        });
        return (handedOver, started);
    }

    [Fact]
    public void Lance_la_version_la_plus_recente_avec_les_memes_arguments()
    {
        using var sandbox = new Sandbox();
        sandbox.Install(V2);
        sandbox.Install(V3);

        var (handedOver, started) = HandOver(sandbox, null, "--MonitorKing:Port=5799", "un argument avec espaces");

        Assert.True(handedOver);
        Assert.Equal(Path.Combine(sandbox.Layout.VersionDir(V3), InstallLayout.AgentExe), Assert.Single(started).FileName);
        Assert.Equal(new[] { "--MonitorKing:Port=5799", "un argument avec espaces" }, started[0].ArgumentList);
        Assert.False(started[0].UseShellExecute);
        Assert.Empty(sandbox.Lines);
    }

    [Fact]
    public void Ignore_les_versions_egales_ou_plus_anciennes_et_les_dossiers_douteux()
    {
        using var sandbox = new Sandbox(running: V2);
        sandbox.Install(new Version(1, 9, 9));
        sandbox.Install(V2);
        sandbox.Install(V3, withExe: false);                       // dossier sans exécutable
        Directory.CreateDirectory(Path.Combine(sandbox.Layout.VersionsDir, "3.0"));
        File.WriteAllText(Path.Combine(sandbox.Layout.VersionsDir, "3.0", InstallLayout.AgentExe), "");
        Directory.CreateDirectory(Path.Combine(sandbox.Layout.VersionsDir, "04.0.0"));
        File.WriteAllText(Path.Combine(sandbox.Layout.VersionsDir, "04.0.0", InstallLayout.AgentExe), "");

        var (handedOver, started) = HandOver(sandbox);

        Assert.False(handedOver);
        Assert.Empty(started);
    }

    [Fact]
    public void Une_version_mise_a_jour_ne_relance_jamais_rien()
    {
        using var sandbox = new Sandbox(running: V2, runningFromVersions: V2);
        sandbox.Install(V2);
        sandbox.Install(V3);

        var (handedOver, started) = HandOver(sandbox);

        Assert.False(handedOver);
        Assert.Empty(started);
        Assert.Equal(sandbox.Root, sandbox.Layout.Root);
        Assert.Equal(V2, sandbox.Layout.RunningFromVersions);
    }

    [Fact]
    public void Si_la_nouvelle_version_s_arrete_aussitot_la_precedente_reprend_la_main()
    {
        using var sandbox = new Sandbox();
        sandbox.Install(V2);
        sandbox.Mark(V2, InstallLayout.Confirmed);
        sandbox.Install(V3);

        var (handedOver, started) = HandOver(sandbox, info => !info.FileName.Contains("3.0.0"));

        Assert.True(handedOver);
        Assert.Equal(new[] { "3.0.0", "2.0.0" }, started.Select(s => Path.GetFileName(Path.GetDirectoryName(s.FileName))));
        Assert.Contains("Version 3.0.0 : arrêtée aussitôt lancée", Assert.Single(sandbox.Lines));
    }

    [Fact]
    public void Si_aucune_version_ne_tient_l_installation_d_origine_demarre_elle_meme()
    {
        using var sandbox = new Sandbox();
        sandbox.Install(V2);

        var (handedOver, _) = HandOver(sandbox, _ => false);

        Assert.False(handedOver);
    }

    [Fact]
    public void Une_version_jamais_confirmee_est_ecartee_apres_trois_lancements()
    {
        using var sandbox = new Sandbox();
        sandbox.Install(V2);

        for (var boot = 1; boot <= UpdateLauncher.MaxAttempts; boot++)
        {
            Assert.True(HandOver(sandbox).HandedOver);
            Assert.Equal(boot.ToString(), File.ReadAllText(sandbox.Layout.Marker(V2, InstallLayout.Attempts)));
        }

        var (handedOver, started) = HandOver(sandbox);

        Assert.False(handedOver);
        Assert.Empty(started);
        Assert.True(sandbox.Has(V2, InstallLayout.Failed));
        Assert.Contains("Version 2.0.0 écartée", sandbox.Lines[^1]);
        Assert.Empty(UpdateLauncher.Candidates(sandbox.Layout, sandbox.Running));
    }

    [Fact]
    public void Une_version_confirmee_n_est_plus_comptee()
    {
        using var sandbox = new Sandbox();
        sandbox.Install(V2);
        sandbox.Mark(V2, InstallLayout.Confirmed);

        for (var boot = 0; boot < 10; boot++) Assert.True(HandOver(sandbox).HandedOver);

        Assert.False(sandbox.Has(V2, InstallLayout.Attempts));
        Assert.False(sandbox.Has(V2, InstallLayout.Failed));
    }

    [Fact]
    public void Un_lancement_impossible_ne_fait_pas_tomber_l_agent()
    {
        using var sandbox = new Sandbox();
        sandbox.Install(V2);

        var handedOver = UpdateLauncher.TryHandOver(sandbox.Layout, sandbox.Running, Array.Empty<string>(), sandbox.Lines.Add, _ => throw new InvalidOperationException("bloqué par l'antivirus"));

        Assert.False(handedOver);
        Assert.Contains("lancement impossible (bloqué par l'antivirus)", Assert.Single(sandbox.Lines));
    }

    // ---------------------------------------------------------------- Disposition des dossiers

    [Fact]
    public void Reconnait_l_installation_d_origine_et_une_version_mise_a_jour()
    {
        var origin = InstallLayout.Detect(@"C:\Program Files\MonitorKing\");
        var updated = InstallLayout.Detect(@"C:\Program Files\MonitorKing\.update\versions\1.2.3\");
        var lookalike = InstallLayout.Detect(@"C:\Program Files\MonitorKing\.update\versions\pas-une-version");

        Assert.Equal((@"C:\Program Files\MonitorKing", (Version?)null), (origin.Root, origin.RunningFromVersions));
        Assert.Equal((@"C:\Program Files\MonitorKing", new Version(1, 2, 3)), (updated.Root, updated.RunningFromVersions));
        Assert.Null(lookalike.RunningFromVersions);
        Assert.Equal(@"C:\Program Files\MonitorKing\.update\versions\1.2.3", origin.VersionDir(new Version(1, 2, 3, 0)));
    }

    // ---------------------------------------------------------------- Confirmation et ménage

    [Fact]
    public void Une_version_qui_demarre_bien_est_confirmee_et_les_anciennes_retirees()
    {
        using var sandbox = new Sandbox(running: V3, runningFromVersions: V3);
        sandbox.Install(V2);
        sandbox.Mark(V2, InstallLayout.Confirmed);
        sandbox.Install(V3);
        sandbox.Mark(V3, InstallLayout.Attempts, "1");
        var newer = sandbox.Install(new Version(4, 0, 0));
        Directory.CreateDirectory(sandbox.Layout.StagingDir);
        var updater = sandbox.Updater();

        updater.ConfirmHealthy();
        updater.ConfirmHealthy();

        Assert.True(sandbox.Has(V3, InstallLayout.Confirmed));
        Assert.False(sandbox.Has(V3, InstallLayout.Attempts));
        Assert.True(Directory.Exists(sandbox.Layout.VersionDir(V3)));
        Assert.False(Directory.Exists(sandbox.Layout.VersionDir(V2)));
        Assert.False(sandbox.Has(V2, InstallLayout.Confirmed));
        Assert.True(Directory.Exists(newer));
        Assert.False(Directory.Exists(sandbox.Layout.StagingDir));
        Assert.Equal(V3, sandbox.Settings.Installed?.Version);
        Assert.Equal("3.0.0", updater.Status.InstalledVersion);
        // Une seule ligne, à la première confirmation.
        Assert.Equal("Version 3.0.0 installée et démarrée.", Assert.Single(sandbox.Lines));
    }

    [Fact]
    public void Tant_que_la_nouvelle_version_n_est_pas_confirmee_la_precedente_reste_en_place()
    {
        using var sandbox = new Sandbox(running: V2, runningFromVersions: V2);
        sandbox.Install(V2);
        sandbox.Install(V3);
        sandbox.Mark(V3, InstallLayout.Attempts, "1");

        sandbox.Updater().ConfirmHealthy();

        Assert.True(Directory.Exists(sandbox.Layout.VersionDir(V2)));
        Assert.True(Directory.Exists(sandbox.Layout.VersionDir(V3)));
    }

    [Fact]
    public void L_installation_d_origine_retire_les_versions_depassees_et_celles_ecartees()
    {
        using var sandbox = new Sandbox(running: V2);
        sandbox.Install(new Version(1, 5, 0));
        sandbox.Install(V2);
        sandbox.Install(V3);
        sandbox.Mark(V3, InstallLayout.Failed);
        var updater = sandbox.Updater();

        updater.ConfirmHealthy();

        Assert.Empty(Directory.EnumerateDirectories(sandbox.Layout.VersionsDir));
        // La marque reste : elle évite de retélécharger une version qui ne démarre pas sur ce PC.
        Assert.True(sandbox.Has(V3, InstallLayout.Failed));
        Assert.Null(sandbox.Settings.Installed);
        Assert.Empty(sandbox.Lines);
    }

    [Fact]
    public void Le_reglage_et_la_derniere_installation_survivent_a_un_redemarrage()
    {
        using var sandbox = new Sandbox();
        var path = Path.Combine(sandbox.Root, "donnees", "update-settings.txt");
        var settings = new UpdateSettings(path);
        Assert.True(settings.Enabled);

        settings.Enabled = false;
        settings.RecordInstalled(V3, 1_791_000_000_000);
        var reloaded = new UpdateSettings(path);

        Assert.False(reloaded.Enabled);
        Assert.Equal((V3, 1_791_000_000_000), reloaded.Installed);
    }
}
