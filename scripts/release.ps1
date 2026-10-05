#Requires -Version 5.1
# Construit, signe et publie une version de l'agent que les copies installées installent toutes seules.
# Le numéro vient de <Version> dans MonitorKing.Agent.csproj : le tag, l'archive et le manifeste en découlent.
# La signature se fait ici, sur ce PC : la clé privée ne va jamais sur GitHub.
param(
    [Parameter(Mandatory = $true)] [string]$NotesFile,
    [string]$Title
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$repoUrl = 'https://github.com/VictorMusta/MonitorKing'
$project = 'src/MonitorKing.Agent/MonitorKing.Agent.csproj'
$manifestName = 'update-manifest.txt'
$keyPath = Join-Path $env:USERPROFILE '.monitorking\release-signing-key.xml'

if (-not (Test-Path $NotesFile)) { throw "Fichier de notes introuvable : $NotesFile" }
$NotesFile = (Resolve-Path $NotesFile).Path

if ((Get-Content $project -Raw) -notmatch '<Version>(\d+\.\d+\.\d+)</Version>') { throw "<Version> introuvable dans $project" }
$version = $Matches[1]
# L'agent reconstruit le tag à partir du numéro lu dans le manifeste : « 0.4.00 » le mènerait à un tag qui n'existe pas.
if (([version]$version).ToString() -ne $version) { throw "La version « $version » n'est pas sous forme canonique." }
$tag = "v$version"
if (-not $Title) { $Title = "MonitorKing $tag" }

# Les agents installés refusent tout manifeste qui n'est pas signé par la clé dont ils embarquent la moitié publique.
# Publier avec une autre clé les couperait de leurs mises à jour sans rien dire : ne jamais en générer une pour passer.
if (-not (Test-Path $keyPath)) { throw "Clé de signature introuvable : $keyPath. Restaure-la depuis sa sauvegarde ; les agents installés n'en acceptent aucune autre." }
$rsa = New-Object System.Security.Cryptography.RSACng
$rsa.FromXmlString([IO.File]::ReadAllText($keyPath))
$publicModulus = [Convert]::ToBase64String($rsa.ExportParameters($false).Modulus)
if (-not (Get-Content 'src/MonitorKing.Updater/UpdateSignature.cs' -Raw).Contains("`"$publicModulus`"")) {
    throw 'La clé de signature ne correspond pas à la clé publique embarquée dans UpdateSignature.cs.'
}

if (git status --porcelain) { throw "L'arbre git n'est pas propre : commite d'abord, la release doit correspondre à un commit." }
git fetch origin --tags --quiet
if ($LASTEXITCODE -ne 0) { throw 'git fetch a échoué' }
$head = git rev-parse HEAD
if ($head -ne (git rev-parse '@{u}')) { throw "HEAD n'est pas poussé : pousse d'abord, le tag est créé sur le dépôt distant." }
if (git tag --list $tag) { throw "Le tag $tag existe déjà : augmente <Version>." }

node src/MonitorKing.Dashboard/check.mjs
if ($LASTEXITCODE -ne 0) { throw 'Le tableau de bord ne se charge pas' }
dotnet test MonitorKing.sln -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Des tests échouent' }

$stage = Join-Path $root 'dist\release'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
$agentDir = Join-Path $stage 'MonitorKing-Agent'
dotnet publish $project -c Release -r win-x64 --self-contained true -o $agentDir --nologo
if ($LASTEXITCODE -ne 0) { throw 'La publication a échoué' }
Copy-Item deploy/agent/inscription.cmd, deploy/agent/demarrage-windows.cmd, deploy/agent/demarrage-windows.ps1 $agentDir
$agentExe = Join-Path $agentDir 'MonitorKing.Agent.exe'

# L'agent publié (autonome) n'embarque pas forcément les mêmes composants que celui de développement :
# on le lance pour de vrai et on interroge quelques routes avant de publier quoi que ce soit.
$agent = Start-Process $agentExe -PassThru -ArgumentList '--MonitorKing:Port=5799', "`"--MonitorKing:DataDirectory=$(Join-Path $stage 'essai')`"", '--MonitorKing:AutoUpdate=false'
try {
    $ready = $false
    for ($i = 0; $i -lt 60 -and -not $ready; $i++) {
        Start-Sleep -Milliseconds 500
        try { $ready = (Invoke-WebRequest -UseBasicParsing http://localhost:5799/api/info).StatusCode -eq 200 } catch { }
    }
    if (-not $ready) { throw "L'agent publié ne répond pas sur /api/info" }
    foreach ($path in '/', '/api/privacy', '/api/events', '/api/update', '/api/breakdown?resource=cpu&minutes=30') {
        Invoke-WebRequest -UseBasicParsing "http://localhost:5799$path" | Out-Null
    }
}
finally { Stop-Process -Id $agent.Id -Force -ErrorAction SilentlyContinue }

$zipName = "MonitorKing-Agent-$tag-win-x64.zip"
$zip = Join-Path $stage $zipName
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($agentDir, $zip, [IO.Compression.CompressionLevel]::Optimal, $true)

# Format figé (voir UpdateManifest.cs) : la signature couvre exactement les octets qui suivent sa ligne.
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
$body = [Text.Encoding]::ASCII.GetBytes("version=$version`n$zipName=$((Get-Item $zip).Length):$hash`n")
$signature = [Convert]::ToBase64String($rsa.SignData($body, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1))
$rsa.Dispose()
$manifest = Join-Path $stage $manifestName
[IO.File]::WriteAllBytes($manifest, [Text.Encoding]::ASCII.GetBytes("signature=$signature`n") + $body)

# Dernier contrôle avant d'envoyer : l'agent que les gens vont recevoir accepte-t-il ce manifeste et cette archive ?
$check = Start-Process $agentExe -Wait -PassThru -ArgumentList '--verifier-manifeste', "`"$manifest`"", "`"$zip`""
if ($check.ExitCode -ne 0) { throw "L'agent publié refuse son propre manifeste (code $($check.ExitCode), voir AgentUpdate.Verify). Rien n'a été publié." }

gh release create $tag $zip $manifest --target $head --title $Title --notes-file $NotesFile
if ($LASTEXITCODE -ne 0) { throw 'gh release create a échoué' }

# Ce que les agents installés vont réellement télécharger doit être exactement ce qui vient d'être construit et signé.
$live = @{
    "$repoUrl/releases/latest/download/$manifestName" = $manifest
    "$repoUrl/releases/download/$tag/$zipName"        = $zip
}
for ($attempt = 1; $attempt -le 5; $attempt++) {
    $matching = 0
    foreach ($url in $live.Keys) {
        try {
            $published = Join-Path $stage 'en-ligne.tmp'
            Invoke-WebRequest $url -OutFile $published -UseBasicParsing
            if ((Get-FileHash $published -Algorithm SHA256).Hash -eq (Get-FileHash $live[$url] -Algorithm SHA256).Hash) { $matching++ }
        } catch {
        }
    }
    if ($matching -eq $live.Count) {
        Write-Host "Version $tag publiée : le manifeste signé et l'archive en ligne sont bien ceux qui viennent d'être construits."
        exit 0
    }
    Start-Sleep -Seconds 5
}
throw "La release $tag est publiée, mais le manifeste ou l'archive en ligne ne correspond pas à ce qui a été construit. Vérifie ses fichiers sur GitHub."
