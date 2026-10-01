using System.Text.Json;
using MonitorKing.Agent.Diagnosis;
using MonitorKing.Agent.Reports;
using MonitorKing.Agent.Storage;

namespace MonitorKing.Agent;

/// <summary>
/// API locale du dashboard. Toutes les routes sont en lecture, sauf l'enregistrement de la disposition
/// de l'écran d'accueil (qui ne touche que la base de l'agent). Aucune route n'agit sur le PC.
/// </summary>
public static class Api
{
    private const int LiveMinutesMax = 30;

    public static void MapAgentApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/info", (MachineInfo info, Database db, AgentOptions options) => new
        {
            info.MachineName,
            info.Os,
            info.Cpu,
            info.LogicalCores,
            info.RamGb,
            info.Gpus,
            info.Administrator,
            info.AgentVersion,
            info.AgentStarted,
            info.BootTime,
            DataPath = db.Path,
            OldestSample = db.OldestSample(),
            options.SampleIntervalMs,
            options.RetentionDays,
            ReadOnly = true,
        });

        api.MapGet("/status", (CollectorHost host) => host.Status);

        api.MapGet("/metrics", (CollectorHost host) => host.Definitions);

        api.MapGet("/live", (CollectorHost host) =>
            host.Latest is { } snapshot ? Results.Ok(snapshot) : Results.Problem("Premier échantillon en cours…", statusCode: 503));

        api.MapGet("/series", (string keys, int? minutes, long? from, long? to, int? points, CollectorHost host, Database db) =>
        {
            var list = keys.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().Take(24).ToList();
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (minutes is { } m && m <= LiveMinutesMax)
            {
                var start = now - Math.Max(1, m) * 60_000L;
                return Results.Ok(new { From = start, To = now, Live = true, Series = host.LiveSeries(list, start) });
            }

            var end = to ?? now;
            var begin = from ?? end - (minutes ?? 60) * 60_000L;
            var series = list.ToDictionary(k => k, k => db.Series(k, begin, end, Math.Clamp(points ?? 600, 10, 2000)));
            return Results.Ok(new { From = begin, To = end, Live = false, Series = series });
        });

        api.MapGet("/processes", (long from, long to, Database db) =>
            db.TopProcesses(from, to).OrderByDescending(p => p.Cpu).ToList());

        api.MapGet("/events", (long? from, long? to, int? limit, Database db) =>
        {
            var end = to ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var begin = from ?? end - 30L * 24 * 3600_000;
            return new
            {
                Events = db.Events(begin, end, Math.Clamp(limit ?? 300, 1, 2000)),
                Hangs = db.Hangs(begin, end),
            };
        });

        api.MapGet("/diagnosis", (int? minutes, long? from, long? to, CollectorHost host, Database db, MachineInfo info, DiagnosisEngine engine) =>
        {
            var window = from is { } begin && to is { } end && end > begin
                ? WindowFactory.History(begin, end, db, host, info)
                : WindowFactory.Live(minutes ?? 2, db, host, info);
            return engine.Analyze(window);
        });

        // Rapport complet d'une période, en Markdown, pensé pour être lu par Claude (ou un humain).
        api.MapGet("/report", (int? minutes, long? from, long? to, ReportBuilder reports) =>
        {
            var end = to ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var begin = from ?? end - Math.Clamp(minutes ?? 60, 1, 7 * 24 * 60) * 60_000L;
            if (end <= begin) return Results.BadRequest("Période vide");
            return Results.Text(reports.Build(begin, end), "text/markdown; charset=utf-8");
        });

        api.MapGet("/layouts/{id}", (string id, Database db) =>
            db.GetLayout(id) is { } json ? Results.Content(json, "application/json") : Results.NotFound());

        api.MapPut("/layouts/{id}", async (string id, HttpRequest request, Database db) =>
        {
            if (id.Length > 64) return Results.BadRequest();
            if (!(request.ContentType ?? "").StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
                return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
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

            db.SaveLayout(id, body);
            return Results.NoContent();
        });
    }
}
