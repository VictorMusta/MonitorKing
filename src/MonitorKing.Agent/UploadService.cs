using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MonitorKing.Core.Sync;

namespace MonitorKing.Agent;

/// <summary>
/// Envoie les mesures au serveur central (store-and-forward) : tout reste d'abord dans la base locale,
/// puis part par lots compressés de 5 minutes, avec un curseur. Si le PC est hors ligne, l'envoi reprend
/// là où il s'était arrêté. La connexion part toujours du PC ; le serveur ne peut rien lui demander.
/// </summary>
public sealed class UploadService : BackgroundService
{
    public const string TokenKey = "server.token";
    public const string ServerUrlKey = "server.url";
    public const string MachineIdKey = "server.machine_id";
    public const string LabelKey = "server.label";
    public const string LastUploadKey = "upload.last";
    public const string LastErrorKey = "upload.error";
    private const string SamplesCursorKey = "upload.cursor.samples";
    private const string EventsCursorKey = "upload.cursor.events";
    private const string HangsCursorKey = "upload.cursor.hangs";
    private const int WindowsPerBatch = 30;
    private const int EventsPerBatch = 200;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly AgentOptions _options;
    private readonly Database _db;
    private readonly Privacy _privacy;
    private readonly AgentMachine _machine;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<UploadService> _logger;

    public UploadService(AgentOptions options, Database db, Privacy privacy, AgentMachine machine, IHttpClientFactory http, ILogger<UploadService> logger)
    {
        _options = options;
        _db = db;
        _privacy = privacy;
        _machine = machine;
        _http = http;
        _logger = logger;
    }

