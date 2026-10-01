# MonitorKing — idée initiale

## Projet
Outil de monitoring et de diagnostic PC (Windows).

## Problème
Comprendre en temps réel pourquoi un PC ralentit (appli qui freeze, disque saturé, RAM pleine, surchauffe, erreurs) et pouvoir diagnostiquer à distance les PC de proches.

Exemple vécu : Clipchamp a fortement ralenti le PC, et le terminer a tout débloqué instantanément, sans que je sache ce qui saturait.

## Pistes d'architecture déjà discutées
- Agent Windows en C#/.NET : capteurs matériels via LibreHardwareMonitorLib, compteurs de performance par processus, journaux d'événements (1000, 1002, 2004, Kernel-Power 41, erreurs disque/WHEA), détection des fenêtres « Ne répond pas », ETW plus tard.
- Local-first / store-and-forward : SQLite locale avec rétention, dashboard local sur localhost (mode hors ligne).
- Synchronisation vers mon serveur auto-hébergé : rattrapage à la demande par paquets compressés avec curseur, et mode streaming temps réel via WebSocket.
- L'agent se connecte toujours vers le serveur (aucun port ouvert côté PC), en HTTPS via mon reverse proxy, avec un jeton par machine.
- Même dashboard web en local et sur le serveur.
- À terme : serveur MCP pour que Claude puisse interroger l'historique des machines.

## Contraintes
- Bande passante faible
- Projet perso
- Je suis développeur web

## Questions ouvertes
- Stack backend
- Nom du produit
- Périmètre de la V1
