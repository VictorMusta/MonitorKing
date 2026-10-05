using System.Globalization;

namespace MonitorKing.Updater;

/// <summary>
/// Réglage de la mise à jour automatique et mémoire de la dernière installation, dans un petit fichier texte du
/// dossier de données. Volontairement à part de la base de l'agent : la mise à jour ne dépend de rien d'autre.
/// </summary>
public sealed class UpdateSettings
{
    private readonly object _gate = new();
    private readonly string? _path;
    private bool _enabled = true;
    private Version? _installed;
    private long _installedAt;

    /// <param name="path">Fichier des réglages, ou null pour ne rien enregistrer (tests).</param>
    public UpdateSettings(string? path)
    {
        _path = path;
        try
        {
            if (path is null || !File.Exists(path)) return;
            foreach (var line in File.ReadAllLines(path))
            {
                if (line == "auto=off") _enabled = false;
                else if (line.StartsWith("installed=", StringComparison.Ordinal)
                    && line["installed=".Length..].Split('@') is [var name, var at]
                    && InstallLayout.TryParseName(name, out var version)
                    && long.TryParse(at, NumberStyles.None, CultureInfo.InvariantCulture, out var ts))
                {
                    _installed = version;
                    _installedAt = ts;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Réglage visible dans le tableau de bord local. Activé par défaut.</summary>
    public bool Enabled
    {
        get { lock (_gate) return _enabled; }
        set { lock (_gate) { _enabled = value; Save(); } }
    }

    /// <summary>Dernière version installée par la mise à jour automatique, et quand (millisecondes Unix).</summary>
    public (Version Version, long At)? Installed
    {
        get { lock (_gate) return _installed is null ? null : (_installed, _installedAt); }
    }

    public void RecordInstalled(Version version, long at)
    {
        lock (_gate)
        {
            _installed = version;
            _installedAt = at;
            Save();
        }
    }

    private void Save()
    {
        if (_path is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var lines = new List<string> { _enabled ? "auto=on" : "auto=off" };
            if (_installed is not null) lines.Add($"installed={InstallLayout.Name(_installed)}@{_installedAt.ToString(CultureInfo.InvariantCulture)}");
            File.WriteAllLines(_path, lines);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
