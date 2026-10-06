# MonitorKing

Comprendre **pourquoi un PC Windows rame** (application gelée, disque saturé, RAM pleine, surchauffe, erreurs), en direct et après coup, sur son PC comme sur ceux de ses proches.

- **Agent** (Windows) : observe le PC, garde tout en local, sert un dashboard sur `http://localhost:5757`.
- **Serveur** (Linux, Docker) : reçoit les données des agents inscrits, en **mode discret** par défaut, et sert le même dashboard avec un sélecteur de PC.

## Lancer l'agent

```bash
dotnet run --project src/MonitorKing.Agent
```

Puis ouvrir <http://localhost:5757>. Prérequis : SDK .NET 8, Windows 10/11. Les droits administrateur ne sont pas nécessaires. En administrateur, avec le pilote [PawnIO](https://pawnio.eu) installé, l'agent lit aussi les températures du processeur et de la carte mère.

### Lancer l'agent au démarrage de Windows

```bash
dotnet publish src/MonitorKing.Agent -c Release -r win-x64 --self-contained true -o out/MonitorKing-Agent
```

Copier `deploy/agent/demarrage-windows.cmd` et `demarrage-windows.ps1` dans `out/MonitorKing-Agent` (ils sont déjà dans le zip des releases), puis double-cliquer sur `demarrage-windows.cmd`. Après confirmation de Windows, le script copie l'agent dans `C:\Program Files\MonitorKing` et crée la tâche planifiée « MonitorKing ». L'agent se lance alors à chaque ouverture de session, en administrateur et sans fenêtre (build Release). `demarrage-windows.cmd /desinstaller` retire tout sauf les données. Pour développer avec `dotnet run`, arrêter d'abord l'agent installé, qui occupe le port 5757.

## Ce que fait le dashboard

| Onglet | Contenu |
|---|---|
| **Mes PC** (serveur) | Les PC inscrits : en ligne ou non, verdict du moment, bouton « Ajouter un PC » qui génère un code d'inscription. |
| **Accueil** | Écran personnalisable : *Personnaliser* → ajouter, déplacer, redimensionner ou retirer des widgets. |
| **Pourquoi ça rame ?** | Verdict : quelle ressource sature, quelle application en est responsable, ce que Windows a signalé à côté. Rapport Markdown à copier pour Claude. |
| **Applications** | Répartition au fil du temps : part de chaque application dans le processeur, la RAM, le GPU, la VRAM ou les E/S (aires empilées, en % ou en valeurs, de 30 min à 7 jours). Puis la consommation en direct, triable. `svchost` séparés par service, processus WebView2 rattachés à leur application. |
| **Processeur & mémoire** | Charge par cœur, pilotes (DPC), mémoire engagée, pagination, activité et temps de réponse de chaque disque. |
| **Carte graphique**, **Capteurs** | GPU par moteur, VRAM, températures, ventilateurs. |
| **Réseau** | Qui utilise la connexion (répartition par application au fil du temps, relevée par le traçage réseau ETW de Windows, agent en administrateur), débit, Wi-Fi. |
| **Journal** | Plantages, gels, arrêts brutaux, écrans bleus, erreurs disque, WHEA, bridage CPU… importés dès le premier lancement (30 jours). |
| **Historique** | Jusqu'à 7 jours. Un cliquer-glisser sur une période l'analyse ; la colonne « Hausse E/S » désigne l'application qui s'active pendant un pic. |

## Confidentialité

