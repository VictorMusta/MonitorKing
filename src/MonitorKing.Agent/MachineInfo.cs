using System.Management;
using System.Security.Principal;
using Microsoft.Win32;
using MonitorKing.Agent.Native;

namespace MonitorKing.Agent;

/// <summary>Fiche d'identité de la machine, calculée une fois au démarrage.</summary>
public sealed class MachineInfo
{
    public static bool IsAdministrator { get; } = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    public string MachineName { get; } = Environment.MachineName;
    public string Os { get; }
    public string Cpu { get; }
    public int LogicalCores { get; } = Environment.ProcessorCount;
    public double RamGb { get; }
    public IReadOnlyList<string> Gpus { get; }
    /// <summary>Carte mère complète, ex. « MSI B550 GAMING GEN3 (MS-7B86) ».</summary>
    public string Board { get; }
    /// <summary>Nom court affiché pour les sondes de la carte mère, ex. « MSI B550 ».</summary>
    public string BoardLabel { get; }
    public bool Administrator => IsAdministrator;
    public string AgentVersion { get; } = typeof(MachineInfo).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
    public long AgentStarted { get; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public long BootTime => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - Environment.TickCount64;

    public MachineInfo(AgentOptions options)
    {
        Os = ReadOs();
        Cpu = Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", null) is string cpu
            ? cpu.Trim()
            : "Processeur inconnu";
        RamGb = Math.Round(NativeMethods.GetMemoryStatus().TotalPhys / (1024d * 1024 * 1024), 1);
        Gpus = ReadGpus();
        (Board, var shortName) = ReadBoard();
        BoardLabel = string.IsNullOrWhiteSpace(options.MotherboardLabel) ? shortName : options.MotherboardLabel.Trim();
    }

    private static readonly Dictionary<string, string> Vendors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Micro-Star International Co., Ltd."] = "MSI",
        ["ASUSTeK COMPUTER INC."] = "ASUS",
        ["Gigabyte Technology Co., Ltd."] = "Gigabyte",
        ["ASRock"] = "ASRock",
    };

    /// <summary>Carte mère via WMI ; nom court = fabricant + chipset (« MSI » + « B550 »).</summary>
    private static (string Full, string Short) ReadBoard()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Manufacturer, Product FROM Win32_BaseBoard");
            foreach (var board in searcher.Get())
            {
                using (board)
                {
                    var maker = board["Manufacturer"]?.ToString()?.Trim() ?? "";
                    var product = board["Product"]?.ToString()?.Trim() ?? "";
                    var vendor = Vendors.TryGetValue(maker, out var known) ? known : maker.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                    var chipset = System.Text.RegularExpressions.Regex.Match(product, @"\b[ABHXZQ]\d{3}[A-Z]?\b");
                    var model = chipset.Success ? chipset.Value : product.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                    var shortName = $"{vendor} {model}".Trim();
                    return ($"{vendor} {product}".Trim(), shortName.Length > 0 ? shortName : "Carte mère");
                }
            }
        }
        catch (Exception)
        {
        }

        return ("Carte mère", "Carte mère");
    }

    private static string ReadOs()
    {
        const string key = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        var product = Registry.GetValue(key, "ProductName", null) as string ?? "Windows";
        var display = Registry.GetValue(key, "DisplayVersion", null) as string;
        var build = Registry.GetValue(key, "CurrentBuild", null) as string;
        // Windows 11 annonce encore « Windows 10 » dans ProductName : on corrige avec le numéro de build.
        if (int.TryParse(build, out var number) && number >= 22000)
            product = product.Replace("Windows 10", "Windows 11");
        return string.Join(" ", new[] { product, display, build is null ? null : $"(build {build})" }.Where(s => !string.IsNullOrEmpty(s)));
    }

    private static IReadOnlyList<string> ReadGpus()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
            return searcher.Get().Cast<ManagementObject>()
                .Select(o => o["Name"]?.ToString())
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n!)
                .ToList();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }
}
