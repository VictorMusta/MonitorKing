using System.Globalization;
using System.Text.RegularExpressions;

namespace MonitorKing.Core;

/// <summary>
/// Périphérique visé par une erreur de stockage, tel que Windows le nomme dans le message de l'événement :
/// « 3 » pour le disque physique n° 3 (\Device\Harddisk3), « E: » pour un volume.
/// C'est la seule partie du message qui quitte le PC en mode discret : elle ne dit rien de son utilisateur,
/// et sans elle le serveur ne distinguerait pas un disque interne d'une carte SD.
/// </summary>
public static partial class StorageDevice
{
    /// <summary>
    /// Mentions que l'agent place au début de la description d'un disque branché sur un bus externe, après son type
    /// s'il est connu (« Disque E: (USB Generic STORAGE DEVICE) », « Disque F: (HDD USB …) ») : le diagnostic s'y fie.
    /// </summary>
    public const string Usb = "USB";
    public const string SdCard = "Carte SD";

    // Événements « disk » 7, 11 et 51 : « … \Device\Harddisk3\DR3 … ». Un volume (\Device\HarddiskVolume3) ne correspond pas.
    [GeneratedRegex(@"\\Device\\Harddisk([0-9]{1,4})\\", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HarddiskPath();

    // Événement « disk » 153 : « … pour le disque 3 (nom d'objet périphérique physique : \Device\000000e6) … ».
    // Le mot « disque » change avec la langue de Windows, pas la forme : un nombre, puis la parenthèse du nom de périphérique.
    [GeneratedRegex(@"([0-9]{1,4})\s*\([^()]*\\Device\\", RegexOptions.CultureInvariant)]
    private static partial Regex NumberBeforeDeviceName();

    // Événements NTFS : « … sur le volume E:. », « Volume C: (\Device\HarddiskVolume3) … ».
    [GeneratedRegex(@"(?<![\w\\/])([A-Za-z]):(?!\w)", RegexOptions.CultureInvariant)]
    private static partial Regex VolumeLetter();

    [GeneratedRegex(@"\((?:HDD |SSD )?(?:" + Usb + "|" + SdCard + @")(?!\w)", RegexOptions.CultureInvariant)]
    private static partial Regex ExternalBus();

    /// <summary>Périphérique nommé dans le message d'un événement de stockage, ou null si Windows ne le nomme pas.</summary>
    public static string? FromMessage(string? message)
    {
        if (string.IsNullOrEmpty(message)) return null;
        if (HarddiskPath().Match(message) is { Success: true } path) return Number(path.Groups[1].Value);
        if (NumberBeforeDeviceName().Match(message) is { Success: true } number) return Number(number.Groups[1].Value);
        if (VolumeLetter().Match(message) is { Success: true } volume) return volume.Groups[1].Value.ToUpperInvariant() + ":";
        return null;
    }

    /// <summary>Périphérique reçu d'ailleurs (lot envoyé par un agent) : remis sous sa forme attendue, ou écarté.</summary>
    public static string? Normalize(string? device) => device switch
    {
        { Length: >= 1 and <= 4 } when device.All(char.IsAsciiDigit) => Number(device),
        { Length: 2 } when char.IsAsciiLetter(device[0]) && device[1] == ':' => device.ToUpperInvariant(),
        _ => null,
    };

    /// <summary>Numéro du disque physique, si le périphérique en est un (et non un volume).</summary>
    public static int? DiskNumber(string device) =>
        device.Length > 0 && device.All(char.IsAsciiDigit) && int.TryParse(device, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : null;

    /// <summary>« disque n° 3 » ou « volume E: » : la numérotation est celle de la Gestion des disques de Windows.</summary>
    public static string Describe(string device) => DiskNumber(device) is { } number ? $"disque n° {number}" : $"volume {device}";

    /// <summary>Vrai si le nom d'un disque suivi (« Disque E: (USB …) ») annonce un bus externe : clé USB, lecteur de cartes, disque externe.</summary>
    public static bool IsExternalDisk(string label) => ExternalBus().IsMatch(label);

    private static string Number(string digits) => int.Parse(digits, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
}
