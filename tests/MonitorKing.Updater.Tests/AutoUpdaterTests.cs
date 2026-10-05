using System.Text;
using MonitorKing.Updater;

namespace MonitorKing.Updater.Tests;

/// <summary>Le parcours complet d'une vérification, contre un faux GitHub : ce qui est accepté, et tout ce qui doit être refusé.</summary>
public class AutoUpdaterTests
{
    private static readonly byte[] Package = Release.Package();

    private static byte[] ManifestFor(byte[] package, Version? version = null, TestKey? key = null) =>
        Release.Manifest(Release.Body(version ?? Release.Published, (Release.PackageName, package)), key);

    private static string AgentExe(Sandbox sandbox, Version version) => Path.Combine(sandbox.Layout.VersionDir(version), InstallLayout.AgentExe);

    [Fact]
    public async Task Une_version_plus_recente_est_telechargee_verifiee_et_posee_a_cote()
    {
        using var sandbox = new Sandbox();
        sandbox.GitHub.Publish(Release.Published, ManifestFor(Package), Package);
        var updater = sandbox.Updater();

        var result = await updater.CheckOnceAsync(default);

        Assert.StartsWith("Version 9.9.9 prête", result);
        Assert.True(File.Exists(AgentExe(sandbox, Release.Published)));
        Assert.True(File.Exists(Path.Combine(sandbox.Layout.VersionDir(Release.Published), "wwwroot", "index.html")));
        Assert.False(Directory.Exists(sandbox.Layout.StagingDir));
        Assert.Equal("9.9.9", updater.Status.ReadyVersion);
        Assert.Equal(new[] { result }, sandbox.Lines);
        // L'installation d'origine n'est pas touchée : seule .update a été créée.
        Assert.Equal(new[] { ".update" }, Directory.EnumerateFileSystemEntries(sandbox.Root).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Le_paquet_vient_du_tag_annonce_par_le_manifeste_pas_de_latest()
    {
        using var sandbox = new Sandbox();
        sandbox.GitHub.Publish(Release.Published, ManifestFor(Package), Package);

        await sandbox.Updater().CheckOnceAsync(default);

        Assert.Contains($"{UpdateSource.ReleasesUrl}/download/v9.9.9/{Release.PackageName}", sandbox.GitHub.Requests);
        Assert.DoesNotContain(sandbox.GitHub.Requests, r => r.Contains("/latest/") && r.EndsWith(".zip"));
    }

    [Fact]
    public async Task Deja_a_jour_une_seule_petite_requete_et_rien_n_est_ecrit()
    {
        using var sandbox = new Sandbox(running: Release.Published);
        sandbox.GitHub.Publish(Release.Published, ManifestFor(Package), Package);

        var result = await sandbox.Updater().CheckOnceAsync(default);

        Assert.Equal("À jour (9.9.9).", result);
        // Une requête, que GitHub fait suivre deux fois : aucune autre adresse n'est demandée.
        Assert.Equal(3, sandbox.GitHub.Requests.Count);
        Assert.All(sandbox.GitHub.Requests, r => Assert.EndsWith(UpdateSource.ManifestName, r));
        Assert.Empty(Directory.EnumerateFileSystemEntries(sandbox.Root));
    }

    [Fact]
    public async Task Jamais_de_retour_en_arriere()
    {
        using var sandbox = new Sandbox(running: new Version(10, 0, 0));
        sandbox.GitHub.Publish(Release.Published, ManifestFor(Package), Package);

        var result = await sandbox.Updater().CheckOnceAsync(default);

        Assert.Equal("À jour (10.0.0).", result);
        Assert.False(sandbox.GitHub.Asked(".zip"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(sandbox.Root));
    }

    [Fact]
    public async Task Manifeste_altere_refuse_avant_tout_telechargement()
    {
        using var sandbox = new Sandbox();
        var manifest = ManifestFor(Package);
        // Un attaquant remplace l'empreinte du paquet sans pouvoir refaire la signature.
        var text = Encoding.ASCII.GetString(manifest);
        var forged = Encoding.ASCII.GetBytes(text[..^65] + new string('0', 64) + "\n");
        sandbox.GitHub.Publish(Release.Published, forged, Package);

        var result = await sandbox.Updater().CheckOnceAsync(default);

        Assert.StartsWith("Manifeste ignoré : il n'est pas signé", result);
        Assert.False(sandbox.GitHub.Asked(".zip"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(sandbox.Root));
    }

    [Fact]
    public async Task Manifeste_signe_par_une_autre_cle_refuse()
    {
        using var sandbox = new Sandbox();
        sandbox.GitHub.Publish(Release.Published, ManifestFor(Package, key: TestKey.Stranger), Package);

        var result = await sandbox.Updater().CheckOnceAsync(default);

        Assert.StartsWith("Manifeste ignoré : il n'est pas signé", result);
        Assert.False(sandbox.GitHub.Asked(".zip"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(sandbox.Root));
    }

    [Fact]
    public async Task Manifeste_bien_signe_mais_hors_format_refuse()
    {
        using var sandbox = new Sandbox();
        var body = Release.Body(Release.Published, (Release.PackageName, Package)).Concat(Encoding.ASCII.GetBytes("note=nouveau champ\n")).ToArray();
        sandbox.GitHub.Publish(Release.Published, Release.Manifest(body), Package);

        var result = await sandbox.Updater().CheckOnceAsync(default);

        Assert.StartsWith("Manifeste ignoré : chaque fichier", result);
        Assert.False(sandbox.GitHub.Asked(".zip"));
    }

    [Fact]
    public async Task Fichier_altere_refuse_et_rien_n_est_installe()
    {
        using var sandbox = new Sandbox();
        var altered = (byte[])Package.Clone();
        altered[^10] ^= 0xFF;
        sandbox.GitHub.Publish(Release.Published, ManifestFor(Package), altered);

        var result = await sandbox.Updater().CheckOnceAsync(default);

        Assert.StartsWith("Version 9.9.9 refusée : le fichier téléchargé ne correspond pas", result);
        Assert.False(Directory.Exists(sandbox.Layout.VersionDir(Release.Published)));
        Assert.False(Directory.Exists(sandbox.Layout.StagingDir));
    }

    [Fact]
    public async Task Telechargement_tronque_refuse()
    {
        using var sandbox = new Sandbox();
        sandbox.GitHub.Publish(Release.Published, ManifestFor(Package), Package);
        // La connexion se coupe à mi-chemin : l'en-tête annonçait la bonne taille.
        sandbox.GitHub.Bytes(FakeGitHub.AssetUrl(Release.PackageName), Package[..(Package.Length / 2)], announced: Package.Length);

        var result = await sandbox.Updater().CheckOnceAsync(default);

        Assert.Equal("Version 9.9.9 non téléchargée : téléchargement incomplet.", result);
        Assert.False(Directory.Exists(sandbox.Layout.VersionDir(Release.Published)));
    }

    [Fact]
    public async Task Fichier_plus_gros_qu_annonce_refuse()
    {
        using var sandbox = new Sandbox();
        sandbox.GitHub.Publish(Release.Published, ManifestFor(Package), Package);
        sandbox.GitHub.Bytes(FakeGitHub.AssetUrl(Release.PackageName), Package.Concat(new byte[1000]).ToArray());

        var result = await sandbox.Updater().CheckOnceAsync(default);

        Assert.Equal("Version 9.9.9 non téléchargée : fichier plus gros qu'annoncé.", result);
        Assert.False(Directory.Exists(sandbox.Layout.VersionDir(Release.Published)));
    }

    [Fact]
    public async Task Manifeste_trop_gros_abandonne()
    {
        using var sandbox = new Sandbox();
        sandbox.GitHub.Publish(Release.Published, new byte[UpdateManifest.MaxBytes + 1]);

        var result = await sandbox.Updater().CheckOnceAsync(default);

        Assert.Equal("Vérification impossible : réponse trop grosse.", result);
    }

    [Theory]
    [InlineData("https://exemple.org/update-manifest.txt")]
    [InlineData("http://github.com/VictorMusta/MonitorKing/releases/download/v9.9.9/update-manifest.txt")]
    [InlineData("https://githubusercontent.com.exemple.org/update-manifest.txt")]
    public async Task Redirection_vers_un_autre_hote_ou_sans_HTTPS_refusee(string target)
    {
        using var sandbox = new Sandbox();
        sandbox.GitHub.Redirect(UpdateSource.LatestManifestUrl, target);
        sandbox.GitHub.Bytes(target, ManifestFor(Package));

        var result = await sandbox.Updater().CheckOnceAsync(default);

        Assert.StartsWith("Vérification impossible : adresse refusée", result);
        Assert.DoesNotContain(target, sandbox.GitHub.Requests);
    }

    [Fact]
    public async Task Pas_de_reseau_une_ligne_de_journal_et_aucune_exception()
    {
        using var sandbox = new Sandbox();
        sandbox.GitHub.Routes[UpdateSource.LatestManifestUrl] = () => throw new HttpRequestException("Hôte inconnu");

        var result = await sandbox.Updater().CheckOnceAsync(default);

        Assert.Equal("Vérification impossible : Hôte inconnu.", result);
        Assert.Single(sandbox.Lines);
    }

    [Fact]
    public async Task Release_sans_manifeste_ignoree()
    {
        using var sandbox = new Sandbox();

        var result = await sandbox.Updater().CheckOnceAsync(default);

        Assert.Equal("Vérification impossible : réponse 404.", result);
    }

    [Fact]
    public async Task Archive_qui_ecrit_hors_du_dossier_refusee()
    {
        using var sandbox = new Sandbox();
        var evil = Release.Package(extra: ("MonitorKing-Agent/../../evasion.txt", Encoding.ASCII.GetBytes("dehors")));
        sandbox.GitHub.Publish(Release.Published, ManifestFor(evil), evil);

        var result = await sandbox.Updater().CheckOnceAsync(default);

        Assert.StartsWith("Version 9.9.9 non installée : l'archive contient un nom de fichier refusé", result);
        Assert.False(Directory.Exists(sandbox.Layout.VersionDir(Release.Published)));
        Assert.False(Directory.Exists(sandbox.Layout.StagingDir));
        Assert.False(File.Exists(Path.Combine(sandbox.Root, "evasion.txt")));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(sandbox.Root)!, "evasion.txt")));
    }

    [Fact]
    public async Task Archive_d_une_autre_version_que_celle_du_manifeste_refusee()
    {
        using var sandbox = new Sandbox();
        var announced = new Version(9, 9, 8);
        sandbox.GitHub.Publish(announced, Release.Manifest(Release.Body(announced, (Release.PackageName, Package))), Package);

        var result = await sandbox.Updater().CheckOnceAsync(default);

        Assert.Equal("Version 9.9.8 non installée : l'archive contient la version 9.9.9, pas la 9.9.8 annoncée", result);
        Assert.False(Directory.Exists(sandbox.Layout.VersionDir(announced)));
    }

    [Fact]
    public async Task Version_deja_prete_pas_de_second_telechargement()
    {
        using var sandbox = new Sandbox();
        sandbox.GitHub.Publish(Release.Published, ManifestFor(Package), Package);
        var updater = sandbox.Updater();
        await updater.CheckOnceAsync(default);
        sandbox.GitHub.Requests.Clear();

        var result = await updater.CheckOnceAsync(default);

        Assert.StartsWith("Version 9.9.9 déjà prête", result);
        Assert.False(sandbox.GitHub.Asked(".zip"));
    }

    [Fact]
    public async Task Version_ecartee_sur_ce_PC_pas_retelechargee()
    {
        using var sandbox = new Sandbox();
        sandbox.GitHub.Publish(Release.Published, ManifestFor(Package), Package);
        Directory.CreateDirectory(sandbox.Layout.VersionsDir);
        sandbox.Mark(Release.Published, InstallLayout.Failed);

        var result = await sandbox.Updater().CheckOnceAsync(default);

        Assert.StartsWith("Version 9.9.9 ignorée : elle n'a pas démarré correctement", result);
        Assert.False(sandbox.GitHub.Asked(".zip"));
    }

    [Fact]
    public async Task Dossier_d_installation_protege_on_le_dit_sans_rien_contourner()
    {
        using var sandbox = new Sandbox();
        sandbox.GitHub.Publish(Release.Published, ManifestFor(Package), Package);
        // Un fichier à la place du dossier .update : impossible d'y écrire, comme sous Program Files sans droits.
        File.WriteAllText(sandbox.Layout.UpdateDir, "");

        var result = await sandbox.Updater().CheckOnceAsync(default);

        Assert.StartsWith("Version 9.9.9 disponible, mais le dossier d'installation est protégé", result);
        Assert.False(sandbox.GitHub.Asked(".zip"));
    }

    [Fact]
    public async Task Paquet_absent_du_manifeste_pour_ce_systeme_ignore()
    {
        using var sandbox = new Sandbox();
        var body = Release.Body(Release.Published, ("MonitorKing-Agent-v9.9.9-linux-x64.zip", Package));
        sandbox.GitHub.Publish(Release.Published, Release.Manifest(body), Package);

        var result = await sandbox.Updater().CheckOnceAsync(default);

        Assert.StartsWith("Version 9.9.9 ignorée : son manifeste ne désigne pas un paquet unique", result);
    }

    [Theory]
    [InlineData(false, true)]   // coupée dans le tableau de bord
    [InlineData(true, false)]   // coupée par la configuration
    public async Task Desactivee_aucune_requete_aucun_travail(bool setting, bool allowed)
    {
        using var sandbox = new Sandbox();
        sandbox.GitHub.Publish(Release.Published, ManifestFor(Package), Package);
        sandbox.Settings.Enabled = setting;

        var result = await sandbox.Updater(allowed: allowed).CheckOnceAsync(default);

        Assert.Null(result);
        Assert.Empty(sandbox.GitHub.Requests);
        Assert.Empty(sandbox.Lines);
        Assert.Empty(Directory.EnumerateFileSystemEntries(sandbox.Root));
    }

    [Fact]
    public async Task Sans_cle_de_publication_embarquee_rien_n_est_installe()
    {
        using var sandbox = new Sandbox();
        sandbox.GitHub.Publish(Release.Published, ManifestFor(Package), Package);

        var result = await sandbox.Updater(new UpdateSignature("", "AQAB")).CheckOnceAsync(default);

        Assert.StartsWith("Manifeste ignoré : il n'est pas signé", result);
        Assert.False(sandbox.GitHub.Asked(".zip"));
    }

    [Fact]
    public async Task Couper_la_mise_a_jour_retire_la_version_prete_jamais_lancee()
    {
        using var sandbox = new Sandbox();
        sandbox.GitHub.Publish(Release.Published, ManifestFor(Package), Package);
        var updater = sandbox.Updater();
        await updater.CheckOnceAsync(default);
        Assert.True(Directory.Exists(sandbox.Layout.VersionDir(Release.Published)));

        updater.SetEnabled(false);

        Assert.False(updater.Status.Enabled);
        Assert.False(Directory.Exists(sandbox.Layout.VersionDir(Release.Published)));
        Assert.Null(updater.Status.ReadyVersion);
    }

    [Fact]
    public async Task L_etat_affiche_suit_la_derniere_verification()
    {
        using var sandbox = new Sandbox();
        var updater = sandbox.Updater();
        var before = updater.Status;

        var result = await updater.CheckOnceAsync(default);

        Assert.Equal(("1.0.0", true, (long?)null, (string?)null), (before.CurrentVersion, before.Enabled, before.LastCheck, before.LastResult));
        Assert.NotNull(updater.Status.LastCheck);
        Assert.Equal(result, updater.Status.LastResult);
    }
}
