#Requires -Version 5.1
# Crée la clé privée qui signe les manifestes de mise à jour. À lancer une seule fois.
# Les agents installés ne font confiance qu'à la clé publique correspondante (src/MonitorKing.Updater/UpdateSignature.cs) :
# si cette clé est perdue, ils ne se mettront plus jamais à jour tout seuls.
$ErrorActionPreference = 'Stop'

$keyPath = Join-Path $env:USERPROFILE '.monitorking\release-signing-key.xml'
if (Test-Path $keyPath) {
    throw "Une clé de signature existe déjà : $keyPath. La remplacer couperait tous les agents installés de leurs mises à jour ; supprime-la toi-même si c'est vraiment ce que tu veux."
}

$rsa = New-Object System.Security.Cryptography.RSACng 3072
try {
    New-Item -ItemType Directory -Force -Path (Split-Path $keyPath) | Out-Null
    [IO.File]::WriteAllText($keyPath, $rsa.ToXmlString($true))

    Write-Host "Clé privée écrite dans $keyPath"
    Write-Host 'Sauvegarde-la en lieu sûr. Ne la commite jamais et ne la dépose jamais sur GitHub.'
    Write-Host ''
    Write-Host 'Clé publique à coller dans src/MonitorKing.Updater/UpdateSignature.cs (ReleaseModulus) :'
    Write-Host ([Convert]::ToBase64String($rsa.ExportParameters($false).Modulus))
} finally {
    $rsa.Dispose()
}
