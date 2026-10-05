using System.Net;

namespace MonitorKing.Updater;

/// <summary>Échec attendu d'une étape de la mise à jour : son message finit tel quel dans le journal.</summary>
public sealed class UpdateException : Exception
{
    public UpdateException(string message) : base(message)
    {
    }
}

/// <summary>
/// Les seules adresses contactées : les releases GitHub du projet, et l'hébergement de fichiers vers lequel
/// GitHub redirige les téléchargements. HTTPS uniquement, certificats validés normalement, tailles plafonnées.
/// La requête ne porte aucun identifiant ni aucune information sur le PC.
/// </summary>
public sealed class UpdateSource : IDisposable
{
    public const string ReleasesUrl = "https://github.com/VictorMusta/MonitorKing/releases";
    public const string ManifestName = "update-manifest.txt";
    private const int MaxRedirects = 5;

    private readonly HttpClient _http;

    /// <param name="handler">Transport de remplacement, pour les tests.</param>
    public UpdateSource(HttpMessageHandler? handler = null)
    {
        // Redirections suivies à la main, pour vérifier chaque adresse avant de la contacter.
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(10) })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("MonitorKing");
    }

    /// <summary>Le manifeste de la dernière version publiée.</summary>
    public static string LatestManifestUrl => $"{ReleasesUrl}/latest/download/{ManifestName}";

    /// <summary>Un fichier de la version annoncée par le manifeste, jamais de « la dernière » : pas de mélange de versions.</summary>
    public static string FileUrl(Version version, string name) => $"{ReleasesUrl}/download/v{InstallLayout.Name(version)}/{name}";

    public static bool IsAllowed(Uri address) =>
        address.Scheme == Uri.UriSchemeHttps
        && (address.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || address.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Télécharge en mémoire : rien n'est écrit sur le disque avant les vérifications.
    /// </summary>
    /// <param name="maxBytes">Plafond : au-delà, le téléchargement est abandonné.</param>
    /// <param name="exactBytes">Taille attendue, quand le manifeste l'annonce : toute autre taille est refusée.</param>
    public async Task<byte[]> DownloadAsync(string url, long maxBytes, long? exactBytes, TimeSpan timeout, CancellationToken stop)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(stop);
        limit.CancelAfter(timeout);
        try
        {
            var address = new Uri(url);
            for (var hop = 0; ; hop++)
            {
                if (!IsAllowed(address)) throw new UpdateException($"adresse refusée ({address.Scheme}://{address.Host})");
                using var response = await _http.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);

                if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
                    or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                {
                    if (hop >= MaxRedirects || response.Headers.Location is not { } location) throw new UpdateException("trop de redirections");
                    address = location.IsAbsoluteUri ? location : new Uri(address, location);
                    continue;
                }

                if (response.StatusCode != HttpStatusCode.OK) throw new UpdateException($"réponse {(int)response.StatusCode}");

                var announced = response.Content.Headers.ContentLength;
                if (announced > maxBytes || (exactBytes is { } wanted && announced is { } given && given != wanted))
                    throw new UpdateException("taille annoncée inattendue");

                await using var stream = await response.Content.ReadAsStreamAsync(limit.Token).ConfigureAwait(false);
                return exactBytes is { } exact
                    ? await ReadExactlyAsync(stream, exact, limit.Token).ConfigureAwait(false)
                    : await ReadUpToAsync(stream, maxBytes, limit.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested)
        {
            throw new UpdateException("délai dépassé");
        }
        catch (Exception e) when (e is HttpRequestException or IOException)
        {
            throw new UpdateException(e.Message);
        }
    }

    private static async Task<byte[]> ReadExactlyAsync(Stream stream, long length, CancellationToken stop)
    {
        var bytes = new byte[length];
        var read = 0;
        while (read < bytes.Length)
        {
            var count = await stream.ReadAsync(bytes.AsMemory(read), stop).ConfigureAwait(false);
            if (count == 0) throw new UpdateException("téléchargement incomplet");
            read += count;
        }

        // Rien ne doit suivre la taille annoncée par le manifeste.
        if (await stream.ReadAsync(new byte[1], stop).ConfigureAwait(false) != 0) throw new UpdateException("fichier plus gros qu'annoncé");
        return bytes;
    }

    private static async Task<byte[]> ReadUpToAsync(Stream stream, long maxBytes, CancellationToken stop)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int count;
        while ((count = await stream.ReadAsync(chunk, stop).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + count > maxBytes) throw new UpdateException("réponse trop grosse");
            buffer.Write(chunk, 0, count);
        }

        return buffer.ToArray();
    }

    public void Dispose() => _http.Dispose();
}
