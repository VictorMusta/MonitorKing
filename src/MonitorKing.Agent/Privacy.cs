using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace MonitorKing.Agent;

/// <summary>
/// Ce qui peut quitter le PC. Mode « discret » par défaut :
/// - les composants de Windows (installés sous C:\Windows, ou noyau/Defender) restent lisibles : ils servent au diagnostic ;
/// - toute autre application devient « Appli 7F3A9C », un pseudonyme stable calculé avec une clé qui ne quitte jamais le PC ;
/// - les titres de fenêtres et les messages des événements Windows (chemins, noms d'utilisateur) ne sont jamais envoyés.
/// L'utilisateur du PC voit tout en local et peut retrouver une application à partir de son pseudonyme.
/// Il peut aussi, lui seul et depuis ce PC, tout partager pendant une durée limitée (mode « complet »).
/// </summary>
public sealed class Privacy
{
    private const string SecretKey = "privacy.secret";
    private const string FullUntilKey = "privacy.full_until";

    // Composants Windows situés hors de C:\Windows, ou sans chemin lisible (processus protégés du noyau).
    private static readonly HashSet<string> WindowsComponents = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Registry", "Secure System", "Memory Compression", "Idle",
        "MsMpEng.exe", "NisSrv.exe", "MpDefenderCoreService.exe", "MpCmdRun.exe",
    };

    private readonly Database _db;
    private readonly byte[] _secret;
    private Dictionary<string, bool> _catalog = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _catalogLoaded = DateTime.MinValue;
    private readonly ConcurrentDictionary<string, string> _pseudonyms = new(StringComparer.OrdinalIgnoreCase);

    public Privacy(Database db)
    {
        _db = db;
        _secret = LoadOrCreateSecret(db);
    }

    public long? FullUntil =>
        long.TryParse(_db.Get(FullUntilKey), out var until) && until > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() ? until : null;

    public string Mode => FullUntil is null ? "discret" : "complet";

    public bool SharesEverything => FullUntil is not null;

    public void ShareFullFor(TimeSpan duration) =>
        _db.Set(FullUntilKey, DateTimeOffset.UtcNow.Add(duration).ToUnixTimeMilliseconds().ToString());

    public void BackToDiscreet() => _db.Set(FullUntilKey, null);

    public string PseudonymOf(string name) => _pseudonyms.GetOrAdd(name, n =>
    {
        var hash = HMACSHA256.HashData(_secret, Encoding.UTF8.GetBytes(n.ToLowerInvariant()));
        return "Appli " + Convert.ToHexString(hash, 0, 3);
    });

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

        return new EventItem
        {
            Ts = e.Ts,
            Log = e.Log,
            Provider = e.Provider,
            EventId = e.EventId,
            Level = e.Level,
            Kind = e.Kind,
            Title = title,
            Message = null,
            RecordId = e.RecordId,
        };
    }

    public HangItem Outgoing(HangItem h) =>
        SharesEverything ? h : h with { Process = Outgoing(h.Process), Title = "" };

    private static byte[] LoadOrCreateSecret(Database db)
    {
        if (db.Get(SecretKey) is { } stored) return Convert.FromBase64String(stored);
        var secret = RandomNumberGenerator.GetBytes(32);
        db.Set(SecretKey, Convert.ToBase64String(secret));
        return secret;
    }
}
