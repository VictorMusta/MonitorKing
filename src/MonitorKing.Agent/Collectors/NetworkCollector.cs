using System.Globalization;
using System.Net;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace MonitorKing.Agent.Collectors;

/// <summary>
/// Réseau par application. Windows ne le compte pas par processus, mais son traçage du noyau (ETW, la source
/// du Moniteur de ressources) signale chaque envoi et réception TCP/UDP avec le processus concerné : on additionne
/// les octets par processus, hors trafic local (127.0.0.1, ::1). Ouvrir une session ETW demande les droits
/// administrateur ; sans eux, seul le débit total des cartes réseau reste mesuré.
/// </summary>
public sealed class NetworkCollector : ICollector
{
    private const string SessionName = "MonitorKing-Network";

    private readonly bool _isAdmin;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private Dictionary<int, (long Send, long Recv)> _bytes = new();
    private long _events;
    private TraceEventSession? _session;
    private Thread? _thread;
    private bool _started;
    private volatile bool _disposed;

    public NetworkCollector(bool isAdmin, ILogger logger)
    {
        _isAdmin = isAdmin;
        _logger = logger;
    }

    public string Id => "network";
    public string Label => "Réseau par application";
    public string? Hint { get; private set; }
    public string? Detail { get; private set; }
    public IEnumerable<MetricDef> Metrics => Array.Empty<MetricDef>();

    public void Collect(Snapshot snapshot, CollectContext context)
    {
        if (!_started) Start();
        if (_session is null) return;

        Dictionary<int, (long Send, long Recv)> bytes;
        long events;
        lock (_gate)
        {
            bytes = _bytes;
            events = _events;
            _bytes = new Dictionary<int, (long, long)>(bytes.Count);
            _events = 0;
        }

        foreach (var (pid, (send, recv)) in bytes)
            context.NetByPid[pid] = (send / context.ElapsedSeconds, recv / context.ElapsedSeconds);
        Detail = string.Create(CultureInfo.GetCultureInfo("fr-FR"),
            $"Traçage réseau de Windows (ETW) : {events / context.ElapsedSeconds:0} envois et réceptions par seconde, trafic local ignoré.");
    }

    private void Start()
    {
        _started = true;
        if (!_isAdmin)
        {
            Hint = "Réseau par application : relance l'agent en administrateur. Le débit total du PC reste mesuré.";
            return;
        }

        TraceEventSession? session = null;
        try
        {
            // Une session du même nom laissée par un agent arrêté brutalement est remplacée.
            session = new TraceEventSession(SessionName) { StopOnDispose = true };
            session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);
            var kernel = session.Source.Kernel;
            kernel.TcpIpSend += e => Count(e.ProcessID, e.size, e.saddr, e.daddr, send: true);
            kernel.TcpIpRecv += e => Count(e.ProcessID, e.size, e.saddr, e.daddr, send: false);
            kernel.TcpIpSendIPV6 += e => Count(e.ProcessID, e.size, e.saddr, e.daddr, send: true);
            kernel.TcpIpRecvIPV6 += e => Count(e.ProcessID, e.size, e.saddr, e.daddr, send: false);
            kernel.UdpIpSend += e => Count(e.ProcessID, e.size, e.saddr, e.daddr, send: true);
            kernel.UdpIpRecv += e => Count(e.ProcessID, e.size, e.saddr, e.daddr, send: false);
            kernel.UdpIpSendIPV6 += e => Count(e.ProcessID, e.size, e.saddr, e.daddr, send: true);
            kernel.UdpIpRecvIPV6 += e => Count(e.ProcessID, e.size, e.saddr, e.daddr, send: false);
            _session = session;
            _thread = new Thread(Listen) { IsBackground = true, Name = "MonitorKing.Network", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }
        catch (Exception ex)
        {
            Hint = "Réseau par application indisponible : " + ex.Message;
            _logger.LogWarning(ex, "Session ETW réseau impossible");
            session?.Dispose();
            _session = null;
        }
    }

    private void Listen()
    {
        try
        {
            _session?.Source.Process(); // rend la main quand la session s'arrête
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Écoute du réseau interrompue");
        }

        if (!_disposed) Hint = "Écoute du réseau interrompue (une autre instance de MonitorKing l'a peut-être reprise).";
    }

    private void Count(int pid, int size, IPAddress source, IPAddress destination, bool send)
    {
        if (pid <= 0 || size <= 0 || IPAddress.IsLoopback(source) || IPAddress.IsLoopback(destination)) return;
        lock (_gate)
        {
            _events++;
            var current = _bytes.GetValueOrDefault(pid);
            _bytes[pid] = send ? (current.Send + size, current.Recv) : (current.Send, current.Recv + size);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        try
        {
            _session?.Dispose();
        }
        catch (Exception)
        {
            // Session déjà arrêtée.
        }

        _thread?.Join(TimeSpan.FromSeconds(2));
    }
}
