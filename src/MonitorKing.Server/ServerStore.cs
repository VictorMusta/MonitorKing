using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MonitorKing.Core;
using MonitorKing.Core.Storage;

namespace MonitorKing.Server;

public sealed class ServerOptions
{
    public string DataDirectory { get; set; } = "/data";
    public int Port { get; set; } = 8080;
    public int RetentionDays { get; set; } = 30;
}

public sealed record MachineRecord(string Id, string Label, long Created, long? LastSeen, string? Mode, MachineSummary? Summary);

/// <summary>
/// Registre du serveur : machines inscrites (jeton haché, jamais stocké en clair), codes d'inscription
/// à usage unique, et une base SQLite par machine, au même format que celle de l'agent.
/// </summary>
public sealed class ServerStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // sans 0/O ni 1/I

    private readonly string _connectionString;
    private readonly string _machinesDirectory;
    private readonly ConcurrentDictionary<string, Database> _databases = new();

    public ServerStore(ServerOptions options)
    {
        Directory.CreateDirectory(options.DataDirectory);
        _machinesDirectory = Path.Combine(options.DataDirectory, "machines");
        Directory.CreateDirectory(_machinesDirectory);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(options.DataDirectory, "server.db"),
            Pooling = true,
            DefaultTimeout = 15,
        }.ToString();

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS machine (
                id TEXT PRIMARY KEY,
                label TEXT NOT NULL,
                token_hash TEXT NOT NULL UNIQUE,
                created_ts INTEGER NOT NULL,
                last_seen_ts INTEGER,
                mode TEXT,
                summary_json TEXT
            );
            CREATE TABLE IF NOT EXISTS enrollment (
                code TEXT PRIMARY KEY,
                label TEXT NOT NULL,
                created_ts INTEGER NOT NULL,
                expires_ts INTEGER NOT NULL,
                used_ts INTEGER,
                machine_id TEXT
            );
            CREATE TABLE IF NOT EXISTS layout (
                id TEXT PRIMARY KEY,
                json TEXT NOT NULL,
                updated_ts INTEGER NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>Crée un code d'inscription à usage unique, valable 24 h, pour un PC à nommer.</summary>
    public (string Code, long Expires) CreateEnrollment(string label)
    {
        var bytes = RandomNumberGenerator.GetBytes(8);
        var chars = bytes.Select(b => CodeAlphabet[b % CodeAlphabet.Length]).ToArray();
        var code = $"{new string(chars, 0, 4)}-{new string(chars, 4, 4)}";
        var expires = Now + 24 * 3600_000L;
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO enrollment (code, label, created_ts, expires_ts) VALUES ($code, $label, $now, $expires)";
        command.Parameters.AddWithValue("$code", code);
        command.Parameters.AddWithValue("$label", label);
        command.Parameters.AddWithValue("$now", Now);
        command.Parameters.AddWithValue("$expires", expires);
        command.ExecuteNonQuery();
        return (code, expires);
    }

    /// <summary>Échange un code valide contre un identifiant et un jeton de machine. Le code ne resert jamais.</summary>
    public (string MachineId, string Token, string Label)? Enroll(string code)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        string label;
        using (var find = connection.CreateCommand())
        {
            find.Transaction = transaction;
            find.CommandText = "SELECT label FROM enrollment WHERE code = $code AND used_ts IS NULL AND expires_ts > $now";
            find.Parameters.AddWithValue("$code", code.Trim().ToUpperInvariant());
            find.Parameters.AddWithValue("$now", Now);
            if (find.ExecuteScalar() is not string found) return null;
            label = found;
        }

        var machineId = Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO machine (id, label, token_hash, created_ts) VALUES ($id, $label, $hash, $now);
                UPDATE enrollment SET used_ts = $now, machine_id = $id WHERE code = $code;
                """;
            insert.Parameters.AddWithValue("$id", machineId);
            insert.Parameters.AddWithValue("$label", label);
            insert.Parameters.AddWithValue("$hash", Hash(token));
            insert.Parameters.AddWithValue("$now", Now);
            insert.Parameters.AddWithValue("$code", code.Trim().ToUpperInvariant());
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
        return (machineId, token, label);
    }

    public string? MachineIdForToken(string token)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM machine WHERE token_hash = $hash";
        command.Parameters.AddWithValue("$hash", Hash(token));
        return command.ExecuteScalar() as string;
    }

    public void Touch(string machineId, string mode, MachineSummary summary)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE machine SET last_seen_ts = $now, mode = $mode, summary_json = $summary WHERE id = $id";
        command.Parameters.AddWithValue("$now", Now);
        command.Parameters.AddWithValue("$mode", mode);
        command.Parameters.AddWithValue("$summary", JsonSerializer.Serialize(summary, Json));
        command.Parameters.AddWithValue("$id", machineId);
        command.ExecuteNonQuery();
    }

    public List<MachineRecord> Machines()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, label, created_ts, last_seen_ts, mode, summary_json FROM machine ORDER BY label";
        var list = new List<MachineRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new MachineRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : JsonSerializer.Deserialize<MachineSummary>(reader.GetString(5), Json)));
        }

        return list;
    }

    public MachineRecord? Machine(string id) => Machines().FirstOrDefault(m => m.Id == id);

    public void RenameMachine(string id, string label)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE machine SET label = $label WHERE id = $id";
        command.Parameters.AddWithValue("$label", label);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    /// <summary>Révoque une machine : son jeton ne sera plus accepté (ses données restent consultables).</summary>
    public void Revoke(string id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE machine SET token_hash = 'revoked-' || id || '-' || $now WHERE id = $id";
        command.Parameters.AddWithValue("$now", Now);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public Database DatabaseFor(string machineId)
    {
        if (machineId.Length is 0 or > 32 || !machineId.All(char.IsAsciiLetterOrDigit))
            throw new ArgumentException("Identifiant de machine invalide", nameof(machineId));
        return _databases.GetOrAdd(machineId, id => new Database(Path.Combine(_machinesDirectory, $"{id}.db")));
    }

    public string? GetLayout(string id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT json FROM layout WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() as string;
    }

    public void SaveLayout(string id, string json)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO layout (id, json, updated_ts) VALUES ($id, $json, $ts)
            ON CONFLICT(id) DO UPDATE SET json = excluded.json, updated_ts = excluded.updated_ts
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$json", json);
        command.Parameters.AddWithValue("$ts", Now);
        command.ExecuteNonQuery();
    }
}

/// <summary>Une machine inscrite, vue par le moteur commun (diagnostic, rapports).</summary>
public sealed class ServerMachine : IMachineContext
{
    private List<MetricDef>? _definitions;

    public ServerMachine(MachineRecord record, Database database)
    {
        Record = record;
        Database = database;
    }

    public MachineRecord Record { get; }

    public MachineSummary Summary => (Record.Summary ?? new MachineSummary(Record.Label, "?", "?", 0, 0, Array.Empty<string>(), "?", false, "?", 0, 2000, 5000))
        with { Name = Record.Label };

    public IReadOnlyCollection<MetricDef> Definitions => _definitions ??= Database.KnownMetrics();

    public Database Database { get; }
}
