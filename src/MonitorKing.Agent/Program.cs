using System.Text.Json.Serialization;
using MonitorKing.Agent;

// MonitorKing — agent local, en lecture seule.
// Il observe le PC et sert le dashboard sur http://localhost uniquement : aucun port n'est ouvert sur le réseau,
// et aucune route ne permet d'agir sur la machine.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

var options = builder.Configuration.GetSection("MonitorKing").Get<AgentOptions>() ?? new AgentOptions();
builder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenLocalhost(options.Port));

builder.Services.AddSingleton(options);
builder.Services.AddSingleton<MachineInfo>();
builder.Services.AddSingleton(_ => new Database(AgentMachine.DatabasePath(options)));
builder.Services.AddSingleton<DiagnosisEngine>();
builder.Services.AddSingleton<ReportBuilder>();
builder.Services.AddSingleton<CollectorHost>();
builder.Services.AddSingleton<AgentMachine>();
builder.Services.AddSingleton<Privacy>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<CollectorHost>());
builder.Services.AddHostedService<EventLogService>();
builder.Services.AddHostedService<UploadService>();
builder.Services.AddHttpClient();
builder.Services.ConfigureHttpJsonOptions(json =>
{
    json.SerializerOptions.NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals;
    json.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-cache",
});
app.MapAgentApi();

app.Logger.LogInformation("MonitorKing (lecture seule) : http://localhost:{Port}", options.Port);
app.Run();
