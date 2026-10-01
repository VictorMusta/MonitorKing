namespace MonitorKing.Agent;

public sealed class AgentOptions
{
    public int Port { get; set; } = 5757;
    public int SampleIntervalMs { get; set; } = 2000;
    /// <summary>Rythme de lecture des capteurs matériels (lecture lente, sur son propre fil).</summary>
    public int SensorIntervalMs { get; set; } = 5000;
    /// <summary>Nombre de ticks agrégés dans une ligne SQLite (5 × 2 s = 10 s).</summary>
    public int PersistEveryTicks { get; set; } = 5;
    public int RetentionDays { get; set; } = 14;
    public int EventRetentionDays { get; set; } = 90;
    public int EventBackfillDays { get; set; } = 30;
    public string? DataDirectory { get; set; }
    /// <summary>Nom affiché pour les sondes de la carte mère. Vide = déduit (fabricant + chipset, ex. « MSI B550 »).</summary>
    public string? MotherboardLabel { get; set; }
    /// <summary>Envoi vers le serveur central (désactivé si l'adresse est vide).</summary>
    public ServerOptions Server { get; set; } = new();
}

public sealed class ServerOptions
{
    /// <summary>Adresse du serveur, ex. https://monitorking.duckdns.org. Vide = pas d'envoi.</summary>
    public string? Url { get; set; }
    /// <summary>Code d'inscription à usage unique, généré sur le serveur. Inutile une fois la machine inscrite.</summary>
    public string? EnrollmentCode { get; set; }
    public int UploadIntervalSeconds { get; set; } = 15;
}
