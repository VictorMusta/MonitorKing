using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using MonitorKing.Agent.Native;

namespace MonitorKing.Agent.Collectors;

/// <summary>
/// Consommation par application, via NtQuerySystemInformation : une seule lecture pour tous les processus,
/// sans ouvrir de handle (donc pas d'« accès refusé » sur les processus système).
/// Les processus sont regroupés par nom d'exécutable ; les processus WebView2 sont rattachés à l'application
/// qui les a lancés (ex. Clipchamp), sinon ils apparaîtraient comme « msedgewebview2.exe ».
/// </summary>
public sealed class ProcessCollector : ICollector
{
    private const double Mb = 1024d * 1024;

    // Décalages de SYSTEM_PROCESS_INFORMATION en 64 bits.
    private const int OffNextEntry = 0;
    private const int OffWorkingSetPrivate = 8;
    private const int OffHardFaults = 16;
    private const int OffCreateTime = 32;
    private const int OffUserTime = 40;
    private const int OffKernelTime = 48;
    private const int OffImageNameLength = 56;
    private const int OffImageNameBuffer = 64;
    private const int OffPid = 80;
    private const int OffParentPid = 88;
    private const int OffPagefileUsage = 184;
    private const int OffReadTransfer = 232;
    private const int OffWriteTransfer = 240;

    private static readonly HashSet<string> HostProcesses = new(StringComparer.OrdinalIgnoreCase) { "msedgewebview2.exe" };

