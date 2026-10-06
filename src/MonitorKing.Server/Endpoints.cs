using System.Text.Json;
using MonitorKing.Core;
using MonitorKing.Core.Diagnosis;
using MonitorKing.Core.Reports;
using MonitorKing.Core.Sync;
using MonitorKing.Core.Web;

namespace MonitorKing.Server;

public static class Endpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>Routes appelées par les agents (laissées publiques par Caddy, protégées par code ou jeton).</summary>
    public static void MapAgentEndpoints(this WebApplication app)
    {
        app.MapPost("/enroll", (EnrollRequest request, ServerStore store, ILogger<ServerStore> logger) =>
        {
            if (store.Enroll(request.Code) is not { } enrolled) return Results.StatusCode(StatusCodes.Status403Forbidden);
            logger.LogInformation("Nouvelle machine inscrite : {Label} ({Id})", enrolled.Label, enrolled.MachineId);
            return Results.Ok(new EnrollResponse(enrolled.MachineId, enrolled.Token, enrolled.Label));
        }).RequireRateLimiting("enroll");

        app.MapPost("/ingest/v1/batch", async (HttpRequest request, ServerStore store) =>
        {
            var authorization = request.Headers.Authorization.ToString();
            if (!authorization.StartsWith("Bearer ", StringComparison.Ordinal)) return Results.Unauthorized();
            if (store.MachineIdForToken(authorization[7..].Trim()) is not { } machineId) return Results.Unauthorized();

            UploadBatch? batch;
            try
            {
                batch = await JsonSerializer.DeserializeAsync<UploadBatch>(request.Body, Json);
            }
            catch (JsonException)
            {
                return Results.BadRequest("Lot illisible");
            }

            if (batch is null || batch.Version != 1) return Results.BadRequest("Version de lot inconnue");

            store.DatabaseFor(machineId).Import(
                batch.Definitions,
                batch.Samples.Select(s => (s.Key, s.Ts, s.Avg, s.Max)).ToList(),
                batch.Processes.Select(p => (p.Ts, p.Row)).ToList(),
                batch.Events,
                batch.Hangs,
                batch.SealedNames?
                    .Where(s => s.Pseudonym.Length <= 32 && s.Sealed.Length <= 4096)
                    .Select(s => (s.Pseudonym, s.Sealed))
                    .ToList());
            store.Touch(machineId, batch.Mode, batch.Summary);
            return Results.Ok(new { Received = batch.Samples.Count });
        });
    }

    /// <summary>API du dashboard (derrière le mot de passe de Caddy) : la liste des PC, puis une API par PC.</summary>
    public static void MapDashboardApi(this WebApplication app)
    {
        // Le navigateur joint le mot de passe mémorisé à toute requête vers ce site, même lancée par la page d'un autre :
        // les écritures ne sont acceptées que du dashboard lui-même (ou d'un outil sans navigateur, comme curl).
        var api = app.MapGroup("/api").RefuseCrossSiteWrites();

        api.MapGet("/mode", () => new { Mode = "server" });

        api.MapGet("/machines", (ServerStore store, DiagnosisEngine engine) => store.Machines().Select(record =>
        {
            DiagnosisResult? diagnosis = null;
            if (record.LastSeen is { } seen)
                diagnosis = engine.Analyze(ServerLive.Window(Open(store, record), 15, seen));
            return new
            {
                record.Id,
                record.Label,
                record.LastSeen,
                Online = record.LastSeen is { } last && Now - last < 2 * 60_000,
                record.Mode,
                record.Summary?.Os,
                record.Summary?.Cpu,
                record.Summary?.RamGb,
                diagnosis?.Severity,
                diagnosis?.Verdict,
            };
        }).ToList());

        api.MapPost("/admin/enrollments", (EnrollmentRequest request, ServerStore store) =>
        {
            var label = (request.Label ?? "").Trim();
            if (label.Length is 0 or > 60) return Results.BadRequest("Donne un nom au PC (60 caractères au plus).");
            var (code, expires) = store.CreateEnrollment(label);
            return Results.Ok(new { Code = code, Expires = expires, Label = label });
        });

        api.MapPost("/admin/machines/{id}/revoke", (string id, ServerStore store) =>
        {
            store.Revoke(id);
            return Results.NoContent();
        });

        api.MapPut("/admin/machines/{id}/label", (string id, EnrollmentRequest request, ServerStore store) =>
        {
            var label = (request.Label ?? "").Trim();
            if (label.Length is 0 or > 60) return Results.BadRequest();
            store.RenameMachine(id, label);
            return Results.NoContent();
        });

        api.MapGet("/layouts/{id}", (string id, ServerStore store) =>
            store.GetLayout(id) is { } json ? Results.Content(json, "application/json") : Results.NotFound());

        api.MapPut("/layouts/{id}", async (string id, HttpRequest request, ServerStore store) =>
        {
            if (id.Length > 64) return Results.BadRequest();
            using var reader = new StreamReader(request.Body);
            var body = await reader.ReadToEndAsync();
            if (body.Length > 256 * 1024) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            try
            {
                using var _ = JsonDocument.Parse(body);
            }
            catch (JsonException)
            {
                return Results.BadRequest("JSON invalide");
            }

            store.SaveLayout(id, body);
            return Results.NoContent();
        });

        // ---- Une machine : mêmes routes que l'agent, préfixées par /api/m/{id}.
        var machine = api.MapGroup("/m/{id}");

        machine.MapGet("/info", (string id, ServerStore store, ServerOptions options) =>
        {
            if (store.Machine(id) is not { } record) return Results.NotFound();
            var m = Open(store, record);
            var s = m.Summary;
            return Results.Ok(new
            {
                MachineId = record.Id,
                MachineName = record.Label,
                s.Os,
                s.Cpu,
                s.LogicalCores,
                s.RamGb,
                s.Gpus,
                s.Administrator,
                s.AgentVersion,
                s.BootTime,
                DataPath = $"serveur · machines/{record.Id}.db",
                OldestSample = m.Database.OldestSample(),
                s.SampleIntervalMs,
                options.RetentionDays,
                ReadOnly = true,
                record.LastSeen,
                PrivacyMode = record.Mode,
            });
        });

        machine.MapGet("/status", () => Array.Empty<CollectorStatus>());

        // Noms chiffrés : le navigateur les déchiffre lui-même avec la clé de lecture fournie par la personne du PC.
        machine.MapGet("/sealed-names", (string id, ServerStore store) =>
            store.Machine(id) is null
                ? Results.NotFound()
                : Results.Ok(store.DatabaseFor(id).SealedNames().Select(s => new { s.Pseudonym, s.Sealed }).ToList()));

        machine.MapGet("/metrics", (string id, ServerStore store) =>
            store.Machine(id) is { } record ? Results.Ok(Open(store, record).Definitions) : Results.NotFound());

        machine.MapGet("/live", (string id, ServerStore store) =>
        {
            if (store.Machine(id) is not { } record) return Results.NotFound();
            return ServerLive.Snapshot(Open(store, record)) is { } snapshot
                ? Results.Ok(snapshot)
                : Results.Problem("Aucune donnée reçue de ce PC pour l'instant.", statusCode: 503);
        });

        machine.MapGet("/series", (string id, string keys, int? minutes, long? from, long? to, int? points, ServerStore store) =>
        {
            if (store.Machine(id) is null) return Results.NotFound();
            var db = store.DatabaseFor(id);
            var list = keys.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().Take(24).ToList();
            var end = to ?? Now;
            var begin = from ?? end - (minutes ?? 60) * 60_000L;
            var series = list.ToDictionary(k => k, k => db.Series(k, begin, end, Math.Clamp(points ?? 600, 10, 2000)));
            return Results.Ok(new { From = begin, To = end, Live = false, Series = series });
        });

        machine.MapGet("/processes", (string id, long from, long to, ServerStore store) =>
            store.Machine(id) is null ? Results.NotFound() : Results.Ok(store.DatabaseFor(id).TopProcesses(from, to).OrderByDescending(p => p.Cpu).ToList()));

        machine.MapGet("/breakdown", (string id, string resource, int? minutes, long? from, long? to, int? points, ServerStore store) =>
        {
            if (store.Machine(id) is null) return Results.NotFound();
            if (!Breakdown.IsKnown(resource)) return Results.BadRequest("Ressource inconnue");
            var end = to ?? Now;
            var begin = from ?? end - Math.Clamp(minutes ?? 60, 5, 31 * 24 * 60) * 60_000L;
            if (end <= begin) return Results.BadRequest("Période vide");
            return Results.Ok(Breakdown.Build(store.DatabaseFor(id), resource, begin, end, Math.Clamp(points ?? 240, 20, 600)));
        });

        machine.MapGet("/events", (string id, long? from, long? to, int? limit, ServerStore store) =>
        {
            if (store.Machine(id) is null) return Results.NotFound();
            var db = store.DatabaseFor(id);
            var end = to ?? Now;
            var begin = from ?? end - 30L * 24 * 3600_000;
            return Results.Ok(new { Events = db.Events(begin, end, Math.Clamp(limit ?? 300, 1, 2000)), Hangs = db.Hangs(begin, end) });
        });

        machine.MapGet("/diagnosis", (string id, int? minutes, long? from, long? to, ServerStore store, DiagnosisEngine engine) =>
        {
            if (store.Machine(id) is not { } record) return Results.NotFound();
            var m = Open(store, record);
            var window = from is { } begin && to is { } end && end > begin
                ? WindowFactory.History(m, begin, end)
                : ServerLive.Window(m, Math.Max(minutes ?? 5, 5), record.LastSeen ?? Now);
            return Results.Ok(engine.Analyze(window));
        });

        machine.MapGet("/report", (string id, int? minutes, long? from, long? to, ServerStore store, ReportBuilder reports) =>
        {
            if (store.Machine(id) is not { } record) return Results.NotFound();
            var end = to ?? Now;
            var begin = from ?? end - Math.Clamp(minutes ?? 60, 1, 7 * 24 * 60) * 60_000L;
            if (end <= begin) return Results.BadRequest("Période vide");
            return Results.Text(reports.Build(Open(store, record), begin, end), "text/markdown; charset=utf-8");
        });
    }

    private static ServerMachine Open(ServerStore store, MachineRecord record) => new(record, store.DatabaseFor(record.Id));
}

public sealed record EnrollmentRequest(string? Label);

/// <summary>Nettoyage quotidien : on ne garde que les jours configurés (et 90 jours d'événements).</summary>
public sealed class RetentionService : BackgroundService
{
    private readonly ServerStore _store;
    private readonly ServerOptions _options;

    public RetentionService(ServerStore store, ServerOptions options)
    {
        _store = store;
        _options = options;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(6));
        do
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var machine in _store.Machines())
            {
                _store.DatabaseFor(machine.Id).ApplyRetention(
                    now.AddDays(-_options.RetentionDays).ToUnixTimeMilliseconds(),
                    now.AddDays(-90).ToUnixTimeMilliseconds());
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
