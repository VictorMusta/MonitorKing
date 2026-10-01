# MonitorKing

Comprendre en temps réel **pourquoi un PC Windows rame** (application gelée, disque saturé, RAM pleine, surchauffe, erreurs), et le retrouver après coup.

> Prototype V0 : agent local + dashboard sur `http://localhost:5757`. La synchronisation vers le serveur n'est pas encore faite.

## Lancer

```bash
dotnet run --project src/MonitorKing.Agent
```

Puis ouvrir <http://localhost:5757>. Prérequis : SDK .NET 8, Windows 10/11. Les droits administrateur ne sont pas nécessaires.

## Ce que fait le prototype

| Onglet | Contenu |
|---|---|
| **Accueil** | Écran personnalisable : *Personnaliser* → ajouter, déplacer, redimensionner ou retirer des widgets. La disposition est enregistrée dans la base locale. |
| **Pourquoi ça rame ?** | Verdict en direct : quelle ressource sature, quelle application en est responsable, et ce que Windows a signalé à côté. |
| **Applications** | Consommation par application (processeur, RAM, E/S, GPU, VRAM), triable. Les `svchost` sont séparés par service, et les processus WebView2 sont rattachés à l'application qui les lance (ex. Clipchamp). |
| **Processeur & mémoire** | Charge par cœur, temps passé dans les pilotes (DPC), mémoire engagée, pagination (défauts durs), activité et temps de réponse de chaque disque. |
| **Carte graphique** | Charge par moteur (3D, vidéo, calcul), VRAM, températures, puissance, ventilateurs. |
| **Capteurs** | Tout ce que LibreHardwareMonitor lit sur la machine. |
| **Réseau** | Débit, qualité et puissance du signal Wi-Fi, canal, débit de liaison. |
| **Journal** | Événements Windows utiles : plantages (1000), gels (1002), arrêts brutaux (41, 6008), écrans bleus, mémoire virtuelle épuisée (2004), erreurs disque, WHEA, bridage CPU (37), plantage du pilote graphique (4101), plus les gels détectés par l'agent. Les 30 derniers jours sont importés dès le premier lancement. |
| **Historique** | Courbes jusqu'à 7 jours. Un cliquer-glisser sur une période l'analyse : verdict et applications responsables à ce moment-là. |

## Principes (issus de la session BMAD *forge-idea*)

- **L'agent ne fait qu'observer.** Aucune route de l'API n'agit sur le PC, et l'agent n'exécute aucune commande reçue. Il écoute uniquement sur `localhost`, avec filtrage de l'en-tête `Host`.
- **Les métriques sont modulaires.** Un collecteur (`ICollector`) déclare ses `MetricDef`, et le dashboard les découvre via `/api/metrics`. Ajouter une métrique revient à écrire un collecteur.
- **L'affichage est modulaire.** Les onglets et l'accueil utilisent le même catalogue de widgets (`wwwroot/js/widgets.js`).
- **Prévu, mais pas encore fait :** le code de l'agent sera signé avec une clé qui reste sur le PC de Victor. Le serveur pourra distribuer les mises à jour, mais pas en fabriquer une valide.

Décisions et pistes : `_bmad-output/forge/monitorking/.memlog.md` (session forge en pause, reprenable avec `/bmad-forge-idea`).

## Architecture

```
src/MonitorKing.Agent/            ASP.NET Core 8 (Kestrel sur localhost)
  Collectors/                     un fichier par famille de données
    SystemCollector               CPU par cœur, DPC, mémoire, disques physiques, réseau
    ProcessCollector              NtQuerySystemInformation : tous les processus en un appel, sans handle
    GpuCollector                  compteurs « GPU Engine » (comme le Gestionnaire des tâches)
    SensorCollector               LibreHardwareMonitorLib 0.9.6
    WifiCollector                 API Native Wifi
    HangCollector                 fenêtres « Ne répond pas » (IsHungAppWindow) et durée des gels
  EventLogService                 import des 30 derniers jours, puis abonnement (pas de relecture périodique)
  CollectorHost                   tick de 2 s, 30 min en mémoire, agrégats de 10 s (moyenne + pic) dans SQLite
  Diagnosis/DiagnosisEngine       règles explicables (« pourquoi ça rame ? »), en direct ou sur une période
  Storage/Database                SQLite : %LOCALAPPDATA%\MonitorKing\monitorking.db
  wwwroot/                        dashboard en HTML/CSS/JS (modules ES), sans build ni CDN : fonctionne hors ligne
```

API : `GET /api/info | status | metrics | live | series | processes | events | diagnosis`, `GET/PUT /api/layouts/{id}`.

Rétention : 14 jours pour les mesures, 90 jours pour les événements (`appsettings.json`).

## Limites connues

- **Températures CPU et carte mère :** il faut lancer l'agent en administrateur, avec le pilote signé [PawnIO](https://pawnio.eu) installé. L'agent n'installe jamais de pilote lui-même. Le GPU fonctionne sans droits admin.
- **Colonne « Disque / E/S » :** les compteurs par processus de Windows incluent aussi le réseau. Une attribution disque exacte demanderait ETW, donc les droits admin.
- **Journal de démarrage** (Diagnostics-Performance) : réservé aux administrateurs.
- **Lancement :** l'agent tourne comme une application dans la session de l'utilisateur, pas encore comme un service Windows. Un service tournerait dans la session 0 et ne verrait pas les fenêtres gelées : il faudra lui adjoindre un petit assistant dans la session de l'utilisateur.

## Licence

MIT, voir [LICENSE](LICENSE). Composants tiers :
- [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) (MPL 2.0), via NuGet ;
- l'outillage [BMAD Method](https://github.com/bmad-code-org/BMAD-METHOD) (MIT, © BMad Code, LLC) dans `_bmad/` et `.claude/skills/`.

## Prochaines étapes envisagées

1. Synchronisation vers le serveur auto-hébergé : rattrapage par paquets compressés avec curseur, et temps réel par WebSocket. La connexion part toujours de l'agent vers le serveur, avec un jeton par machine.
2. Signature des versions de l'agent et vérification à l'installation.
3. Service Windows avec assistant dans la session utilisateur, et ETW pour attribuer les accès disque par processus.
4. Serveur MCP pour interroger l'historique des machines depuis Claude.
