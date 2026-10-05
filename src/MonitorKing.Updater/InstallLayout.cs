namespace MonitorKing.Updater;

/// <summary>
/// Où vivent les versions de l'agent. L'installation d'origine (celle que Windows lance) n'est jamais modifiée :
/// chaque mise à jour est posée entière à côté, dans <c>.update\versions\x.y.z</c>, et lancée au démarrage suivant.
/// <code>
/// C:\Program Files\MonitorKing\          installation d'origine, version de secours
///   .update\staging\                     extraction en cours, invisible tant qu'elle n'est pas finie
///   .update\versions\1.2.3\              version complète et vérifiée
///   .update\versions\1.2.3.confirmed     elle a démarré correctement au moins une fois
///   .update\versions\1.2.3.attempts      nombre de lancements avant confirmation
///   .update\versions\1.2.3.failed        écartée : elle n'a jamais démarré correctement
/// </code>
/// Cette disposition est figée comme le format du manifeste : c'est l'installation d'origine, jamais mise à jour,
/// qui la lit à chaque démarrage.
/// </summary>
public sealed class InstallLayout
{
    public const string AgentExe = "MonitorKing.Agent.exe";
    public const string AgentAssembly = "MonitorKing.Agent.dll";
    public const string Confirmed = "confirmed";
    public const string Attempts = "attempts";
    public const string Failed = "failed";
    private const string UpdateFolder = ".update";
    private const string VersionsFolder = "versions";

    private InstallLayout(string root, Version? runningFromVersions)
    {
        Root = root;
        RunningFromVersions = runningFromVersions;
    }

    /// <summary>Dossier de l'installation d'origine.</summary>
    public string Root { get; }

    /// <summary>Version lancée depuis <c>.update\versions</c>, ou null quand c'est l'installation d'origine qui tourne.</summary>
    public Version? RunningFromVersions { get; }

    public string UpdateDir => Path.Combine(Root, UpdateFolder);
    public string VersionsDir => Path.Combine(UpdateDir, VersionsFolder);
    public string StagingDir => Path.Combine(UpdateDir, "staging");
    public string LockFile => Path.Combine(UpdateDir, "lock");

    public string VersionDir(Version version) => Path.Combine(VersionsDir, Name(version));

    public string Marker(Version version, string kind) => Path.Combine(VersionsDir, $"{Name(version)}.{kind}");

    public static string Name(Version version) => $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";

    /// <summary>Numéro à trois composantes : celui d'un assembly en a quatre, on ne compare jamais la dernière.</summary>
    public static Version Normalize(Version version) => new(version.Major, version.Minor, Math.Max(version.Build, 0));

    public static bool TryParseName(string? name, out Version version)
    {
        version = new Version(0, 0, 0);
        if (name is null || !UpdateManifest.CanonicalVersion().IsMatch(name)) return false;
        version = Version.Parse(name);
        return true;
    }

    /// <summary>Déduit la disposition du dossier de l'exécutable en cours.</summary>
    public static InstallLayout Detect(string baseDirectory)
    {
        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory));
        var versions = Path.GetDirectoryName(directory);
        var update = versions is null ? null : Path.GetDirectoryName(versions);
        var root = update is null ? null : Path.GetDirectoryName(update);
        if (root is not null
            && string.Equals(Path.GetFileName(versions), VersionsFolder, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetFileName(update), UpdateFolder, StringComparison.OrdinalIgnoreCase)
            && TryParseName(Path.GetFileName(directory), out var version))
        {
            return new InstallLayout(root, version);
        }

        return new InstallLayout(directory, null);
    }
}
