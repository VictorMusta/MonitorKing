namespace MonitorKing.Core.Tests;

public class StorageDeviceTests
{
    [Theory]
    // Événements « disk » 51, 7 et 11 : le chemin du périphérique, identique dans toutes les langues.
    [InlineData(@"Une erreur a été détectée sur le périphérique \Device\Harddisk3\DR3 lors d'une opération de pagination.", "3")]
    [InlineData(@"An error was detected on device \Device\Harddisk12\DR15 during a paging operation.", "12")]
    [InlineData(@"The device, \Device\Harddisk1\DR1, has a bad block.", "1")]
    [InlineData(@"The driver detected a controller error on \Device\Harddisk0\DR0.", "0")]
    // Événement « disk » 153 : le numéro précède la parenthèse du nom de périphérique, quel que soit le mot pour « disque ».
    [InlineData(@"L’opération d’E/S à l’adresse de bloc logique 0x0 pour le disque 3 (nom d’objet périphérique physique : \Device\000000e6) a été tentée à nouveau.", "3")]
    [InlineData(@"The IO operation at logical block address 0x1a2b3c for Disk 2 (PDO name: \Device\00000035) was retried.", "2")]
    [InlineData(@"Der E/A-Vorgang an der logischen Blockadresse 0x21 für Datenträger 4 (PDO-Name: \Device\00000036) wurde wiederholt.", "4")]
    // Événements NTFS : la lettre du volume.
    [InlineData("Une altération a été découverte dans la structure du système de fichiers sur le volume E:.\r\n\r\nLa nature exacte de l'altération est inconnue.", "E:")]
    [InlineData(@"Volume C: (\Device\HarddiskVolume3) needs to be taken offline to perform a Full Chkdsk.", "C:")]
    [InlineData("The default transaction resource manager on volume f: encountered a non-retryable error and could not start the transaction manager.", "F:")]
    public void Retrouve_le_peripherique_nomme_par_Windows(string message, string device)
    {
        Assert.Equal(device, StorageDevice.FromMessage(message));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    // Le pilote NVMe ne nomme que le port du contrôleur.
    [InlineData(@"The driver detected a controller error on \Device\RaidPort0.")]
    [InlineData(@"Reset to device, \Device\RaidPort0, was issued.")]
    // Volumes sans lettre : ni disque, ni lecteur à nommer.
    [InlineData(@"The default transaction resource manager on volume \\?\Volume{b75e2c83-0000-0000-0000-602200000000} encountered a non-retryable error and could not start the transaction manager.")]
    [InlineData(@"Volume ?? (\Device\HarddiskVolume12) needs to be taken offline to perform a Full Chkdsk.")]
    public void Ne_devine_pas_quand_Windows_ne_nomme_ni_disque_ni_lecteur(string? message)
    {
        Assert.Null(StorageDevice.FromMessage(message));
    }

    [Theory]
    [InlineData("3", "3")]
    [InlineData("003", "3")]
    [InlineData("e:", "E:")]
    [InlineData("E:", "E:")]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("12345", null)]
    [InlineData("EE:", null)]
    [InlineData("3:", null)]
    [InlineData("<b>", null)]
    [InlineData(@"\Device\Harddisk3", null)]
    public void N_accepte_d_un_agent_qu_un_numero_de_disque_ou_une_lettre_de_volume(string? received, string? kept)
    {
        Assert.Equal(kept, StorageDevice.Normalize(received));
    }

    [Theory]
    [InlineData("Disque E: (USB Generic STORAGE DEVICE)", true)]
    [InlineData("Disque 3 (USB Generic STORAGE DEVICE)", true)]
    [InlineData("Disque F: (HDD USB EXEMPLE Externe 2000)", true)]
    [InlineData("Disque G: (SSD USB EXEMPLE Portable 500)", true)]
    [InlineData("Disque H: (Carte SD EXEMPLE 64)", true)]
    [InlineData("Disque C: (SSD NVMe EXEMPLE 500)", false)]
    [InlineData("Disque C: D: (HDD EXEMPLE 2000)", false)]
    [InlineData("Disque C: (SSD EXEMPLE 250)", false)]
    [InlineData("Disque 3", false)]
    // Seule compte la mention placée par l'agent en tête de la description, pas un modèle qui contiendrait « USB ».
    [InlineData("Disque C: (EXEMPLE USB 500)", false)]
    [InlineData("Disque C: (USBX 500)", false)]
    public void Reconnait_un_disque_externe_a_la_mention_de_son_bus(string label, bool external)
    {
        Assert.Equal(external, StorageDevice.IsExternalDisk(label));
    }

    [Fact]
    public void Decrit_le_peripherique_comme_la_Gestion_des_disques()
    {
        Assert.Equal("disque n° 3", StorageDevice.Describe("3"));
        Assert.Equal("volume E:", StorageDevice.Describe("E:"));
        Assert.Equal(3, StorageDevice.DiskNumber("3"));
        Assert.Null(StorageDevice.DiskNumber("E:"));
    }
}
