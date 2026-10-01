using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;

namespace MonitorKing.Agent.Storage;

/// <summary>
/// Stockage local (SQLite). Les mesures sont agrégées par fenêtres de 10 s (moyenne + max) ;
/// les applications ne sont gardées que si elles figurent parmi les plus gourmandes de la fenêtre.
/// </summary>
public sealed class Database
{
    private readonly string _connectionString;
    private readonly ConcurrentDictionary<string, long> _metricIds = new();

    public Database(AgentOptions options)
    {
        var directory = string.IsNullOrWhiteSpace(options.DataDirectory)
            ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonitorKing")
            : Environment.ExpandEnvironmentVariables(options.DataDirectory);
        Directory.CreateDirectory(directory);
        Path = System.IO.Path.Combine(directory, "monitorking.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path,
            Pooling = true,
            DefaultTimeout = 15,
        }.ToString();
        Initialize();
    }

    public string Path { get; }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA synchronous=NORMAL;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private void Initialize()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS metric (
                id INTEGER PRIMARY KEY,
                key TEXT NOT NULL UNIQUE,
                label TEXT NOT NULL,
                unit TEXT NOT NULL,
                grp TEXT NOT NULL,
                max REAL
            );
            CREATE TABLE IF NOT EXISTS sample (
                metric_id INTEGER NOT NULL,
                ts INTEGER NOT NULL,
                avg REAL NOT NULL,
                max REAL NOT NULL,
                PRIMARY KEY (metric_id, ts)
            ) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS proc_sample (
                ts INTEGER NOT NULL,
                name TEXT NOT NULL,
                description TEXT,
                via TEXT,
                cpu REAL NOT NULL,
                ram_mb REAL NOT NULL,
                commit_mb REAL NOT NULL,
                io_read REAL NOT NULL,
                io_write REAL NOT NULL,
                hard_faults REAL NOT NULL,
                gpu REAL NOT NULL,
                vram_mb REAL NOT NULL,
                count INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_proc_sample_ts ON proc_sample(ts);
            CREATE TABLE IF NOT EXISTS event (
                id INTEGER PRIMARY KEY,
                ts INTEGER NOT NULL,
                log TEXT NOT NULL,
                provider TEXT NOT NULL,
                event_id INTEGER NOT NULL,
                level INTEGER NOT NULL,
                kind TEXT NOT NULL,
                title TEXT NOT NULL,
                message TEXT,
                record_id INTEGER NOT NULL,
                UNIQUE (log, record_id)
            );
            CREATE INDEX IF NOT EXISTS ix_event_ts ON event(ts);
            CREATE TABLE IF NOT EXISTS hang (
                id INTEGER PRIMARY KEY,
                start_ts INTEGER NOT NULL,
                end_ts INTEGER,
                pid INTEGER NOT NULL,
                process TEXT NOT NULL,
                title TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_hang_start ON hang(start_ts);
            CREATE TABLE IF NOT EXISTS agent_run (
                start_ts INTEGER PRIMARY KEY
            );
            CREATE TABLE IF NOT EXISTS layout (
                id TEXT PRIMARY KEY,
                json TEXT NOT NULL,
                updated_ts INTEGER NOT NULL
            );
            -- Un gel encore ouvert au démarrage date d'une session précédente : sa fin est inconnue.
            UPDATE hang SET end_ts = -1 WHERE end_ts IS NULL;
            -- Les événements informatifs (ex. « volume sain ») ne sont pas des problèmes.
            DELETE FROM event WHERE level > 3 AND kind <> 'boot';
            """;
        command.ExecuteNonQuery();
    }

    private long MetricId(SqliteConnection connection, SqliteTransaction? transaction, MetricDef def)
    {
        if (_metricIds.TryGetValue(def.Key, out var id)) return id;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO metric (key, label, unit, grp, max) VALUES ($key, $label, $unit, $grp, $max)
            ON CONFLICT(key) DO UPDATE SET label = excluded.label, unit = excluded.unit, grp = excluded.grp, max = excluded.max;
            SELECT id FROM metric WHERE key = $key;
            """;
        command.Parameters.AddWithValue("$key", def.Key);
        command.Parameters.AddWithValue("$label", def.Label);
        command.Parameters.AddWithValue("$unit", def.Unit);
        command.Parameters.AddWithValue("$grp", def.Group);
        command.Parameters.AddWithValue("$max", (object?)def.Max ?? DBNull.Value);
        id = (long)command.ExecuteScalar()!;
        _metricIds[def.Key] = id;
        return id;
    }

    public void PersistWindow(long ts, IEnumerable<(MetricDef Def, double Avg, double Max)> samples, IEnumerable<ProcRow> processes)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "INSERT OR REPLACE INTO sample (metric_id, ts, avg, max) VALUES ($id, $ts, $avg, $max)";
            var id = command.Parameters.Add("$id", SqliteType.Integer);
            command.Parameters.AddWithValue("$ts", ts);
            var avg = command.Parameters.Add("$avg", SqliteType.Real);
            var max = command.Parameters.Add("$max", SqliteType.Real);
            foreach (var (def, a, m) in samples)
            {
                id.Value = MetricId(connection, transaction, def);
                avg.Value = a;
                max.Value = m;
                command.ExecuteNonQuery();
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO proc_sample (ts, name, description, via, cpu, ram_mb, commit_mb, io_read, io_write, hard_faults, gpu, vram_mb, count)
                VALUES ($ts, $name, $description, $via, $cpu, $ram, $commit, $read, $write, $hf, $gpu, $vram, $count)
                """;
            command.Parameters.AddWithValue("$ts", ts);
            var name = command.Parameters.Add("$name", SqliteType.Text);
            var description = command.Parameters.Add("$description", SqliteType.Text);
            var via = command.Parameters.Add("$via", SqliteType.Text);
            var cpu = command.Parameters.Add("$cpu", SqliteType.Real);
            var ram = command.Parameters.Add("$ram", SqliteType.Real);
            var commit = command.Parameters.Add("$commit", SqliteType.Real);
            var read = command.Parameters.Add("$read", SqliteType.Real);
            var write = command.Parameters.Add("$write", SqliteType.Real);
            var hf = command.Parameters.Add("$hf", SqliteType.Real);
            var gpu = command.Parameters.Add("$gpu", SqliteType.Real);
            var vram = command.Parameters.Add("$vram", SqliteType.Real);
            var count = command.Parameters.Add("$count", SqliteType.Integer);
            foreach (var p in processes)
            {
                name.Value = p.Name;
                description.Value = (object?)p.Description ?? DBNull.Value;
                via.Value = (object?)p.Via ?? DBNull.Value;
                cpu.Value = p.Cpu;
                ram.Value = p.RamMb;
                commit.Value = p.CommitMb;
                read.Value = p.IoReadBps;
                write.Value = p.IoWriteBps;
                hf.Value = p.HardFaultsPerSec;
                gpu.Value = p.Gpu;
                vram.Value = p.VramMb;
                count.Value = p.Count;
                command.ExecuteNonQuery();
            }
        }

        transaction.Commit();
    }

    /// <summary>Série d'une métrique, regroupée en au plus <paramref name="maxPoints"/> points.</summary>
    public List<double[]> Series(string key, long from, long to, int maxPoints)
    {
        var step = Math.Max(10_000, (to - from) / Math.Max(1, maxPoints));
        step = (long)Math.Ceiling(step / 10_000d) * 10_000;
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT (s.ts / $step) * $step AS bucket, AVG(s.avg), MAX(s.max)
            FROM sample s JOIN metric m ON m.id = s.metric_id
            WHERE m.key = $key AND s.ts >= $from AND s.ts <= $to
            GROUP BY bucket ORDER BY bucket
            """;
        command.Parameters.AddWithValue("$step", step);
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$from", from);
        command.Parameters.AddWithValue("$to", to);
        var points = new List<double[]>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            points.Add(new[] { reader.GetInt64(0) + step / 2d, reader.GetDouble(1), reader.GetDouble(2) });
        return points;
    }

    /// <summary>Fenêtres de 10 s brutes d'une métrique (moyenne et pic de chaque fenêtre).</summary>
    public List<(long Ts, double Avg, double Max)> Windows(string key, long from, long to)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.ts, s.avg, s.max FROM sample s JOIN metric m ON m.id = s.metric_id
            WHERE m.key = $key AND s.ts >= $from AND s.ts <= $to ORDER BY s.ts
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$from", from);
        command.Parameters.AddWithValue("$to", to);
        var list = new List<(long, double, double)>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) list.Add((reader.GetInt64(0), reader.GetDouble(1), reader.GetDouble(2)));
        return list;
    }

    /// <summary>Consommation de chaque application, fenêtre de 10 s par fenêtre (pour corréler avec des pics).</summary>
    public List<(long Ts, string Name, double Cpu, double Io, double Gpu)> ProcessWindows(long from, long to)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT ts, name, cpu, io_read + io_write, gpu FROM proc_sample WHERE ts >= $from AND ts <= $to";
        command.Parameters.AddWithValue("$from", from);
        command.Parameters.AddWithValue("$to", to);
        var list = new List<(long, string, double, double, double)>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) list.Add((reader.GetInt64(0), reader.GetString(1), reader.GetDouble(2), reader.GetDouble(3), reader.GetDouble(4)));
        return list;
    }

    /// <summary>Événements regroupés par titre (nombre et dernière occurrence).</summary>
    public List<(string Title, string Kind, int Count, long Last)> EventSummary(long from, long to)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT title, kind, COUNT(*), MAX(ts) FROM event WHERE ts >= $from AND ts <= $to
            GROUP BY title, kind ORDER BY COUNT(*) DESC, MAX(ts) DESC LIMIT 30
            """;
        command.Parameters.AddWithValue("$from", from);
        command.Parameters.AddWithValue("$to", to);
        var list = new List<(string, string, int, long)>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) list.Add((reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt64(3)));
        return list;
    }

    /// <summary>Moyenne et pic de chaque métrique sur une fenêtre de temps.</summary>
    public Dictionary<string, (double Avg, double Max, double Last)> MetricStats(long from, long to)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT m.key, AVG(s.avg), MAX(s.max),
                   (SELECT s2.avg FROM sample s2 WHERE s2.metric_id = m.id AND s2.ts >= $from AND s2.ts <= $to ORDER BY s2.ts DESC LIMIT 1)
            FROM sample s JOIN metric m ON m.id = s.metric_id
            WHERE s.ts >= $from AND s.ts <= $to
            GROUP BY m.id
            """;
        command.Parameters.AddWithValue("$from", from);
        command.Parameters.AddWithValue("$to", to);
        var stats = new Dictionary<string, (double, double, double)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            stats[reader.GetString(0)] = (reader.GetDouble(1), reader.GetDouble(2), reader.IsDBNull(3) ? reader.GetDouble(1) : reader.GetDouble(3));
        return stats;
    }

    /// <summary>Applications les plus gourmandes sur une fenêtre, moyennées sur toute la fenêtre.</summary>
    public List<ProcRow> TopProcesses(long from, long to)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            WITH windows AS (SELECT COUNT(DISTINCT ts) AS n FROM proc_sample WHERE ts >= $from AND ts <= $to)
            SELECT name, MAX(description), MAX(via),
                   SUM(cpu) / (SELECT n FROM windows), MAX(ram_mb), MAX(commit_mb),
                   SUM(io_read) / (SELECT n FROM windows), SUM(io_write) / (SELECT n FROM windows),
                   SUM(hard_faults) / (SELECT n FROM windows), SUM(gpu) / (SELECT n FROM windows),
                   MAX(vram_mb), MAX(count)
            FROM proc_sample WHERE ts >= $from AND ts <= $to
            GROUP BY name
            """;
        command.Parameters.AddWithValue("$from", from);
        command.Parameters.AddWithValue("$to", to);
        var rows = new List<ProcRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new ProcRow
            {
                Name = reader.GetString(0),
                Description = reader.IsDBNull(1) ? null : reader.GetString(1),
                Via = reader.IsDBNull(2) ? null : reader.GetString(2),
                Cpu = reader.GetDouble(3),
                RamMb = reader.GetDouble(4),
                CommitMb = reader.GetDouble(5),
                IoReadBps = reader.GetDouble(6),
                IoWriteBps = reader.GetDouble(7),
                HardFaultsPerSec = reader.GetDouble(8),
                Gpu = reader.GetDouble(9),
                VramMb = reader.GetDouble(10),
                Count = reader.GetInt32(11),
            });
        }

        return rows;
    }

    public int InsertEvents(IEnumerable<EventItem> events)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO event (ts, log, provider, event_id, level, kind, title, message, record_id)
            VALUES ($ts, $log, $provider, $eventId, $level, $kind, $title, $message, $recordId)
            """;
        var ts = command.Parameters.Add("$ts", SqliteType.Integer);
        var log = command.Parameters.Add("$log", SqliteType.Text);
        var provider = command.Parameters.Add("$provider", SqliteType.Text);
        var eventId = command.Parameters.Add("$eventId", SqliteType.Integer);
        var level = command.Parameters.Add("$level", SqliteType.Integer);
        var kind = command.Parameters.Add("$kind", SqliteType.Text);
        var title = command.Parameters.Add("$title", SqliteType.Text);
        var message = command.Parameters.Add("$message", SqliteType.Text);
        var recordId = command.Parameters.Add("$recordId", SqliteType.Integer);
        var inserted = 0;
        foreach (var e in events)
        {
            ts.Value = e.Ts;
            log.Value = e.Log;
            provider.Value = e.Provider;
            eventId.Value = e.EventId;
            level.Value = e.Level;
            kind.Value = e.Kind;
            title.Value = e.Title;
            message.Value = (object?)e.Message ?? DBNull.Value;
            recordId.Value = e.RecordId;
            inserted += command.ExecuteNonQuery();
        }

        transaction.Commit();
        return inserted;
    }

    public List<EventItem> Events(long from, long to, int limit)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ts, log, provider, event_id, level, kind, title, message, record_id
            FROM event WHERE ts >= $from AND ts <= $to ORDER BY ts DESC LIMIT $limit
            """;
        command.Parameters.AddWithValue("$from", from);
        command.Parameters.AddWithValue("$to", to);
        command.Parameters.AddWithValue("$limit", limit);
        var list = new List<EventItem>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new EventItem
            {
                Ts = reader.GetInt64(0),
                Log = reader.GetString(1),
                Provider = reader.GetString(2),
                EventId = reader.GetInt32(3),
                Level = reader.GetInt32(4),
                Kind = reader.GetString(5),
                Title = reader.GetString(6),
                Message = reader.IsDBNull(7) ? null : reader.GetString(7),
                RecordId = reader.GetInt64(8),
            });
        }

        return list;
    }

    public long InsertHang(long start, int pid, string process, string title)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO hang (start_ts, pid, process, title) VALUES ($start, $pid, $process, $title); SELECT last_insert_rowid();";
        command.Parameters.AddWithValue("$start", start);
        command.Parameters.AddWithValue("$pid", pid);
        command.Parameters.AddWithValue("$process", process);
        command.Parameters.AddWithValue("$title", title);
        return (long)command.ExecuteScalar()!;
    }

    public void EndHang(long id, long end)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE hang SET end_ts = $end WHERE id = $id";
        command.Parameters.AddWithValue("$end", end);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public List<HangItem> Hangs(long from, long to)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, start_ts, end_ts, pid, process, title FROM hang
            WHERE start_ts <= $to AND (end_ts IS NULL OR end_ts = -1 OR end_ts >= $from)
            ORDER BY start_ts DESC LIMIT 500
            """;
        command.Parameters.AddWithValue("$from", from);
        command.Parameters.AddWithValue("$to", to);
        var list = new List<HangItem>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            long? end = reader.IsDBNull(2) ? null : reader.GetInt64(2);
            list.Add(new HangItem(reader.GetInt64(0), reader.GetInt64(1), end is -1 ? null : end, reader.GetInt32(3), reader.GetString(4), reader.GetString(5)));
        }

        return list;
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
        command.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        command.ExecuteNonQuery();
    }

    /// <summary>Dernière fenêtre enregistrée avant un instant : jusqu'où l'agent tournait lors de son exécution précédente.</summary>
    public long? LastSampleBefore(long ts)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT MAX(ts) FROM sample WHERE ts < $ts";
        command.Parameters.AddWithValue("$ts", ts);
        return command.ExecuteScalar() is long value ? value : null;
    }

    /// <summary>Événements déjà importés depuis un instant, pour ne pas les relire au redémarrage.</summary>
    public HashSet<(string Log, long RecordId)> KnownEventRecords(long since)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT log, record_id FROM event WHERE ts >= $since";
        command.Parameters.AddWithValue("$since", since);
        var set = new HashSet<(string, long)>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) set.Add((reader.GetString(0), reader.GetInt64(1)));
        return set;
    }

    /// <summary>Note le démarrage de l'agent : l'activité qu'il provoque au lancement ne doit pas passer pour un problème.</summary>
    public void RecordAgentStart(long ts)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO agent_run (start_ts) VALUES ($ts)";
        command.Parameters.AddWithValue("$ts", ts);
        command.ExecuteNonQuery();
    }

    public List<long> AgentStarts(long from, long to)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT start_ts FROM agent_run WHERE start_ts >= $from AND start_ts <= $to ORDER BY start_ts";
        command.Parameters.AddWithValue("$from", from);
        command.Parameters.AddWithValue("$to", to);
        var list = new List<long>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) list.Add(reader.GetInt64(0));
        return list;
    }

    /// <summary>Plus ancienne mesure enregistrée (pour borner l'historique dans l'interface).</summary>
    public long? OldestSample()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT MIN(ts) FROM proc_sample";
        return command.ExecuteScalar() is long value ? value : null;
    }

    public void ApplyRetention(long samplesBefore, long eventsBefore)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM sample WHERE ts < $samples;
            DELETE FROM proc_sample WHERE ts < $samples;
            DELETE FROM hang WHERE start_ts < $events;
            DELETE FROM event WHERE ts < $events;
            """;
        command.Parameters.AddWithValue("$samples", samplesBefore);
        command.Parameters.AddWithValue("$events", eventsBefore);
        command.ExecuteNonQuery();
    }
}