    private static readonly HashSet<string> GenericParents = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe", "svchost.exe", "sihost.exe", "RuntimeBroker.exe", "services.exe", "System",
    };

    private readonly int _logicalCores = Environment.ProcessorCount;
    private readonly Database _database;
    private readonly Dictionary<string, string?> _descriptions = new(StringComparer.OrdinalIgnoreCase);

    public ProcessCollector(Database database) => _database = database;
    private Dictionary<int, Previous> _previous = new();
    private Dictionary<int, (string Names, string Display)> _services = new();
    private HashSet<int> _serviceless = new();
    private DateTime _nextServiceRefresh = DateTime.MinValue;
    private DateTime _earliestServiceRefresh = DateTime.MinValue;
    private IntPtr _buffer = IntPtr.Zero;
    private int _bufferSize = 2 * 1024 * 1024;

    public string Id => "processes";
    public string Label => "Applications et processus";
    public string? Hint => null;

    public IEnumerable<MetricDef> Metrics { get; } = new MetricDef[]
    {
        new("mem.hardfaults", "Lectures de pages sur le disque (défauts durs)", "/s", "memory"),
        new("sys.processes", "Processus en cours", "", "cpu"),
    };

    public void Collect(Snapshot snapshot, CollectContext context)
    {
        var raw = Query();
        RefreshServices(raw);
        var byPid = new Dictionary<int, RawProcess>(raw.Count);
        foreach (var p in raw) byPid[p.Pid] = p;

        var cpuDenominator = context.ElapsedSeconds * 1e7 * _logicalCores;
        var groups = new Dictionary<string, ProcessGroup>(StringComparer.OrdinalIgnoreCase);
        var next = new Dictionary<int, Previous>(raw.Count);
        double totalHardFaults = 0;

        foreach (var p in raw)
        {
            if (p.Pid == 0) continue; // « Idle » : le temps libre du processeur, pas une application.

            double cpu = 0, read = 0, write = 0, hardFaults = 0;
            if (_previous.TryGetValue(p.Pid, out var prev) && prev.CreateTime == p.CreateTime)
            {
                cpu = Math.Max(0, p.CpuTime - prev.CpuTime) / cpuDenominator * 100;
                read = Math.Max(0, p.ReadBytes - prev.ReadBytes) / context.ElapsedSeconds;
                write = Math.Max(0, p.WriteBytes - prev.WriteBytes) / context.ElapsedSeconds;
                hardFaults = Math.Max(0L, (long)p.HardFaults - prev.HardFaults) / context.ElapsedSeconds;
            }

            next[p.Pid] = new Previous(p.CreateTime, p.CpuTime, p.ReadBytes, p.WriteBytes, p.HardFaults);
            totalHardFaults += hardFaults;

            var (name, via) = ResolveGroup(p, byPid);
            string? serviceDescription = null;
            // 80 « svchost.exe » regroupés ne disent rien : on les sépare par service Windows hébergé.
            if (name.Equals("svchost.exe", StringComparison.OrdinalIgnoreCase) && _services.TryGetValue(p.Pid, out var service))
            {
                name = $"svchost.exe · {service.Names}";
                serviceDescription = service.Display;
            }

            if (!groups.TryGetValue(name, out var group))
            {
                group = new ProcessGroup { Name = name, Via = via, Description = serviceDescription };
                groups[name] = group;
            }

            group.Via ??= via;
            group.Count++;
            group.Cpu += cpu;
            group.RamMb += p.PrivateWorkingSet / Mb;
            group.CommitMb += p.Commit / Mb;
            group.IoReadBps += read;
            group.IoWriteBps += write;
            group.HardFaultsPerSec += hardFaults;
            group.Pids.Add(p.Pid);

            if (context.GpuByPid.TryGetValue(p.Pid, out var gpu))
            {
                if (gpu.Utilization > group.Gpu) group.GpuEngine = gpu.Engine;
                group.Gpu = Math.Min(100, group.Gpu + gpu.Utilization);
            }

            if (context.VramBytesByPid.TryGetValue(p.Pid, out var vram))
                group.VramMb += vram / Mb;
        }

        _previous = next;

        foreach (var group in groups.Values)
        {
            group.Cpu = Math.Min(100, group.Cpu);
            group.Description ??= Describe(group.Name, group.Pids);
        }

        snapshot.Processes = groups.Values.OrderByDescending(g => g.Cpu).ThenByDescending(g => g.RamMb).ToList();
        snapshot.Metrics["mem.hardfaults"] = totalHardFaults;
        snapshot.Metrics["sys.processes"] = raw.Count - 1;
    }

    /// <summary>
    /// Services Windows par processus (lecture WMI). Relue seulement quand un svchost inconnu apparaît
    /// (au plus toutes les 30 s), sinon tous les quarts d'heure : l'interroger chaque minute créait
    /// des pics d'activité disque réguliers, causés par l'agent lui-même.
    /// </summary>
    private void RefreshServices(List<RawProcess> raw)
    {
        var now = DateTime.UtcNow;
        var hosts = raw.Where(p => p.Name.Equals("svchost.exe", StringComparison.OrdinalIgnoreCase)).Select(p => p.Pid).ToList();
        var unknownHost = hosts.Any(pid => !_services.ContainsKey(pid) && !_serviceless.Contains(pid));
        if (now < _nextServiceRefresh && !(unknownHost && now >= _earliestServiceRefresh)) return;
        _nextServiceRefresh = now.AddMinutes(15);
        _earliestServiceRefresh = now.AddSeconds(30);
        try
        {
            var byPid = new Dictionary<int, List<(string Name, string Display)>>();
            using var searcher = new ManagementObjectSearcher("SELECT Name, DisplayName, ProcessId FROM Win32_Service WHERE ProcessId <> 0");
            foreach (var item in searcher.Get())
            {
                using (item)
                {
                    var pid = Convert.ToInt32(item["ProcessId"]);
                    var name = item["Name"]?.ToString() ?? "?";
                    var display = item["DisplayName"]?.ToString() ?? name;
                    if (!byPid.TryGetValue(pid, out var list)) byPid[pid] = list = new List<(string, string)>();
                    list.Add((name, display));
                }
            }

            _services = byPid.ToDictionary(
                kv => kv.Key,
                kv => (
                    string.Join("+", kv.Value.Select(s => s.Name).Take(2)) + (kv.Value.Count > 2 ? "+…" : ""),
                    string.Join(", ", kv.Value.Select(s => s.Display).Take(2)) + (kv.Value.Count > 2 ? "…" : "")));
            // Certains svchost n'hébergent aucun service actif : on les retient pour ne pas relancer la requête en boucle.
            _serviceless = hosts.Where(pid => !_services.ContainsKey(pid)).ToHashSet();
        }
        catch (Exception)
        {
            // WMI indisponible : les svchost restent regroupés.
        }
    }

    private static (string Name, string? Via) ResolveGroup(RawProcess process, Dictionary<int, RawProcess> byPid)
    {
        if (!HostProcesses.Contains(process.Name)) return (process.Name, null);

        var current = process;
        for (var depth = 0; depth < 12; depth++)
        {
            // Un parent créé après l'enfant est un PID réutilisé : ce n'est pas le vrai parent.
            if (!byPid.TryGetValue(current.ParentPid, out var parent) || parent.Pid == current.Pid || parent.CreateTime > current.CreateTime)
                break;
            if (!HostProcesses.Contains(parent.Name))
                return GenericParents.Contains(parent.Name) ? (process.Name, null) : (parent.Name, "WebView2");
            current = parent;
        }

        return (process.Name, null);
    }

    private List<RawProcess> Query()
    {
        while (true)
        {
            if (_buffer == IntPtr.Zero) _buffer = Marshal.AllocHGlobal(_bufferSize);
            var status = NativeMethods.NtQuerySystemInformation(NativeMethods.SystemProcessInformation, _buffer, _bufferSize, out var needed);
            if (status == NativeMethods.StatusInfoLengthMismatch)
            {
                Marshal.FreeHGlobal(_buffer);
                _buffer = IntPtr.Zero;
                _bufferSize = Math.Max(needed + 256 * 1024, _bufferSize * 2);
                continue;
            }

            if (status != 0)
                throw new InvalidOperationException($"NtQuerySystemInformation a échoué (0x{status:X8})");
            break;
        }

        var list = new List<RawProcess>(512);
        var offset = 0;
        while (true)
        {
            var entry = _buffer + offset;
            var pid = (int)Marshal.ReadInt64(entry, OffPid);
            var nameLength = (ushort)Marshal.ReadInt16(entry, OffImageNameLength);
            var namePtr = Marshal.ReadIntPtr(entry, OffImageNameBuffer);
            var name = nameLength > 0 && namePtr != IntPtr.Zero
                ? Marshal.PtrToStringUni(namePtr, nameLength / 2)
                : pid == 0 ? "Idle" : "System";

            list.Add(new RawProcess(
                pid,
                (int)Marshal.ReadInt64(entry, OffParentPid),
                name,
                Marshal.ReadInt64(entry, OffCreateTime),
                Marshal.ReadInt64(entry, OffUserTime) + Marshal.ReadInt64(entry, OffKernelTime),
                Marshal.ReadInt64(entry, OffWorkingSetPrivate),
                Marshal.ReadInt64(entry, OffPagefileUsage),
                Marshal.ReadInt64(entry, OffReadTransfer),
                Marshal.ReadInt64(entry, OffWriteTransfer),
                (uint)Marshal.ReadInt32(entry, OffHardFaults)));

            var nextEntry = Marshal.ReadInt32(entry, OffNextEntry);
            if (nextEntry == 0) break;
            offset += nextEntry;
        }

        return list;
    }

    /// <summary>
    /// Nom lisible de l'application (« Description » de l'exécutable), mis en cache par nom.
    /// Note aussi son emplacement dans le catalogue : installée sous C:\Windows = composant de Windows,
    /// qui reste lisible côté serveur en mode discret.
    /// </summary>
    private string? Describe(string name, List<int> pids)
    {
        if (_descriptions.TryGetValue(name, out var cached)) return cached;

        string? description = null;
        string? path = null;
        foreach (var pid in pids.Take(3))
        {
            var handle = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, pid);
            if (handle == IntPtr.Zero) continue;
            try
            {
                var buffer = new StringBuilder(1024);
                var size = buffer.Capacity;
                if (!NativeMethods.QueryFullProcessImageName(handle, 0, buffer, ref size)) continue;
                path = buffer.ToString();
                var info = FileVersionInfo.GetVersionInfo(path);
                description = string.IsNullOrWhiteSpace(info.FileDescription) ? null : info.FileDescription.Trim();
                break;
            }
            catch (Exception)
            {
                // Fichier introuvable ou protégé : on garde le nom de l'exécutable.
            }
            finally
            {
                NativeMethods.CloseHandle(handle);
            }
        }

        _descriptions[name] = description;
        var system = path is not null && path.StartsWith(WindowsDirectory, StringComparison.OrdinalIgnoreCase);
        try
        {
            _database.UpsertApp(name, path, description, system);
        }
        catch (Exception)
        {
            // Le catalogue est un confort : son absence ne doit pas bloquer la collecte.
        }

        return description;
    }

    private static readonly string WindowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";

    public void Dispose()
    {
        if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
        _buffer = IntPtr.Zero;
    }

    private sealed record RawProcess(
        int Pid,
        int ParentPid,
        string Name,
        long CreateTime,
        long CpuTime,
        long PrivateWorkingSet,
        long Commit,
        long ReadBytes,
        long WriteBytes,
        uint HardFaults);

    private sealed record Previous(long CreateTime, long CpuTime, long ReadBytes, long WriteBytes, uint HardFaults);
}
