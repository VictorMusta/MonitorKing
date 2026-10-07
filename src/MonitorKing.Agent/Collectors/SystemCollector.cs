using System.Diagnostics;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using MonitorKing.Agent.Native;

namespace MonitorKing.Agent.Collectors;

/// <summary>Processeur (par cœur), mémoire, disques physiques et débit réseau.</summary>
public sealed class SystemCollector : ICollector
{
    private const double Gb = 1024d * 1024 * 1024;
    // SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION : Idle, Kernel, User, Dpc, Interrupt (5 × 8 octets) + InterruptCount (4) + alignement.
    private const int CoreInfoSize = 48;

    private readonly int _cores = Environment.ProcessorCount;
    private long[]? _prevCores;
    private readonly Dictionary<string, DiskCounters> _disks = new();
    private Dictionary<string, string>? _models;
    private DateTime _nextDiskRefresh = DateTime.MinValue;
    private long _prevNetRx, _prevNetTx;
    private bool _hasNet;
    private readonly List<MetricDef> _defs = new()
    {
        new("cpu.total", "Processeur", "%", "cpu", 100),
        new("cpu.maxcore", "Cœur le plus chargé", "%", "cpu", 100),
        new("cpu.dpc", "Pilotes (DPC + interruptions)", "%", "cpu", 100),
        new("mem.load", "Mémoire vive utilisée", "%", "memory", 100),
        new("mem.used", "Mémoire vive utilisée (volume)", "Go", "memory"),
        new("mem.commit", "Mémoire engagée", "Go", "memory"),
        new("mem.commitpct", "Mémoire engagée / limite", "%", "memory", 100),
        new("disk.active", "Disque le plus actif", "%", "disk", 100),
        new("disk.latency", "Temps de réponse disque (pire disque)", "ms", "disk"),
        new("disk.read", "Lecture disque", "o/s", "disk"),
        new("disk.write", "Écriture disque", "o/s", "disk"),
        new("net.down", "Réception réseau", "o/s", "network"),
        new("net.up", "Envoi réseau", "o/s", "network"),
    };

    public string Id => "system";
    public string Label => "Processeur, mémoire, disques, réseau";
    public string? Hint { get; private set; }

    public IEnumerable<MetricDef> Metrics
    {
        get { lock (_defs) return _defs.ToArray(); }
    }

    public void Collect(Snapshot snapshot, CollectContext context)
    {
        CollectCpu(snapshot);
        CollectMemory(snapshot);
        CollectDisks(snapshot);
        CollectNetwork(snapshot, context);
    }

