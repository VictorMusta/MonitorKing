# MonitorKing : lancer l'agent à chaque ouverture de session Windows, avec les droits administrateur
# (nécessaires aux températures du processeur et de la carte mère, avec le pilote PawnIO).
# Copie l'agent (le dossier de ce script) dans C:\Program Files\MonitorKing, où il ne peut pas être modifié
# sans droits administrateur, puis crée la tâche planifiée « MonitorKing » : sans fenêtre, sans limite de durée,
# sur secteur comme sur batterie. Relancer ce script après une mise à jour remplace l'agent installé.
# Désinstaller : demarrage-windows.cmd /desinstaller (les données de %LOCALAPPDATA%\MonitorKing restent).
param([switch]$Desinstaller, [string]$Sid)

$ErrorActionPreference = 'Stop'
$taskName = 'MonitorKing'
$target = Join-Path $env:ProgramFiles 'MonitorKing'

# Il faut les droits administrateur : le script se relance lui-même, Windows demande confirmation.
# Il transmet le SID de la personne connectée : la tâche sera la sienne, même si l'élévation se fait avec un autre compte.
# (Le SID plutôt que le nom : avec un nom de PC accentué, Windows qualifie mal « PÉCÉ\victo ».)
$identity = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -Sid $([Security.Principal.WindowsIdentity]::GetCurrent().User.Value)"
    if ($Desinstaller) { $arguments += ' -Desinstaller' }
    Start-Process powershell -Verb RunAs -ArgumentList $arguments
    exit
}

if (-not $Sid) { $Sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value } # lancé directement en administrateur

function Stop-Agent {
    try { Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue } catch { }
    Get-Process -Name 'MonitorKing.Agent' -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 2
}

try {
    if ($Desinstaller) {
        Stop-Agent
        try { Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue } catch { }
        if (Test-Path $target) { Remove-Item $target -Recurse -Force }
        Write-Host "MonitorKing ne se lance plus au démarrage. Tes données restent dans $env:LOCALAPPDATA\MonitorKing." -ForegroundColor Green
    }
    else {
        $source = $PSScriptRoot
        if (-not (Test-Path (Join-Path $source 'MonitorKing.Agent.exe'))) {
            throw "MonitorKing.Agent.exe introuvable à côté de ce script ($source)."
        }

        $user = try { ([Security.Principal.SecurityIdentifier]$Sid).Translate([Security.Principal.NTAccount]).Value } catch { $Sid }

        Write-Host "Arrêt de l'agent s'il tourne déjà…"
        Stop-Agent

        if ($source.TrimEnd('\') -ne $target.TrimEnd('\')) {
            Write-Host "Copie de l'agent dans $target…"
            New-Item -ItemType Directory -Force -Path $target | Out-Null
            Copy-Item -Path (Join-Path $source '*') -Destination $target -Recurse -Force
        }

        # Lancement automatique sans droits administrateur posé par inscription.cmd : remplacé par la tâche.
        $shortcut = Join-Path ([Environment]::GetFolderPath('Startup')) 'MonitorKing.lnk'
        if (Test-Path $shortcut) { Remove-Item $shortcut -Force }

        $action = New-ScheduledTaskAction -Execute (Join-Path $target 'MonitorKing.Agent.exe') -WorkingDirectory $target
        $trigger = New-ScheduledTaskTrigger -AtLogOn -User $Sid
        $trigger.Delay = 'PT30S' # laisser la session s'ouvrir d'abord
        $principal = New-ScheduledTaskPrincipal -UserId $Sid -LogonType Interactive -RunLevel Highest
        $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew
        Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force `
            -Description 'MonitorKing : agent de diagnostic en lecture seule, tableau de bord sur http://localhost:5757.' | Out-Null

        Write-Host 'Démarrage de l''agent…'
        Start-ScheduledTask -TaskName $taskName
        $ok = $false
        for ($i = 0; $i -lt 30 -and -not $ok; $i++) {
            Start-Sleep -Seconds 1
            try { Invoke-WebRequest -UseBasicParsing -TimeoutSec 2 http://localhost:5757/api/status | Out-Null; $ok = $true } catch { }
        }

        if ($ok) {
            Write-Host "C'est fait : MonitorKing tourne sans fenêtre et se lancera à chaque ouverture de session de $user." -ForegroundColor Green
            Write-Host 'Tableau de bord : http://localhost:5757'
        }
        else {
            Write-Host "La tâche est créée, mais l'agent ne répond pas encore sur http://localhost:5757 : regarde la tâche « MonitorKing » dans le Planificateur de tâches." -ForegroundColor Yellow
        }
    }
}
catch {
    Write-Host "Échec : $($_.Exception.Message)" -ForegroundColor Red
}

Read-Host 'Appuie sur Entrée pour fermer'
