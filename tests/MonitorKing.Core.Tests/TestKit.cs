using Microsoft.Data.Sqlite;
using MonitorKing.Core.Storage;

namespace MonitorKing.Core.Tests;

/// <summary>Un dossier jetable pour des bases au format de l'agent et du serveur.</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder() => Directory.CreateDirectory(Root);

    public string Root { get; } = Path.Combine(Path.GetTempPath(), "monitorking-tests", Guid.NewGuid().ToString("N"));

    public string File(string name) => Path.Combine(Root, $"{name}.db");

    /// <summary>Ouvre la base (la crée au besoin). Rouvrir le même nom, c'est redémarrer l'agent ou le serveur : rien en mémoire.</summary>
    public Database Open(string name) => new(File(name));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
