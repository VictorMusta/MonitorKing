using System.Text;
using MonitorKing.Agent.Native;
using MonitorKing.Agent.Storage;

namespace MonitorKing.Agent.Collectors;

/// <summary>
/// Détecte les fenêtres « Ne répond pas » (IsHungAppWindow : plus de 5 s sans traiter de message)
/// et enregistre chaque gel avec sa durée, même quand l'application finit par se débloquer.
/// Windows ne journalise un gel (événement 1002) que si l'utilisateur ferme l'application.
/// </summary>
public sealed class HangCollector : ICollector
{
    private readonly Database _database;
    private readonly Dictionary<IntPtr, ActiveHang> _active = new();

    public HangCollector(Database database) => _database = database;

    public string Id => "hangs";
    public string Label => "Fenêtres qui ne répondent pas";
    public string? Hint => null;
    public IEnumerable<MetricDef> Metrics { get; } = new MetricDef[]
    {
        new("hang.count", "Fenêtres qui ne répondent pas", "", "cpu"),
    };

    public void Collect(Snapshot snapshot, CollectContext context)
    {
        var hung = new List<(IntPtr Handle, int Pid, string Title)>();
        NativeMethods.EnumWindows((handle, _) =>
        {
            if (!NativeMethods.IsWindowVisible(handle)) return true;
            if (NativeMethods.GetWindow(handle, NativeMethods.GwOwner) != IntPtr.Zero) return true;
            if (!NativeMethods.IsHungAppWindow(handle)) return true;
            // Windows remplace une fenêtre gelée par une « fenêtre fantôme » appartenant à DWM : on l'ignore.
            if (ClassName(handle) == "Ghost") return true;
            NativeMethods.GetWindowThreadProcessId(handle, out var pid);
            hung.Add((handle, (int)pid, Title(handle)));
            return true;
        }, IntPtr.Zero);

        var seen = new HashSet<IntPtr>();
        foreach (var (handle, pid, title) in hung)
        {
            seen.Add(handle);
            if (!_active.TryGetValue(handle, out var active))
            {
                var process = snapshot.Processes.FirstOrDefault(g => g.Pids.Contains(pid))?.Name ?? $"PID {pid}";
                active = new ActiveHang(_database.InsertHang(snapshot.Ts, pid, process, title), snapshot.Ts, pid, process, title);
                _active[handle] = active;
            }

            snapshot.Hung.Add(new HungWindow(active.Pid, active.Process, active.Title, active.Since));
        }

        foreach (var handle in _active.Keys.Where(h => !seen.Contains(h)).ToList())
        {
            _database.EndHang(_active[handle].Id, snapshot.Ts);
            _active.Remove(handle);
        }

        snapshot.Metrics["hang.count"] = snapshot.Hung.Count;
    }

    private static string Title(IntPtr handle)
    {
        var text = new StringBuilder(256);
        NativeMethods.GetWindowText(handle, text, text.Capacity);
        return text.ToString();
    }

    private static string ClassName(IntPtr handle)
    {
        var text = new StringBuilder(64);
        NativeMethods.GetClassName(handle, text, text.Capacity);
        return text.ToString();
    }

    public void Dispose()
    {
    }

    private sealed record ActiveHang(long Id, long Since, int Pid, string Process, string Title);
}
