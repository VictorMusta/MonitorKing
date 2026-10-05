using System.Security.Cryptography;

namespace MonitorKing.Updater;

/// <summary>Ce que le tableau de bord local affiche de la mise à jour automatique.</summary>
public sealed record UpdateStatus(
    string CurrentVersion,
    bool Enabled,
    bool DisabledByConfiguration,
    long? LastCheck,
    string? LastResult,
    string? ReadyVersion,
    string? InstalledVersion,
    long? InstalledAt);

/// <summary>
/// Mise à jour automatique de l'agent. Une vérification après le démarrage puis toutes les six heures environ ;
/// quand une version plus récente est publiée, elle est téléchargée, vérifiée et posée à côté de celle qui tourne,
/// pour être lancée au prochain démarrage. Ordre des vérifications : signature du manifeste, version strictement
/// supérieure, téléchargement, taille et SHA-256, installation. Rien n'est écrit avant la fin des vérifications.
/// </summary>
public sealed class AutoUpdater
{
    private const string PackageSuffix = "-win-x64.zip";
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);
    private static readonly TimeSpan Jitter = TimeSpan.FromHours(1);
    private static readonly TimeSpan ManifestTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PackageTimeout = TimeSpan.FromMinutes(20);

    private readonly InstallLayout _layout;
    private readonly Version _running;
    private readonly UpdateSettings _settings;
    private readonly UpdateLog _log;
    private readonly UpdateSignature _signature;
    private readonly Func<UpdateSource> _source;
    private readonly TaskCompletionSource _firstCheck = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();
    private long? _lastCheck;
    private string? _lastResult;

    public AutoUpdater(InstallLayout layout, Version running, UpdateSettings settings, UpdateLog log, UpdateSignature signature, Func<UpdateSource> source)
    {
        _layout = layout;
        _running = InstallLayout.Normalize(running);
        _settings = settings;
        _log = log;
        _signature = signature;
        _source = source;
    }

    /// <summary>Coupé par la configuration (<c>MonitorKing:AutoUpdate=false</c>) : le réglage du tableau de bord n'y change rien.</summary>
    public bool DisabledByConfiguration { get; init; }

    public bool Enabled => !DisabledByConfiguration && _settings.Enabled;

    /// <summary>Terminé après la première vérification (ou tout de suite si la mise à jour est désactivée).</summary>
    public Task FirstCheck => _firstCheck.Task;

    public UpdateStatus Status
    {
        get
        {
            var ready = UpdateLauncher.Candidates(_layout, _running).FirstOrDefault();
            var installed = _settings.Installed;
            lock (_gate)
            {
                return new UpdateStatus(
                    InstallLayout.Name(_running), Enabled, DisabledByConfiguration, _lastCheck, _lastResult,
                    ready is null ? null : InstallLayout.Name(ready),
                    installed is null ? null : InstallLayout.Name(installed.Value.Version), installed?.At);
            }
        }
    }

    /// <summary>Boucle de fond. Un échec n'entraîne aucune nouvelle tentative avant la vérification suivante.</summary>
    public async Task RunAsync(CancellationToken stop)
    {
        try
        {
            await Task.Delay(StartupDelay, stop).ConfigureAwait(false);
            while (true)
            {
                await CheckOnceAsync(stop).ConfigureAwait(false);
                _firstCheck.TrySetResult();
                await Task.Delay(Interval + Jitter * Random.Shared.NextDouble(), stop).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _firstCheck.TrySetResult();
        }
    }

    /// <summary>Une vérification complète. Ne lève jamais : chaque issue est une ligne du journal.</summary>
    public async Task<string?> CheckOnceAsync(CancellationToken stop)
    {
        // Désactivé : aucun travail, aucune requête.
        if (!Enabled) return null;

        string result;
        try
        {
            result = await CheckAsync(stop).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            throw;
        }
        catch (UpdateException e)
        {
            result = $"Vérification abandonnée : {e.Message}.";
        }
        catch (Exception e)
        {
            result = $"Vérification abandonnée : {e.GetType().Name}, {e.Message}";
        }

        lock (_gate)
        {
            _lastCheck = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _lastResult = result;
        }

        _log.Write(result);
        return result;
    }

    private async Task<string> CheckAsync(CancellationToken stop)
    {
        using var source = _source();

        byte[] raw;
        try
        {
            raw = await source.DownloadAsync(UpdateSource.LatestManifestUrl, UpdateManifest.MaxBytes, null, ManifestTimeout, stop).ConfigureAwait(false);
        }
        catch (UpdateException e)
        {
            return $"Vérification impossible : {e.Message}.";
        }

        // 1. La signature, avant d'interpréter quoi que ce soit.
        if (!UpdateManifest.TrySplit(raw, out var signature, out var signed) || !_signature.IsValid(signed, signature))
            return "Manifeste ignoré : il n'est pas signé avec la clé de publication de MonitorKing.";

        UpdateManifest manifest;
        try
        {
            manifest = UpdateManifest.Parse(signed);
        }
        catch (FormatException e)
        {
            return $"Manifeste ignoré : {e.Message}.";
        }

        // 2. Version strictement supérieure : jamais de retour en arrière. Le cas courant s'arrête ici, après une seule petite requête.
        var version = manifest.Version;
        var name = InstallLayout.Name(version);
        if (version <= _running) return $"À jour ({InstallLayout.Name(_running)}).";

        if (Directory.Exists(_layout.VersionDir(version))) return $"Version {name} déjà prête : elle sera lancée au prochain démarrage de l'agent.";
        if (File.Exists(_layout.Marker(version, InstallLayout.Failed))) return $"Version {name} ignorée : elle n'a pas démarré correctement sur ce PC.";

        var packages = manifest.Files.Where(f => f.Name.EndsWith(PackageSuffix, StringComparison.Ordinal)).ToList();
        if (packages.Count != 1) return $"Version {name} ignorée : son manifeste ne désigne pas un paquet unique pour Windows 64 bits.";
        var package = packages[0];
        if (package.Size > UpdateStager.MaxPackageBytes) return $"Version {name} ignorée : paquet trop gros ({package.Size} octets).";

        // Pas d'élévation ni de contournement : si le dossier d'installation est protégé, on se contente de le dire.
        if (!CanWrite()) return $"Version {name} disponible, mais le dossier d'installation est protégé : réinstalle l'agent pour l'obtenir.";

        // 3. Téléchargement, depuis la version annoncée par le manifeste signé.
        byte[] zip;
        try
        {
            zip = await source.DownloadAsync(UpdateSource.FileUrl(version, package.Name), package.Size, package.Size, PackageTimeout, stop).ConfigureAwait(false);
        }
        catch (UpdateException e)
        {
            return $"Version {name} non téléchargée : {e.Message}.";
        }

        // 4. Taille (imposée au téléchargement) et SHA-256.
        if (zip.LongLength != package.Size || !Convert.ToHexString(SHA256.HashData(zip)).Equals(package.Sha256, StringComparison.OrdinalIgnoreCase))
            return $"Version {name} refusée : le fichier téléchargé ne correspond pas à l'empreinte du manifeste.";

        // 5. Installation à côté de la version qui tourne.
        try
        {
            UpdateStager.Stage(_layout, version, zip);
        }
        catch (Exception e) when (e is UpdateException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return $"Version {name} non installée : {e.Message}";
        }

        return $"Version {name} prête : elle sera lancée au prochain démarrage de l'agent.";
    }

    /// <summary>
    /// À appeler une fois l'agent démarré correctement : confirme la version en cours, puis retire celles qui ne
    /// servent plus. Tant qu'une version n'est pas confirmée, la précédente reste en place pour reprendre la main.
    /// </summary>
    public void ConfirmHealthy()
    {
        try
        {
            if (!Directory.Exists(_layout.VersionsDir)) return;

            if (_layout.RunningFromVersions is { } current)
            {
                var confirmed = _layout.Marker(current, InstallLayout.Confirmed);
                if (!File.Exists(confirmed))
                {
                    File.WriteAllText(confirmed, "");
                    _settings.RecordInstalled(current, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    _log.Write($"Version {InstallLayout.Name(current)} installée et démarrée.");
                }

                File.Delete(_layout.Marker(current, InstallLayout.Attempts));
            }

            Cleanup();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.Write($"Ménage des anciennes versions impossible : {e.Message}");
        }
    }

    /// <summary>Active ou coupe la mise à jour automatique (réglage du tableau de bord local).</summary>
    public void SetEnabled(bool enabled)
    {
        if (_settings.Enabled == enabled) return;
        _settings.Enabled = enabled;
        _log.Write(enabled ? "Mise à jour automatique réactivée." : "Mise à jour automatique désactivée depuis le tableau de bord.");
        if (enabled) return;

        // Couper veut dire « plus rien ne sera installé » : une version prête mais jamais lancée est retirée.
        foreach (var version in UpdateLauncher.Candidates(_layout, _running))
        {
            if (File.Exists(_layout.Marker(version, InstallLayout.Confirmed)) || File.Exists(_layout.Marker(version, InstallLayout.Attempts))) continue;
            UpdateStager.TryDelete(_layout.VersionDir(version));
        }
    }

    /// <summary>Retire les versions plus anciennes que celle qui tourne, les versions écartées et les restes d'extraction.</summary>
    private void Cleanup()
    {
        UpdateStager.TryDelete(_layout.StagingDir);

        foreach (var directory in Directory.EnumerateDirectories(_layout.VersionsDir))
        {
            if (!InstallLayout.TryParseName(Path.GetFileName(directory), out var version)) continue;
            var failed = File.Exists(_layout.Marker(version, InstallLayout.Failed));
            if (version > _running && !failed) continue;
            if (version == _running && _layout.RunningFromVersions is not null) continue;

            UpdateStager.TryDelete(directory);
            TryDeleteFile(_layout.Marker(version, InstallLayout.Confirmed));
            TryDeleteFile(_layout.Marker(version, InstallLayout.Attempts));
            // La marque « écartée » d'une version plus récente reste : elle évite de la retélécharger.
            if (version <= _running) TryDeleteFile(_layout.Marker(version, InstallLayout.Failed));
        }
    }

    private bool CanWrite()
    {
        try
        {
            Directory.CreateDirectory(_layout.VersionsDir);
            var probe = Path.Combine(_layout.UpdateDir, $"probe-{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