    /// <summary>Adresse du serveur : celle de la configuration, sinon celle mémorisée lors de l'inscription.</summary>
    public static string? ServerUrl(AgentOptions options, Database db) =>
        string.IsNullOrWhiteSpace(options.Server.Url) ? db.Get(ServerUrlKey) : options.Server.Url.TrimEnd('/');

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (ServerUrl(_options, _db) is not { } server)
        {
            _logger.LogInformation("Pas de serveur configuré : les données restent sur ce PC.");
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(5, _options.Server.UploadIntervalSeconds));
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); // laisser l'agent enregistrer ses premières fenêtres

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var token = await EnsureEnrolledAsync(server, stoppingToken);
                if (token is not null)
                {
                    // Rattrapage : on enchaîne les lots tant qu'il reste des données, dans la limite de 20 par cycle.
                    for (var i = 0; i < 20 && await SendBatchAsync(server, token, stoppingToken); i++)
                    {
                    }

                    _db.Set(LastUploadKey, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString());
                    _db.Set(LastErrorKey, null);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _db.Set(LastErrorKey, ex.Message);
                _logger.LogWarning("Envoi au serveur impossible : {Message}", ex.Message);
            }

            await Task.Delay(interval, stoppingToken);
        }
    }

    private async Task<string?> EnsureEnrolledAsync(string server, CancellationToken token)
    {
        if (_db.Get(TokenKey) is { } existing) return existing;
        if (string.IsNullOrWhiteSpace(_options.Server.EnrollmentCode))
            throw new InvalidOperationException("Machine non inscrite : renseigne MonitorKing:Server:EnrollmentCode.");

        using var client = _http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        var response = await client.PostAsJsonAsync($"{server}/enroll",
            new EnrollRequest(_options.Server.EnrollmentCode.Trim(), _machine.Summary.AgentVersion), Json, token);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Inscription refusée ({(int)response.StatusCode}) : code invalide ou déjà utilisé.");

        var enrolled = await response.Content.ReadFromJsonAsync<EnrollResponse>(Json, token)
            ?? throw new InvalidOperationException("Réponse d'inscription vide.");
        _db.Set(MachineIdKey, enrolled.MachineId);
        _db.Set(LabelKey, enrolled.Label);
        _db.Set(TokenKey, enrolled.Token);
        _db.Set(ServerUrlKey, server); // les lancements suivants n'auront plus besoin de l'adresse
        // Premier envoi : les dernières 24 h, pas tout l'historique local.
        _db.Set(SamplesCursorKey, DateTimeOffset.UtcNow.AddHours(-24).ToUnixTimeMilliseconds().ToString());
        _logger.LogInformation("Machine inscrite sur {Server} sous le nom « {Label} »", server, enrolled.Label);
        return enrolled.Token;
    }

    /// <summary>Envoie un lot ; renvoie vrai s'il reste probablement des données à envoyer.</summary>
    private async Task<bool> SendBatchAsync(string server, string bearer, CancellationToken token)
    {
        var samplesCursor = Cursor(SamplesCursorKey);
        var eventsCursor = Cursor(EventsCursorKey);
        var hangsCursor = Cursor(HangsCursorKey);

        var windows = _db.WindowsAfter(samplesCursor, WindowsPerBatch);
        var upTo = windows.Count > 0 ? windows[^1] : samplesCursor;
        var samples = windows.Count > 0 ? _db.SamplesBetween(samplesCursor, upTo) : new();
        var processes = windows.Count > 0 ? _db.ProcessRowsBetween(samplesCursor, upTo) : new();
        var events = _db.EventsAfterId(eventsCursor, EventsPerBatch);
        var hangs = _db.HangsChangedSince(hangsCursor);

        var keys = samples.Select(s => s.Key).ToHashSet();
        var definitions = _machine.Definitions.Where(d => keys.Contains(d.Key)).ToList();
        var summary = _machine.Summary;
        if (!_privacy.SharesEverything)
            summary = summary with { Name = _db.Get(LabelKey) ?? "PC" };

        var outgoingProcesses = processes.Select(p => new ProcessDto(p.Ts, _privacy.Outgoing(p.Row))).ToList();
        var outgoingEvents = events.Select(e => _privacy.Outgoing(e.Event)).ToList();
        var outgoingHangs = hangs.Select(_privacy.Outgoing).ToList();
        // Les pseudonymes produits ci-dessus partent aussi sous forme chiffrée (lisibles seulement avec la clé du PC).
        var seals = _privacy.PendingSeals();
        if (windows.Count == 0 && events.Count == 0 && hangs.Count == 0 && seals.Count == 0) return false;

        var batch = new UploadBatch(
            1,
            _privacy.Mode,
            summary,
            definitions,
            samples.Select(s => new SampleDto(s.Key, s.Ts, s.Avg, s.Max)).ToList(),
            outgoingProcesses,
            outgoingEvents,
            outgoingHangs,
            seals);

        using var client = _http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(60);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{server}/ingest/v1/batch")
        {
            Content = Gzip(JsonSerializer.SerializeToUtf8Bytes(batch, Json)),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        using var response = await client.SendAsync(request, token);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            // Jeton révoqué côté serveur : il faudra un nouveau code d'inscription.
            _db.Set(TokenKey, null);
            throw new InvalidOperationException("Jeton refusé par le serveur : machine à réinscrire.");
        }

        response.EnsureSuccessStatusCode();

        _privacy.ConfirmSeals(seals);
        _db.Set(SamplesCursorKey, upTo.ToString());
        if (events.Count > 0) _db.Set(EventsCursorKey, events[^1].Id.ToString());
        if (hangs.Count > 0) _db.Set(HangsCursorKey, hangs.Max(h => Math.Max(h.Start, h.End ?? h.Start)).ToString());
        return windows.Count == WindowsPerBatch || events.Count == EventsPerBatch;
    }

    private long Cursor(string key) => long.TryParse(_db.Get(key), out var value) ? value : 0;

    private static ByteArrayContent Gzip(byte[] json)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true))
            gzip.Write(json);
        var content = new ByteArrayContent(buffer.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Headers.ContentEncoding.Add("gzip");
        return content;
    }
}
