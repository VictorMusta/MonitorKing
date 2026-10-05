using MonitorKing.Updater;

namespace MonitorKing.Agent;

/// <summary>
/// Branche la mise à jour automatique sur l'agent. Elle passe avant tout le reste et n'en dépend pas : ni la base,
/// ni le serveur web, ni la configuration. Un agent qui ne démarre plus peut ainsi encore recevoir son correctif.
/// </summary>
public static class AgentUpdate
{
    private const string VerifyManifest = "--verifier-manifeste";

    // Un agent lancé depuis le code source (dotnet run) ne se met pas à jour : il ne doit ni contacter GitHub,
    // ni céder la place à une version publiée posée à côté de son dossier de compilation.
#if DEBUG
    private static readonly bool DevelopmentBuild = true;
#else
    private static readonly bool DevelopmentBuild = false;
#endif

    public static Version RunningVersion { get; } =
        InstallLayout.Normalize(typeof(AgentUpdate).Assembly.GetName().Version ?? new Version(0, 1, 0));

    /// <summary>
    /// Premier geste de l'agent. Vrai si le processus doit s'arrêter là : une version plus récente, déjà installée,
    /// a pris le relais, ou il ne s'agissait que de contrôler un manifeste pour le script de publication.
    /// </summary>
    public static bool HandOver(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length >= 1 && args[0] == VerifyManifest)
        {
            exitCode = Verify(args.ElementAtOrDefault(1), args.ElementAtOrDefault(2));
            return true;
        }

        return !DevelopmentBuild && UpdateLauncher.TryHandOver(InstallLayout.Detect(AppContext.BaseDirectory), RunningVersion, args, Log(args).Write);
    }

    /// <summary>Lance les vérifications en arrière-plan, à l'écart du reste de l'agent.</summary>
    /// <param name="allowed">Faux si la configuration coupe la mise à jour (<c>MonitorKing:AutoUpdate=false</c>).</param>
    public static AutoUpdater Start(string[] args, bool allowed)
    {
        var updater = new AutoUpdater(
            InstallLayout.Detect(AppContext.BaseDirectory),
            RunningVersion,
            new UpdateSettings(Path.Combine(DataDirectory(args), "update-settings.txt")),
            Log(args),
            UpdateSignature.Release,
            () => new UpdateSource())
        {
            DisabledByConfiguration = !allowed || DevelopmentBuild,
        };
        _ = Task.Run(() => updater.RunAsync(CancellationToken.None));
        return updater;
    }

    /// <summary>L'agent tourne depuis assez longtemps pour être jugé sain : la version est confirmée, les anciennes retirées.</summary>
    public static void ConfirmWhenStable(AutoUpdater updater, CancellationToken stopping) =>
        _ = Task.Delay(TimeSpan.FromSeconds(30), stopping).ContinueWith(_ => updater.ConfirmHealthy(), TaskContinuationOptions.OnlyOnRanToCompletion);

    /// <summary>
    /// L'agent n'a pas pu démarrer. Plutôt que de s'arrêter aussitôt, on laisse la mise à jour finir sa première
    /// vérification : si un correctif est publié, il sera installé et lancé au prochain démarrage.
    /// </summary>
    public static int WaitForFix(AutoUpdater updater, string[] args, Exception failure)
    {
        Log(args).Write($"L'agent n'a pas pu démarrer : {failure.GetType().Name}, {failure.Message}");
        if (updater.Enabled) updater.FirstCheck.Wait(TimeSpan.FromMinutes(30));
        return 1;
    }

    /// <summary>
    /// Contrôle de publication : le manifeste est-il accepté par cet agent, tel qu'il sera installé chez les gens ?
    /// Codes de sortie : 0 accepté, 2 signature refusée, 3 format refusé, 4 autre version que cet agent,
    /// 5 paquet absent du manifeste ou différent du fichier fourni.
    /// </summary>
    private static int Verify(string? manifestPath, string? packagePath)
    {
        try
        {
            if (manifestPath is null) return 3;
            if (!UpdateManifest.TrySplit(File.ReadAllBytes(manifestPath), out var signature, out var signed) || !UpdateSignature.Release.IsValid(signed, signature)) return 2;
            var manifest = UpdateManifest.Parse(signed);
            if (manifest.Version != RunningVersion) return 4;
            if (packagePath is null) return 0;

            var package = manifest.Files.SingleOrDefault(f => f.Name == Path.GetFileName(packagePath));
            if (package is null || new FileInfo(packagePath).Length != package.Size) return 5;
            using var stream = File.OpenRead(packagePath);
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).Equals(package.Sha256, StringComparison.OrdinalIgnoreCase) ? 0 : 5;
        }
        catch (Exception e) when (e is FormatException or IOException or UnauthorizedAccessException)
        {
            return 3;
        }
    }

    private static UpdateLog Log(string[] args) => new(Path.Combine(DataDirectory(args), "update.log")) { Echo = Console.WriteLine };

    /// <summary>Dossier de données, lu sans passer par la configuration de l'application (même règle qu'<see cref="AgentMachine.DatabasePath"/>).</summary>
    private static string DataDirectory(string[] args)
    {
        const string option = "--MonitorKing:DataDirectory=";
        var custom = args.FirstOrDefault(a => a.StartsWith(option, StringComparison.OrdinalIgnoreCase))?[option.Length..]
            ?? Environment.GetEnvironmentVariable("MonitorKing__DataDirectory");
        return string.IsNullOrWhiteSpace(custom)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonitorKing")
            : Environment.ExpandEnvironmentVariables(custom);
    }
}
