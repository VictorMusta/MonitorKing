using System.Text.Json.Serialization;
using Microsoft.AspNetCore.RateLimiting;
using MonitorKing.Core.Diagnosis;
using MonitorKing.Core.Reports;
using MonitorKing.Server;

// MonitorKing — serveur central. À placer derrière un reverse proxy HTTPS (Caddy) qui protège le dashboard
// par mot de passe et laisse passer seulement /enroll et /ingest vers les agents (authentifiés par jeton).
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

var options = builder.Configuration.GetSection("MonitorKing").Get<ServerOptions>() ?? new ServerOptions();
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.ListenAnyIP(options.Port);
    kestrel.Limits.MaxRequestBodySize = 32 * 1024 * 1024;
});

builder.Services.AddSingleton(options);
builder.Services.AddSingleton<ServerStore>();
builder.Services.AddSingleton<DiagnosisEngine>();
builder.Services.AddSingleton<ReportBuilder>();
builder.Services.AddHostedService<RetentionService>();
builder.Services.AddRequestDecompression();
builder.Services.AddRateLimiter(limiter =>
{
    limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    limiter.AddFixedWindowLimiter("enroll", window =>
    {
        window.PermitLimit = 10;
        window.Window = TimeSpan.FromMinutes(1);
        window.QueueLimit = 0;
    });
});
builder.Services.ConfigureHttpJsonOptions(json =>
{
    json.SerializerOptions.NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals;
    json.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

var app = builder.Build();

app.UseRequestDecompression();
app.UseRateLimiter();
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-cache",
});
app.MapAgentEndpoints();
app.MapDashboardApi();

app.Logger.LogInformation("MonitorKing serveur : port {Port}, données dans {Data}", options.Port, options.DataDirectory);
app.Run();
