using System.Runtime.InteropServices;
using System.Text;
using MonitorKing.Agent.Native;

namespace MonitorKing.Agent.Collectors;

/// <summary>Signal et débit de la connexion Wi-Fi (API Native Wifi de Windows, en lecture seule).</summary>
public sealed class WifiCollector : ICollector
{
    private const uint OpcodeChannelNumber = 8;
    private const uint OpcodeCurrentConnection = 7;
    private const uint OpcodeRssi = 0x10000102;
    private const int InterfaceInfoSize = 16 + 512 + 4;
    private const uint ErrorAccessDenied = 5;

    private IntPtr _handle = IntPtr.Zero;
    private bool _unavailable;

    public string Id => "wifi";
    public string Label => "Wi-Fi";
    public string? Hint { get; private set; }

    public IEnumerable<MetricDef> Metrics { get; } = new MetricDef[]
    {
        new("wifi.signal", "Qualité du signal Wi-Fi", "%", "network", 100),
        new("wifi.rssi", "Puissance du signal Wi-Fi", "dBm", "network"),
        new("wifi.rx", "Débit de liaison Wi-Fi (réception)", "Mb/s", "network"),
        new("wifi.tx", "Débit de liaison Wi-Fi (envoi)", "Mb/s", "network"),
    };

    public void Collect(Snapshot snapshot, CollectContext context)
    {
        if (_unavailable) return;
        if (_handle == IntPtr.Zero && NativeMethods.WlanOpenHandle(2, IntPtr.Zero, out _, out _handle) != 0)
        {
            _unavailable = true;
            Hint = "Pas de Wi-Fi sur ce PC (service WLAN absent).";
            return;
        }

        if (NativeMethods.WlanEnumInterfaces(_handle, IntPtr.Zero, out var list) != 0) return;
        try
        {
            var count = Marshal.ReadInt32(list, 0);
            for (var i = 0; i < count; i++)
            {
                var item = list + 8 + i * InterfaceInfoSize;
                var guid = Marshal.PtrToStructure<Guid>(item);
                var adapter = Marshal.PtrToStringUni(item + 16) ?? "Wi-Fi";
                var state = Marshal.ReadInt32(item, 16 + 512);
                if (state != 1)
                {
                    snapshot.Wifi ??= new WifiInfo(adapter, StateName(state), null, null, null, null, null, null, null);
                    continue;
                }

                snapshot.Wifi = ReadConnection(adapter, ref guid, snapshot);
                break;
            }
        }
        finally
        {
            NativeMethods.WlanFreeMemory(list);
        }
    }

    private WifiInfo ReadConnection(string adapter, ref Guid guid, Snapshot snapshot)
    {
        string? ssid = null;
        int? quality = null;
        double? rx = null, tx = null;

        var result = NativeMethods.WlanQueryInterface(_handle, ref guid, OpcodeCurrentConnection, IntPtr.Zero, out _, out var data, IntPtr.Zero);
        if (result == 0)
        {
            try
            {
                // WLAN_CONNECTION_ATTRIBUTES : état (4), mode (4), profil (512), puis WLAN_ASSOCIATION_ATTRIBUTES à 520.
                var ssidLength = Math.Clamp(Marshal.ReadInt32(data, 520), 0, 32);
                var bytes = new byte[ssidLength];
                Marshal.Copy(data + 524, bytes, 0, ssidLength);
                ssid = ssidLength > 0 ? Encoding.UTF8.GetString(bytes) : null;
                quality = Marshal.ReadInt32(data, 576);
                rx = (uint)Marshal.ReadInt32(data, 580) / 1000.0;
                tx = (uint)Marshal.ReadInt32(data, 584) / 1000.0;
                Hint = null;
            }
            finally
            {
                NativeMethods.WlanFreeMemory(data);
            }
        }
        else if (result == ErrorAccessDenied)
        {
            Hint = "Windows bloque la lecture du Wi-Fi : autorise « Laisser les applications de bureau accéder à votre position » (Paramètres > Confidentialité et sécurité > Localisation).";
        }

        var rssi = QueryInt(ref guid, OpcodeRssi);
        var channel = QueryInt(ref guid, OpcodeChannelNumber);

        if (quality is { } q) snapshot.Metrics["wifi.signal"] = q;
        if (rssi is { } r) snapshot.Metrics["wifi.rssi"] = r;
        if (rx is { } down) snapshot.Metrics["wifi.rx"] = down;
        if (tx is { } up) snapshot.Metrics["wifi.tx"] = up;

        var band = channel switch
        {
            null => null,
            <= 14 => "2,4 GHz",
            _ => "5 GHz",
        };
        return new WifiInfo(adapter, "Connecté", ssid, quality, rssi, rx, tx, channel, band);
    }

    private int? QueryInt(ref Guid guid, uint opcode)
    {
        if (NativeMethods.WlanQueryInterface(_handle, ref guid, opcode, IntPtr.Zero, out _, out var data, IntPtr.Zero) != 0)
            return null;
        try
        {
            return Marshal.ReadInt32(data);
        }
        finally
        {
            NativeMethods.WlanFreeMemory(data);
        }
    }

    private static string StateName(int state) => state switch
    {
        0 => "Non prêt",
        1 => "Connecté",
        3 => "Déconnexion…",
        4 => "Déconnecté",
        5 => "Association…",
        6 => "Recherche…",
        7 => "Authentification…",
        _ => "Inconnu",
    };

    public void Dispose()
    {
        if (_handle != IntPtr.Zero) NativeMethods.WlanCloseHandle(_handle, IntPtr.Zero);
        _handle = IntPtr.Zero;
    }
}
