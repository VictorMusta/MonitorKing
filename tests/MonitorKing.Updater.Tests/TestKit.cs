using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using MonitorKing.Updater;

namespace MonitorKing.Updater.Tests;

/// <summary>Clé de publication de test : jamais celle des vraies releases.</summary>
internal sealed class TestKey
{
    private readonly RSA _rsa = RSA.Create(3072);

    public TestKey()
    {
        // RSA.Create ne génère la clé qu'à son premier usage, et pas à l'abri de deux fils d'exécution : deux tests
        // lancés en même temps obtenaient chacun la leur, et la signature « de la bonne clé » était refusée de temps
        // en temps. On la génère donc ici, avant que quiconque s'en serve.
        var parameters = _rsa.ExportParameters(false);
        Signature = new UpdateSignature(Convert.ToBase64String(parameters.Modulus!), Convert.ToBase64String(parameters.Exponent!));
    }

    public static TestKey Publisher { get; } = new();
    public static TestKey Stranger { get; } = new();

    public UpdateSignature Signature { get; }

    public string Sign(byte[] signed)
    {
        lock (_rsa) return Convert.ToBase64String(_rsa.SignData(signed, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }
}

internal static class Release
{
    /// <summary>La version que porte l'assembly de tests, utilisé comme faux agent dans les archives.</summary>
    public static readonly Version Published = new(9, 9, 9);
    public const string PackageName = "MonitorKing-Agent-v9.9.9-win-x64.zip";

    public static byte[] Body(Version version, params (string Name, byte[] Content)[] files)
    {
        var text = new StringBuilder($"version={InstallLayout.Name(version)}\n");
        foreach (var (name, content) in files)
            text.Append($"{name}={content.Length}:{Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant()}\n");
        return Encoding.ASCII.GetBytes(text.ToString());
    }

    public static byte[] Manifest(byte[] body, TestKey? key = null) =>
        Encoding.ASCII.GetBytes($"signature={(key ?? TestKey.Publisher).Sign(body)}\n").Concat(body).ToArray();

    /// <summary>Archive telle que le script de publication la produit : tout dans un dossier « MonitorKing-Agent ».</summary>
    public static byte[] Package(char separator = '/', params (string Name, byte[] Content)[] extra)
    {
        var entries = new List<(string, byte[])>
        {
            ("MonitorKing-Agent/MonitorKing.Agent.exe", Encoding.ASCII.GetBytes("faux exécutable")),
            ("MonitorKing-Agent/MonitorKing.Agent.dll", File.ReadAllBytes(typeof(Release).Assembly.Location)),
            ("MonitorKing-Agent/wwwroot/index.html", Encoding.ASCII.GetBytes("<html></html>")),
        };
        entries.AddRange(extra);
        return Zip(entries.Select(e => (e.Item1.Replace('/', separator), e.Item2)).ToArray());
    }

    public static byte[] Zip(params (string Name, byte[] Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var stream = archive.CreateEntry(name).Open();
                stream.Write(content);
            }
        }

        return buffer.ToArray();
    }
}

/// <summary>Faux GitHub : mêmes adresses et mêmes redirections que les vraies releases, sans réseau.</summary>
internal sealed class FakeGitHub : HttpMessageHandler
{
    private const string Assets = "https://release-assets.githubusercontent.com/fake";

    public List<string> Requests { get; } = new();
    public Dictionary<string, Func<HttpResponseMessage>> Routes { get; } = new();

    public UpdateSource Source() => new(this);

    public bool Asked(string fragment) => Requests.Any(r => r.Contains(fragment, StringComparison.Ordinal));

    /// <summary>Publie une release : le manifeste sous « latest », puis chaque fichier sous son tag.</summary>
    public FakeGitHub Publish(Version version, byte[] manifest, byte[]? package = null, string packageName = Release.PackageName)
    {
        var tag = $"{UpdateSource.ReleasesUrl}/download/v{InstallLayout.Name(version)}";
        Redirect(UpdateSource.LatestManifestUrl, $"{tag}/{UpdateSource.ManifestName}");
        Redirect($"{tag}/{UpdateSource.ManifestName}", $"{Assets}/{UpdateSource.ManifestName}");
        Bytes($"{Assets}/{UpdateSource.ManifestName}", manifest);
        if (package is null) return this;

        Redirect($"{tag}/{packageName}", $"{Assets}/{packageName}");
        Bytes($"{Assets}/{packageName}", package);
        return this;
    }

    public void Redirect(string from, string to) => Routes[from] = () =>
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(to);
        return response;
    };

    public void Bytes(string url, byte[] content, long? announced = null) => Routes[url] = () =>
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new Unseekable(content)) };
        response.Content.Headers.ContentLength = announced;
        return response;
    };

    public static string AssetUrl(string name) => $"{Assets}/{name}";

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        Requests.Add(url);
        return Task.FromResult(Routes.TryGetValue(url, out var make) ? make() : new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    /// <summary>Flux sans longueur connue, comme une réponse réseau : la taille annoncée ne vient que de l'en-tête.</summary>
    private sealed class Unseekable : MemoryStream
    {
        public Unseekable(byte[] content) : base(content, writable: false)
        {
        }

        public override bool CanSeek => false;
    }
}

/// <summary>Un dossier d'installation jetable, avec l'updater branché sur le faux GitHub.</summary>
internal sealed class Sandbox : IDisposable
{
    public Sandbox(Version? running = null, Version? runningFromVersions = null)
    {
        Root = Path.Combine(Path.GetTempPath(), "monitorking-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Running = running ?? new Version(1, 0, 0);
        Layout = InstallLayout.Detect(runningFromVersions is null ? Root : Path.Combine(Root, ".update", "versions", InstallLayout.Name(runningFromVersions)));
        Log.Echo = Lines.Add;
    }

    public string Root { get; }
    public Version Running { get; }
    public InstallLayout Layout { get; }
    public FakeGitHub GitHub { get; } = new();
    public UpdateSettings Settings { get; } = new(null);
    public UpdateLog Log { get; } = new(null);
    public List<string> Lines { get; } = new();

    public AutoUpdater Updater(UpdateSignature? signature = null, bool allowed = true) =>
        new(Layout, Running, Settings, Log, signature ?? TestKey.Publisher.Signature, GitHub.Source) { DisabledByConfiguration = !allowed };

    /// <summary>Pose une version « installée » dans .update\versions, comme l'aurait fait une mise à jour.</summary>
    public string Install(Version version, bool withExe = true)
    {
        var directory = Layout.VersionDir(version);
        Directory.CreateDirectory(directory);
        if (withExe) File.WriteAllText(Path.Combine(directory, InstallLayout.AgentExe), "faux exécutable");
        return directory;
    }

    public void Mark(Version version, string kind, string content = "") => File.WriteAllText(Layout.Marker(version, kind), content);

    public bool Has(Version version, string kind) => File.Exists(Layout.Marker(version, kind));

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
