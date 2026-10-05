using System.Text.Json.Serialization;
using MonitorKing.Agent;

// MonitorKing — agent local, en lecture seule.
// Il observe le PC et sert le dashboard sur http://localhost uniquement : aucun port n'est ouvert sur le réseau,
// et aucune route ne permet d'agir sur la machine.

// La mise à jour passe avant tout le reste : si une version plus récente est déjà installée, elle prend le relais ici.
if (AgentUpdate.HandOver(args, out var exitCode)) return exitCode;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

var options = builder.Configuration.GetSection("MonitorKing").Get<AgentOptions>() ?? new AgentOptions();
var updater = AgentUpdate.Start(args, options.AutoUpdate);

try
{
    builder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenLocalhost(options.Port));

    builder.Services.AddSingleton(options);
    builder.Services.AddSingleton(updater);
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

    app.Lifetime.ApplicationStarted.Register(() => AgentUpdate.ConfirmWhenStable(updater, app.Lifetime.ApplicationStopping));
    app.Logger.LogInformation("MonitorKing (lecture seule) : http://localhost:{Port}", options.Port);
    app.Run();
    return 0;
}
catch (Exception failure) when (!builder.Environment.IsDevelopment())
{
    // Un agent installé qui ne démarre plus laisse à la mise à jour le temps de lui trouver un correctif.
    return AgentUpdate.WaitForFix(updater, args, failure);
}
