@echo off
rem MonitorKing - inscription de ce PC aupres du serveur (a lancer une seule fois).
rem Le code d'inscription est donne par la personne qui gere le serveur ; il ne sert qu'une fois.
cd /d "%~dp0"
echo.
echo  MonitorKing - inscription de ce PC
echo  ----------------------------------
echo  Par defaut, ce PC envoie ses donnees en mode discret : les noms de tes applications
echo  sont remplaces par des pseudonymes. Toi, tu vois tout sur http://localhost:5757
echo.
set /p CODE= Code d'inscription (exemple ABCD-EFGH) :
if "%CODE%"=="" (
  echo  Aucun code saisi : rien n'a ete fait.
  pause
  exit /b 1
)

start "MonitorKing" /min MonitorKing.Agent.exe --MonitorKing:Server:Url=https://monitorking-victor.duckdns.org --MonitorKing:Server:EnrollmentCode=%CODE%

echo.
set /p AUTO= Lancer MonitorKing automatiquement au demarrage de Windows ? (O/N) :
if /i "%AUTO%"=="O" (
  powershell -NoProfile -Command "$s = (New-Object -ComObject WScript.Shell).CreateShortcut([Environment]::GetFolderPath('Startup') + '\MonitorKing.lnk'); $s.TargetPath = '%~dp0MonitorKing.Agent.exe'; $s.WorkingDirectory = '%~dp0'; $s.WindowStyle = 7; $s.Save()"
  echo  Raccourci ajoute au demarrage de Windows.
)

echo.
echo  L'agent demarre ; le tableau de bord va s'ouvrir dans le navigateur.
timeout /t 6 >nul
start "" http://localhost:5757
