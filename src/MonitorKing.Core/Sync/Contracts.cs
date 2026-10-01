namespace MonitorKing.Core.Sync;

// Contrat d'échange agent → serveur. La connexion part toujours de l'agent (HTTPS sortant) ;
// le serveur ne peut rien demander à l'agent, seulement accuser réception.

/// <summary>Demande d'inscription avec un code à usage unique généré sur le serveur.</summary>
public sealed record EnrollRequest(string Code, string AgentVersion);

/// <summary>Identifiant et jeton propres à la machine ; le libellé est celui choisi à la création du code.</summary>
public sealed record EnrollResponse(string MachineId, string Token, string Label);

public sealed record SampleDto(string Key, long Ts, double Avg, double Max);

public sealed record ProcessDto(long Ts, ProcRow Row);

/// <summary>
/// Vrai nom d'une application pseudonymisée, chiffré (AES-256-GCM) avec la clé de lecture du PC.
/// Le serveur le stocke sans pouvoir le lire ; seul un navigateur qui a la clé peut le déchiffrer.
/// </summary>
public sealed record SealedName(string Pseudonym, string Sealed);

/// <summary>
/// Lot de données envoyé par l'agent. En mode « discret » (par défaut), les applications non Windows
/// sont pseudonymisées et les titres de fenêtres comme les messages des événements sont retirés.
/// </summary>
public sealed record UploadBatch(
    int Version,
    string Mode,
    MachineSummary Summary,
    List<MetricDef> Definitions,
    List<SampleDto> Samples,
    List<ProcessDto> Processes,
    List<EventItem> Events,
    List<HangItem> Hangs,
    List<SealedName>? SealedNames = null);
