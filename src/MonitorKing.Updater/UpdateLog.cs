using System.Globalization;

namespace MonitorKing.Updater;

/// <summary>Journal de la mise à jour : une ligne datée par issue, dans le dossier de données de l'agent. N'échoue jamais.</summary>
public sealed class UpdateLog
{
    private const long MaxBytes = 256 * 1024;
    private readonly object _gate = new();
    private readonly string? _path;

    /// <param name="path">Fichier du journal, ou null pour ne rien écrire (tests).</param>
    public UpdateLog(string? path) => _path = path;

    /// <summary>Appelé à chaque ligne, pour la relayer vers la console ou le journal de l'application.</summary>
    public Action<string>? Echo { get; set; }

    public void Write(string line)
    {
        lock (_gate)
        {
            try
            {
                Echo?.Invoke(line);
                if (_path is null) return;
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                // Journal borné : au-delà de la limite, on ne garde que la seconde moitié.
                if (File.Exists(_path) && new FileInfo(_path).Length > MaxBytes)
                {
                    var lines = File.ReadAllLines(_path);
                    File.WriteAllLines(_path, lines.Skip(lines.Length / 2));
                }

                File.AppendAllText(_path, $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} {line}{Environment.NewLine}");
            }
            catch (Exception)
            {
            }
        }
    }
}
