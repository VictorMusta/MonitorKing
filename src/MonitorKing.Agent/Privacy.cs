using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MonitorKing.Core.Sync;

namespace MonitorKing.Agent;

/// <summary>
/// Ce qui peut quitter le PC. Mode « discret » par défaut :
/// - les composants de Windows (installés sous C:\Windows, ou noyau/Defender) restent lisibles : ils servent au diagnostic ;
/// - toute autre application devient « Appli 7F3A9C », un pseudonyme stable calculé avec une clé qui ne quitte jamais le PC ;
/// - les titres de fenêtres et les messages des événements Windows (chemins, noms d'utilisateur) ne sont jamais envoyés ;
///   d'une erreur de stockage, seul part le périphérique visé (numéro du disque ou lettre du volume).
/// L'utilisateur du PC voit tout en local et peut retrouver une application à partir de son pseudonyme.
/// Il peut aussi, lui seul et depuis ce PC, tout partager pendant une durée limitée (mode « complet »).
/// </summary>
public sealed class Privacy
{
    private const string SecretKey = "privacy.secret";
    private const string FullUntilKey = "privacy.full_until";
    private const string ReadKeyKey = "privacy.read_key";

    // Composants Windows situés hors de C:\Windows, ou sans chemin lisible (processus protégés du noyau).
    private static readonly HashSet<string> WindowsComponents = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Registry", "Secure System", "Memory Compression", "Idle",
        "MsMpEng.exe", "NisSrv.exe", "MpDefenderCoreService.exe", "MpCmdRun.exe",
    };

    private readonly Database _db;
    private readonly byte[] _secret;
    private byte[] _readKey;
    private Dictionary<string, bool> _catalog = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _catalogLoaded = DateTime.MinValue;
    private readonly ConcurrentDictionary<string, string> _pseudonyms = new(StringComparer.OrdinalIgnoreCase);
    // Pseudonyme → vrai nom, pour les noms chiffrés à envoyer ; et ceux déjà confirmés par le serveur.
    private readonly ConcurrentDictionary<string, string> _realNames = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _sealedSent = new(StringComparer.Ordinal);

    public Privacy(Database db)
    {
        _db = db;
        _secret = LoadOrCreateSecret(db, SecretKey);
        _readKey = LoadOrCreateSecret(db, ReadKeyKey);
    }

    /// <summary>
    /// Clé de lecture des noms : permet de déchiffrer, dans un navigateur, les vrais noms envoyés chiffrés.
    /// Elle ne part jamais vers le serveur ; c'est l'utilisateur du PC qui la donne (ou non) à qui il veut.
    /// </summary>
    public string ReadKey => Convert.ToBase64String(_readKey).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Nouvelle clé de lecture : les noms sont rechiffrés et l'ancienne clé ne sert plus à rien.</summary>
    public void RotateReadKey()
    {
        _readKey = RandomNumberGenerator.GetBytes(32);
        _db.Set(ReadKeyKey, Convert.ToBase64String(_readKey));
        _sealedSent.Clear();
    }

    /// <summary>Noms chiffrés pas encore confirmés par le serveur (pseudonyme → nom et description, en AES-GCM).</summary>
    public List<SealedName> PendingSeals()
    {
        var pending = _realNames.Where(kv => !_sealedSent.ContainsKey(kv.Key)).ToList();
        if (pending.Count == 0) return new List<SealedName>();
        var descriptions = _db.Apps().ToDictionary(a => a.Name, a => a.Description, StringComparer.OrdinalIgnoreCase);
        return pending
            .Select(kv => new SealedName(kv.Key, Seal(JsonSerializer.Serialize(new { n = kv.Value, d = descriptions.GetValueOrDefault(kv.Value) }))))
            .ToList();
    }

    public void ConfirmSeals(IEnumerable<SealedName> sent)
    {
        foreach (var s in sent) _sealedSent[s.Pseudonym] = true;
    }

    /// <summary>AES-256-GCM : nonce (12 octets) + texte chiffré + étiquette (16 octets), en base64.</summary>
    private string Seal(string plaintext)
    {
        var data = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[data.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(_readKey, 16)) aes.Encrypt(nonce, data, cipher, tag);
        return Convert.ToBase64String([.. nonce, .. cipher, .. tag]);
    }

    public long? FullUntil =>
        long.TryParse(_db.Get(FullUntilKey), out var until) && until > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() ? until : null;

    public string Mode => FullUntil is null ? "discret" : "complet";

    public bool SharesEverything => FullUntil is not null;

    public void ShareFullFor(TimeSpan duration) =>
        _db.Set(FullUntilKey, DateTimeOffset.UtcNow.Add(duration).ToUnixTimeMilliseconds().ToString());

    public void BackToDiscreet() => _db.Set(FullUntilKey, null);

    public string PseudonymOf(string name)
    {
        var pseudonym = _pseudonyms.GetOrAdd(name, n =>
        {
            var hash = HMACSHA256.HashData(_secret, Encoding.UTF8.GetBytes(n.ToLowerInvariant()));
            return "Appli " + Convert.ToHexString(hash, 0, 3);
        });
        _realNames.TryAdd(pseudonym, name);
        return pseudonym;
    }

    public bool IsWindowsComponent(string name)
    {
        if (name.StartsWith("svchost.exe", StringComparison.OrdinalIgnoreCase)) return true;
        if (WindowsComponents.Contains(name)) return true;
        if (DateTime.UtcNow - _catalogLoaded > TimeSpan.FromMinutes(1))
        {
            _catalog = _db.Apps().ToDictionary(a => a.Name, a => a.System, StringComparer.OrdinalIgnoreCase);
            _catalogLoaded = DateTime.UtcNow;
        }

        return _catalog.TryGetValue(name, out var system) && system;
    }

    /// <summary>Nom d'application tel qu'il peut être envoyé.</summary>
    public string Outgoing(string name) => SharesEverything || IsWindowsComponent(name) ? name : PseudonymOf(name);

    public ProcRow Outgoing(ProcRow row)
    {
        if (SharesEverything || IsWindowsComponent(row.Name)) return row;
        return new ProcRow
        {
            Name = PseudonymOf(row.Name),
            Description = null,
            Via = row.Via,
            Cpu = row.Cpu,
            RamMb = row.RamMb,
            CommitMb = row.CommitMb,
            IoReadBps = row.IoReadBps,
            IoWriteBps = row.IoWriteBps,
            HardFaultsPerSec = row.HardFaultsPerSec,
            Gpu = row.Gpu,
            VramMb = row.VramMb,
            NetSendBps = row.NetSendBps,
            NetRecvBps = row.NetRecvBps,
            Count = row.Count,
        };
    }

    public EventItem Outgoing(EventItem e)
    {
        if (SharesEverything) return e;
        var title = e.Title;
        // Les titres de plantage, de gel et de service se terminent par un nom d'application ou de service.
        if (e.Kind is "crash" or "hang" or "service" && title.LastIndexOf(" : ", StringComparison.Ordinal) is var cut and > 0)
        {
            var name = title[(cut + 3)..];
            var safe = e.Kind == "service" ? PseudonymOf(name) : Outgoing(name);
            title = title[..(cut + 3)] + safe;
        }

        return e.WithoutMessage(title);
    }

    public HangItem Outgoing(HangItem h) =>
        SharesEverything ? h : h with { Process = Outgoing(h.Process), Title = "" };

    private static byte[] LoadOrCreateSecret(Database db, string key)
    {
        if (db.Get(key) is { } stored) return Convert.FromBase64String(stored);
        var secret = RandomNumberGenerator.GetBytes(32);
        db.Set(key, Convert.ToBase64String(secret));
        return secret;
    }
}
