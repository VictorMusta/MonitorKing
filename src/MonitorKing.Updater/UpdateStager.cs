using System.IO.Compression;
using System.Reflection;

namespace MonitorKing.Updater;

/// <summary>
/// Pose une version à côté de celle qui tourne. L'archive reçue est déjà vérifiée (taille et SHA-256 du manifeste
/// signé) ; elle est extraite dans un dossier provisoire, puis rendue visible d'un seul renommage. Une panne en
/// cours de route ne laisse donc jamais une version à moitié installée.
/// </summary>
public static class UpdateStager
{
    public const long MaxPackageBytes = 300L * 1024 * 1024;
    private const int MaxEntries = 5000;
    private const long MaxExtractedBytes = 1024L * 1024 * 1024;

    public static void Stage(InstallLayout layout, Version version, byte[] verifiedZip)
    {
        Directory.CreateDirectory(layout.VersionsDir);

        // Une seule préparation à la fois, même si deux agents tournent.
        FileStream gate;
        try
        {
            gate = new FileStream(layout.LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            throw new UpdateException("une autre instance de l'agent prépare déjà cette mise à jour");
        }

        using (gate)
        {
            var staging = layout.StagingDir;
            try
            {
                // Reste d'une préparation interrompue (coupure de courant, agent arrêté) : on repart de zéro.
                if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
                Directory.CreateDirectory(staging);

                Extract(verifiedZip, staging);
                CheckContent(staging, version);

                var target = layout.VersionDir(version);
                if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
                Directory.Move(staging, target);
            }
            catch
            {
                TryDelete(staging);
                throw;
            }
        }
    }

    private static void Extract(byte[] zip, string staging)
    {
        using var archive = new ZipArchive(new MemoryStream(zip, writable: false), ZipArchiveMode.Read);
        if (archive.Entries.Count == 0 || archive.Entries.Count > MaxEntries) throw new UpdateException("archive vide ou trop fournie");

        var names = archive.Entries.Select(entry => SafeName(entry.FullName)).ToList();
        var prefix = CommonFolder(names);
        var root = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
        long extracted = 0;

        for (var i = 0; i < names.Count; i++)
        {
            var relative = names[i][prefix.Length..];
            if (relative.Length == 0) continue;

            var target = Path.GetFullPath(Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new UpdateException("l'archive désigne un fichier hors du dossier de l'agent");

            if (relative.EndsWith('/'))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var entry = archive.Entries[i];
            extracted += entry.Length;
            if (extracted > MaxExtractedBytes) throw new UpdateException("archive trop grosse une fois décompressée");

            using var input = entry.Open();
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            CopyAtMost(input, output, entry.Length);
        }
    }

    /// <summary>Nom d'entrée avec « / » pour séparateur, refusé s'il peut sortir du dossier (chemin absolu, « .. », lecteur).</summary>
    private static string SafeName(string name)
    {
        var normalized = name.Replace('\\', '/');
        var segments = normalized.TrimEnd('/').Split('/');
        if (normalized.Length == 0 || normalized.StartsWith('/') || normalized.Contains(':')
            || normalized.Any(c => c < 0x20)
            || segments.Any(s => s.Length == 0 || s is "." or ".." || s.EndsWith(' ') || s.EndsWith('.')))
        {
            throw new UpdateException("l'archive contient un nom de fichier refusé");
        }

        return normalized;
    }

    /// <summary>Dossier unique qui contient toute l'archive (« MonitorKing-Agent/ »), ou rien si elle est à plat.</summary>
    private static string CommonFolder(List<string> names)
    {
        var first = names[0];
        var slash = first.IndexOf('/');
        if (slash <= 0) return "";
        var prefix = first[..(slash + 1)];
        return names.All(n => n.StartsWith(prefix, StringComparison.Ordinal)) ? prefix : "";
    }

    /// <summary>L'en-tête de l'archive annonce une taille : on n'écrit jamais plus.</summary>
    private static void CopyAtMost(Stream input, Stream output, long length)
    {
        var buffer = new byte[81920];
        long written = 0;
        int count;
        while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            written += count;
            if (written > length) throw new UpdateException("archive incohérente");
            output.Write(buffer, 0, count);
        }

        if (written != length) throw new UpdateException("archive incohérente");
    }

    /// <summary>Le contenu doit être l'agent, dans la version annoncée par le manifeste : pas de mélange de versions.</summary>
    private static void CheckContent(string staging, Version version)
    {
        var assembly = Path.Combine(staging, InstallLayout.AgentAssembly);
        if (!File.Exists(Path.Combine(staging, InstallLayout.AgentExe)) || !File.Exists(assembly))
            throw new UpdateException("l'archive ne contient pas l'agent");

        Version? found;
        try
        {
            found = AssemblyName.GetAssemblyName(assembly).Version;
        }
        catch (Exception e) when (e is BadImageFormatException or IOException)
        {
            throw new UpdateException("l'agent de l'archive est illisible");
        }

        if (found is null || InstallLayout.Normalize(found) != InstallLayout.Normalize(version))
            throw new UpdateException($"l'archive contient la version {found?.ToString(3) ?? "?"}, pas la {InstallLayout.Name(version)} annoncée");
    }

    internal static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
