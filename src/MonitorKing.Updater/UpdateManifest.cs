using System.Text;
using System.Text.RegularExpressions;

namespace MonitorKing.Updater;

/// <summary>Un fichier publié avec une version : son nom, sa taille en octets et son SHA-256 (hexadécimal minuscule).</summary>
public sealed record UpdateFile(string Name, long Size, string Sha256);

/// <summary>
/// Manifeste d'une version. Format figé, pour que les agents déjà installés lisent encore les manifestes futurs :
/// <code>
/// signature=&lt;base64&gt;         signature de tout ce qui suit ce premier saut de ligne
/// version=1.2.3
/// &lt;nom&gt;=&lt;taille&gt;:&lt;sha256&gt;  une ligne par fichier publié
/// </code>
/// ASCII, lignes terminées par LF. Tout ce qui s'en écarte est refusé : une nouveauté passe par un fichier à côté,
/// jamais par une ligne en plus.
/// </summary>
public sealed partial class UpdateManifest
{
    public const int MaxBytes = 16 * 1024;
    private const int MaxFiles = 16;
    private const string SignaturePrefix = "signature=";
    private const string VersionPrefix = "version=";

    private UpdateManifest(Version version, IReadOnlyList<UpdateFile> files)
    {
        Version = version;
        Files = files;
    }

    public Version Version { get; }
    public IReadOnlyList<UpdateFile> Files { get; }

    /// <summary>Sépare la signature du contenu signé. Rien d'autre n'est interprété tant que la signature n'est pas vérifiée.</summary>
    public static bool TrySplit(byte[] raw, out string signature, out byte[] signed)
    {
        signature = "";
        signed = Array.Empty<byte>();
        if (raw.Length == 0 || raw.Length > MaxBytes) return false;

        var end = Array.IndexOf(raw, (byte)'\n');
        if (end <= SignaturePrefix.Length || end == raw.Length - 1) return false;

        // Un octet non ASCII devient « ? », que l'expression ci-dessous refuse.
        var first = Encoding.ASCII.GetString(raw, 0, end);
        if (!first.StartsWith(SignaturePrefix, StringComparison.Ordinal) || !Base64().IsMatch(first[SignaturePrefix.Length..])) return false;

        signature = first[SignaturePrefix.Length..];
        signed = raw[(end + 1)..];
        return true;
    }

    /// <summary>Lit le contenu signé (sans la ligne de signature). Lève <see cref="FormatException"/> au moindre écart.</summary>
    public static UpdateManifest Parse(byte[] signed)
    {
        if (signed.Length == 0 || signed.Length > MaxBytes) throw new FormatException("taille inattendue");
        foreach (var b in signed)
            if (b != '\n' && b is < 0x20 or > 0x7E) throw new FormatException("caractère interdit");
        if (signed[^1] != '\n') throw new FormatException("dernière ligne non terminée");

        var lines = Encoding.ASCII.GetString(signed, 0, signed.Length - 1).Split('\n');
        if (lines.Length < 2) throw new FormatException("il faut un numéro de version et au moins un fichier");
        if (lines.Length > 1 + MaxFiles) throw new FormatException("trop de fichiers");

        if (!lines[0].StartsWith(VersionPrefix, StringComparison.Ordinal) || !CanonicalVersion().IsMatch(lines[0][VersionPrefix.Length..]))
            throw new FormatException("la première ligne doit être « version=x.y.z »");
        var version = Version.Parse(lines[0][VersionPrefix.Length..]);

        var files = new List<UpdateFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "version", "signature" };
        foreach (var line in lines.Skip(1))
        {
            var match = FileLine().Match(line);
            if (!match.Success || !seen.Add(match.Groups[1].Value))
                throw new FormatException("chaque fichier doit être une ligne unique « nom=taille:sha256 »");
            files.Add(new UpdateFile(match.Groups[1].Value, long.Parse(match.Groups[2].Value), match.Groups[3].Value));
        }

        return new UpdateManifest(version, files);
    }

    [GeneratedRegex(@"\A[A-Za-z0-9+/]{16,1024}={0,2}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Base64();

    // Forme canonique seulement : l'adresse de téléchargement est reconstruite à partir du numéro lu.
    [GeneratedRegex(@"\A(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\z", RegexOptions.CultureInvariant)]
    internal static partial Regex CanonicalVersion();

    // Nom nu, sans séparateur de dossier : un manifeste ne peut désigner aucun chemin.
    [GeneratedRegex(@"\A([A-Za-z0-9][A-Za-z0-9_.\-]{0,119})=([1-9][0-9]{0,11}):([0-9a-f]{64})\z", RegexOptions.CultureInvariant)]
    private static partial Regex FileLine();
}