    private void CollectCpu(Snapshot snapshot)
    {
        var size = CoreInfoSize * _cores;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (NativeMethods.NtQuerySystemInformation(NativeMethods.SystemProcessorPerformanceInformation, buffer, size, out _) != 0)
                return;

            var current = new long[_cores * 5];
            for (var i = 0; i < _cores; i++)
                for (var f = 0; f < 5; f++)
                    current[i * 5 + f] = Marshal.ReadInt64(buffer, i * CoreInfoSize + f * 8);

            if (_prevCores is { } prev)
            {
                double busyAll = 0, totalAll = 0, dpcAll = 0, maxCore = 0;
                for (var i = 0; i < _cores; i++)
                {
                    var idle = current[i * 5] - prev[i * 5];
                    // Le temps noyau inclut le temps d'inactivité.
                    var total = current[i * 5 + 1] - prev[i * 5 + 1] + current[i * 5 + 2] - prev[i * 5 + 2];
                    var dpc = current[i * 5 + 3] - prev[i * 5 + 3] + current[i * 5 + 4] - prev[i * 5 + 4];
                    if (total <= 0) continue;
                    var busy = Math.Max(0, total - idle);
                    maxCore = Math.Max(maxCore, 100.0 * busy / total);
                    busyAll += busy;
                    totalAll += total;
                    dpcAll += Math.Max(0, dpc);
                }

                if (totalAll > 0)
                {
                    snapshot.Metrics["cpu.total"] = Math.Clamp(100.0 * busyAll / totalAll, 0, 100);
                    snapshot.Metrics["cpu.dpc"] = Math.Clamp(100.0 * dpcAll / totalAll, 0, 100);
                    snapshot.Metrics["cpu.maxcore"] = Math.Clamp(maxCore, 0, 100);
                }
            }

            _prevCores = current;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void CollectMemory(Snapshot snapshot)
    {
        var m = NativeMethods.GetMemoryStatus();
        if (m.TotalPhys == 0) return;
        snapshot.Metrics["mem.load"] = 100.0 * (m.TotalPhys - m.AvailPhys) / m.TotalPhys;
        snapshot.Metrics["mem.used"] = (m.TotalPhys - m.AvailPhys) / Gb;
        var commit = m.TotalPageFile - m.AvailPageFile;
        snapshot.Metrics["mem.commit"] = commit / Gb;
        if (m.TotalPageFile > 0)
            snapshot.Metrics["mem.commitpct"] = 100.0 * commit / m.TotalPageFile;
    }

    private void CollectDisks(Snapshot snapshot)
    {
        if (DateTime.UtcNow >= _nextDiskRefresh)
            RefreshDisks();

        double maxActive = 0, maxLatency = 0, read = 0, write = 0;
        var any = false;
        foreach (var (instance, counters) in _disks)
        {
            try
            {
                var active = Math.Clamp(100 - counters.Idle.NextValue(), 0, 100);
                var latencyMs = counters.Latency.NextValue() * 1000;
                var r = counters.Read.NextValue();
                var w = counters.Write.NextValue();
                snapshot.Metrics[$"disk.{counters.Index}.active"] = active;
                snapshot.Metrics[$"disk.{counters.Index}.latency"] = latencyMs;
                maxActive = Math.Max(maxActive, active);
                maxLatency = Math.Max(maxLatency, latencyMs);
                read += r;
                write += w;
                any = true;
            }
            catch (InvalidOperationException)
            {
                // Disque débranché entre deux rafraîchissements : il disparaîtra au prochain.
                _nextDiskRefresh = DateTime.MinValue;
            }
        }

        if (!any) return;
        snapshot.Metrics["disk.active"] = maxActive;
        snapshot.Metrics["disk.latency"] = maxLatency;
        snapshot.Metrics["disk.read"] = read;
        snapshot.Metrics["disk.write"] = write;
    }

    private void RefreshDisks()
    {
        _nextDiskRefresh = DateTime.UtcNow.AddMinutes(1);
        string[] instances;
        try
        {
            instances = new PerformanceCounterCategory("PhysicalDisk").GetInstanceNames();
        }
        catch (Exception ex)
        {
            Hint = "Compteurs disque indisponibles : " + ex.Message;
            return;
        }

        var wanted = instances.Where(i => i != "_Total").ToHashSet();
        foreach (var gone in _disks.Keys.Where(k => !wanted.Contains(k)).ToList())
        {
            _disks[gone].Dispose();
            _disks.Remove(gone);
        }

        // L'inventaire WMI des disques sollicite toute la pile de stockage : on ne le fait que
        // si un disque inconnu apparaît (le faire chaque minute créait des pics d'activité réguliers).
        var added = wanted.Where(i => !_disks.ContainsKey(i)).ToList();
        if (added.Count > 0 && (_models is null || added.Any(i => !_models.ContainsKey(i.Split(' ', 2)[0]))))
            _models = PhysicalDiskModels();
        var models = _models ?? new Dictionary<string, string>();
        foreach (var instance in added)
        {
            var index = instance.Split(' ', 2)[0];
            var letters = instance.Length > index.Length ? instance[(index.Length + 1)..].Trim() : "";
            var name = string.IsNullOrEmpty(letters) ? $"Disque {index}" : $"Disque {letters}";
            if (models.TryGetValue(index, out var model)) name += $" ({model})";
            _disks[instance] = new DiskCounters(
                index,
                new PerformanceCounter("PhysicalDisk", "% Idle Time", instance, true),
                new PerformanceCounter("PhysicalDisk", "Avg. Disk sec/Transfer", instance, true),
                new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", instance, true),
                new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", instance, true));
            lock (_defs)
            {
                if (_defs.All(d => d.Key != $"disk.{index}.active"))
                {
                    _defs.Add(new MetricDef($"disk.{index}.active", $"{name} · activité", "%", "disk", 100));
                    _defs.Add(new MetricDef($"disk.{index}.latency", $"{name} · temps de réponse", "ms", "disk"));
                }
            }
        }
    }

    /// <summary>Type (HDD, SSD, NVMe), bus externe éventuel (USB, carte SD) et modèle de chaque disque physique, par numéro.</summary>
    private static Dictionary<string, string> PhysicalDiskModels()
    {
        var models = new Dictionary<string, string>();
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage",
                "SELECT DeviceId, FriendlyName, MediaType, BusType FROM MSFT_PhysicalDisk");
            foreach (var disk in searcher.Get())
            {
                using (disk)
                {
                    var media = Convert.ToInt32(disk["MediaType"] ?? 0);
                    var bus = Convert.ToInt32(disk["BusType"] ?? 0);
                    var type = (media, bus) switch
                    {
                        (3, _) => "HDD",
                        (_, 17) => "SSD NVMe",
                        (4, _) => "SSD",
                        _ => null,
                    };
                    // Bus externe (USB, lecteur de cartes SD) : toujours annoncé, même quand le type du disque est connu.
                    // Le diagnostic s'y fie pour ne pas prendre une clé USB ou une carte SD défaillante pour un disque interne.
                    var external = bus switch
                    {
                        7 => StorageDevice.Usb,
                        12 => StorageDevice.SdCard,
                        _ => null,
                    };
                    var friendly = disk["FriendlyName"]?.ToString()?.Trim();
                    models[disk["DeviceId"]?.ToString() ?? ""] = string.Join(" ", new[] { type, external, friendly }.Where(s => !string.IsNullOrEmpty(s)));
                }
            }
        }
        catch (Exception)
        {
            // Classe de stockage indisponible : on garde « Disque N ».
        }

        return models;
    }

    private void CollectNetwork(Snapshot snapshot, CollectContext context)
    {
        long rx = 0, tx = 0;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            // Les commutateurs virtuels Hyper-V doublent le trafic de la carte physique.
            if (nic.Name.StartsWith("vEthernet", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var stats = nic.GetIPStatistics();
                rx += stats.BytesReceived;
                tx += stats.BytesSent;
            }
            catch (NetworkInformationException)
            {
            }
        }

        if (_hasNet)
        {
            snapshot.Metrics["net.down"] = Math.Max(0, rx - _prevNetRx) / context.ElapsedSeconds;
            snapshot.Metrics["net.up"] = Math.Max(0, tx - _prevNetTx) / context.ElapsedSeconds;
        }

        _prevNetRx = rx;
        _prevNetTx = tx;
        _hasNet = true;
    }

    public void Dispose()
    {
        foreach (var disk in _disks.Values) disk.Dispose();
        _disks.Clear();
    }

    private sealed record DiskCounters(string Index, PerformanceCounter Idle, PerformanceCounter Latency, PerformanceCounter Read, PerformanceCounter Write) : IDisposable
    {
        public void Dispose()
        {
            Idle.Dispose();
            Latency.Dispose();
            Read.Dispose();
            Write.Dispose();
        }
    }
}