- **Mode discret (par défaut)** : tout le détail reste sur le PC. Vers le serveur partent les mesures, les composants de Windows en clair (ce qui est installé sous `C:\Windows`, Defender…) et les autres applications **sous pseudonyme** (« Appli 7F3A9C », calculé avec une clé qui ne quitte jamais le PC). Ni titres de fenêtres, ni nom du Wi-Fi, ni messages Windows (chemins, noms d'utilisateur).
- **Retrouver une application** : sur le PC, *Confidentialité et envoi* (pied de page) donne la correspondance pseudonyme → application.
- **Clé de lecture (chiffrement de bout en bout)** : les vrais noms partent aussi, chiffrés en AES-256-GCM avec une clé qui reste sur le PC ; le serveur ne peut pas les lire. Si la personne donne sa clé (même fenêtre *Confidentialité et envoi*), le dashboard du serveur déchiffre les noms **dans le navigateur** (bouton *Noms masqués* en haut). Renouveler la clé retire l'accès.
- **Partage complet** : activable seulement depuis le PC, pour 1 h ou 24 h, puis retour automatique au mode discret.
- **Lecture seule** : l'agent n'exécute jamais de commande reçue ; le serveur ne peut rien lui demander. La connexion part toujours du PC.

## Mises à jour automatiques

L'agent installé se met à jour tout seul, sans rien demander et sans droits supplémentaires.

- **Ce qui est contacté** : uniquement `github.com/VictorMusta/MonitorKing/releases`, et l'hébergement de fichiers vers lequel GitHub redirige les téléchargements (`*.githubusercontent.com`). La requête ne contient aucun identifiant ni aucune information sur le PC.
- **Quand** : deux minutes après le démarrage de l'agent, puis toutes les six heures environ. Quand l'agent est à jour, cela se résume à une petite requête.
- **Ce qui est vérifié, dans cet ordre** : la signature du manifeste de la version (clé de publication RSA 3072 bits, dont la moitié privée n'est pas sur GitHub : pirater le dépôt ne suffit pas à pousser une mise à jour) ; un numéro de version strictement supérieur ; la taille et l'empreinte SHA-256 de l'archive téléchargée. Rien n'est écrit sur le disque avant la fin de ces vérifications.
- **Ce qui est installé** : la nouvelle version est posée entière à côté de l'installation, dans `.update\versions\x.y.z`, et prend le relais au **prochain démarrage** de l'agent. L'installation d'origine n'est jamais modifiée ; si la nouvelle version ne démarre pas, la précédente reprend la main.
- **Dossier d'installation protégé** (agent lancé sans droits administrateur depuis `C:\Program Files`) : l'agent ne contourne rien, il signale seulement qu'une version est disponible.
- **Désactiver** : *Confidentialité et envoi* (pied de page du dashboard local) → *Désactiver les mises à jour automatiques*, ou lancer l'agent avec `--MonitorKing:AutoUpdate=false`. Désactivé, l'agent ne contacte plus GitHub.
- **Journal** : une ligne par vérification dans `%LOCALAPPDATA%\MonitorKing\update.log`.

## Inscrire un PC sur le serveur

1. Dans le dashboard du serveur, *Mes PC* → *Ajouter un PC* → nom du PC → un code à usage unique (24 h).
2. **PC sans outils de développement** : télécharger le zip de l'agent dans les [Releases](https://github.com/VictorMusta/MonitorKing/releases) (.NET inclus), le dézipper, double-cliquer sur `inscription.cmd` et saisir le code. Les releases sont produites par `scripts/release.ps1` (voir *Publier une version de l'agent*).
3. **Depuis le code source**, lancer l'agent une première fois avec l'adresse du serveur et le code :

```bash
dotnet run --project src/MonitorKing.Agent -- --MonitorKing:Server:Url=https://monitorking.example.duckdns.org --MonitorKing:Server:EnrollmentCode=ABCD-EFGH
```

Le PC garde ensuite son propre jeton (le code ne sert plus) et envoie ses données par lots compressés toutes les 15 s, avec rattrapage s'il a été hors ligne.

## Déployer le serveur (Docker + Caddy)

```bash
docker compose -f deploy/docker-compose.yml up -d --build
```

Le conteneur écoute sur `127.0.0.1:8095`. Le bloc [deploy/Caddyfile.monitorking](deploy/Caddyfile.monitorking) protège le dashboard par mot de passe et laisse passer uniquement `/enroll` et `/ingest/*` vers les agents. Données dans le volume `monitorking_data` (30 jours de mesures).

Changer le mot de passe du dashboard : installer [deploy/change-password.sh](deploy/change-password.sh) sur le serveur (`/usr/local/bin/monitorking-password`), puis `ssh -t root@serveur monitorking-password` ; le mot de passe est saisi sur le serveur et seul son hash est écrit.

## Publier une version de l'agent

1. **Une seule fois** : `scripts/new-signing-key.ps1` crée la clé de signature dans `%USERPROFILE%\.monitorking\release-signing-key.xml` et affiche la clé publique à coller dans `src/MonitorKing.Updater/UpdateSignature.cs`. **Cette clé doit être sauvegardée hors du PC** : sans elle, les agents installés ne se mettront plus jamais à jour tout seuls. Ne jamais en générer une nouvelle pour contourner un échec, et ne jamais la déposer sur GitHub.
2. Augmenter `<Version>` dans `src/MonitorKing.Agent/MonitorKing.Agent.csproj` (seule source du numéro de version), commiter, pousser.
3. `powershell -ExecutionPolicy Bypass -File scripts/release.ps1 -NotesFile notes.md`. Le script refuse de partir si l'arbre git n'est pas propre et poussé, si le tag existe déjà ou si la clé manque. Il lance les tests, publie l'agent, l'essaie, signe le manifeste, crée la release GitHub, puis retélécharge ce qui est en ligne pour vérifier que c'est bien ce qu'il vient de construire.

Le format du manifeste (`update-manifest.txt`, décrit dans `UpdateManifest.cs`) et la disposition de `.update\versions` (`InstallLayout.cs`) sont figés : les agents déjà installés doivent pouvoir lire toutes les versions futures.

## Architecture

```
src/MonitorKing.Core/        moteur commun (sans Windows) : modèles, SQLite, diagnostic, rapports, contrat d'envoi,
                             refus des écritures venues d'un autre site (CSRF) sur les deux API
src/MonitorKing.Updater/     mise à jour automatique de l'agent, sans dépendance : manifeste signé, téléchargement, installation
tests/                       tests automatiques de la mise à jour et de la protection des API
src/MonitorKing.Agent/       Windows : collecteurs, journaux d'événements, mode discret, envoi vers le serveur
  Collectors/                CPU par cœur, processus (NtQuerySystemInformation), GPU, capteurs (LibreHardwareMonitor),
                             Wi-Fi, fenêtres « Ne répond pas »
  Privacy, UploadService     pseudonymisation, lots compressés avec curseur
src/MonitorKing.Server/      Linux : inscription, réception des lots, une base par PC, API /api/m/{id}/…
src/MonitorKing.Dashboard/   le dashboard (HTML/CSS/JS sans build ni CDN), servi par l'agent et par le serveur
deploy/                      docker-compose et bloc Caddy
scripts/                     publication d'une version signée de l'agent
```

`node src/MonitorKing.Dashboard/check.mjs` vérifie que chaque module du dashboard se charge (syntaxe, imports et exports). Le script de publication et le workflow *Vérifications* le lancent avant de compiler.

## Limites connues

- **Colonne « Disque / E/S »** : les compteurs par processus de Windows incluent le réseau ; une attribution disque exacte demanderait ETW. Le réseau seul a sa propre colonne (ETW, agent en administrateur ; sans ces droits, seul le débit total du PC est mesuré).
- **Agent lancé à la main** : pas encore de service Windows. Un service tournerait dans la session 0 et ne verrait pas les fenêtres gelées : il faudra un petit assistant dans la session de l'utilisateur.
- **Mises à jour de l'agent** : une version téléchargée n'est lancée qu'au démarrage suivant de l'agent (ouverture de session), et l'installation d'origine reste sur le disque à côté de la version en cours (environ 110 Mo de plus). Les agents installés avant la 0.4.0 n'ont pas la mise à jour automatique : ils doivent être réinstallés une dernière fois à la main.

Décisions et pistes : `_bmad-output/forge/monitorking/.memlog.md` (session BMAD *forge-idea*, reprenable avec `/bmad-forge-idea`).

## Licence

MIT, voir [LICENSE](LICENSE). Composants tiers :
- [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) (MPL 2.0), via NuGet ;
- l'outillage [BMAD Method](https://github.com/bmad-code-org/BMAD-METHOD) (MIT, © BMad Code, LLC) dans `_bmad/` et `.claude/skills/`.

## Prochaines étapes

1. Déploiement sur le VPS et inscription du premier PC.
2. Serveur MCP : interroger les machines depuis Claude et être prévenu quand un PC a un problème.
3. Agent en service Windows, mises à jour signées, ETW pour l'attribution disque.
