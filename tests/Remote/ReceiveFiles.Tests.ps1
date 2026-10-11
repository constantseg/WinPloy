# Contrôle du protocole de réception de ReceiveFiles.ps1 : cache adressé par empreinte,
# décompression gzip, refus d’une entrée de cache modifiée et d’un transfert corrompu.
# Ce script ne touche ni au réseau ni aux ACL : il s’exécute avec n’importe quel PowerShell 5.1+.
#   pwsh -NoProfile -File tests/Remote/ReceiveFiles.Tests.ps1

$ErrorActionPreference = 'Stop'

$script = (Resolve-Path (Join-Path $PSScriptRoot '../../src/WinPloy.Core/Deployment/Remote/ReceiveFiles.ps1')).Path

$root  = Join-Path ([IO.Path]::GetTempPath()) ("recv_" + [guid]::NewGuid().ToString('N'))
$cache = Join-Path $root 'cache'
$stage = Join-Path $root 'stage'
$null = New-Item -ItemType Directory -Path $stage -Force

# Fichier source : assez gros pour que gzip gagne, et compressible.
$payload = [Text.Encoding]::UTF8.GetBytes(('Write-Host bonjour' * 500))
$sha = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($payload)).Replace('-','').ToLowerInvariant()

function Invoke-Recv { param($R) & $script -R $R }

$items = @([pscustomobject]@{ Rel = 'sub\install.ps1'; Sha = $sha })

# 1. Plan : le fichier manque.
$missing = @(Invoke-Recv ([pscustomobject]@{ Mode='Plan'; CacheDir=$cache; StageDir=$stage; Items=$items }))
if ($missing.Count -ne 1 -or $missing[0] -ne $sha) { throw "Plan initial : attendu 1 manquant, obtenu $($missing -join ',')" }
Write-Host "OK  Plan signale le fichier absent du cache"

# 2. Chunk compressé en gzip (comme le fait RemoteFileCopier).
$ms = New-Object IO.MemoryStream
$gz = New-Object IO.Compression.GzipStream($ms, [IO.Compression.CompressionLevel]::Fastest, $true)
$gz.Write($payload, 0, $payload.Length); $gz.Dispose()
$compressed = $ms.ToArray(); $ms.Dispose()
if ($compressed.Length -ge $payload.Length) { throw "Le jeu d'essai devrait se compresser" }
Invoke-Recv ([pscustomobject]@{ Mode='Chunk'; CacheDir=$cache; Sha=$sha; Data=$compressed; Compressed=$true; Append=$false })
Write-Host "OK  Chunk gzip reçu et décompressé ($($compressed.Length) -> $($payload.Length) octets)"

# 3. Commit : l'empreinte est revérifiée côté poste.
Invoke-Recv ([pscustomobject]@{ Mode='Commit'; CacheDir=$cache; Sha=$sha })
if (-not (Test-Path (Join-Path $cache $sha))) { throw "Commit n'a pas déposé l'entrée de cache" }
Write-Host "OK  Commit vérifie l'empreinte et dépose l'entrée de cache"

# 4. Plan à nouveau : plus rien à envoyer.
$missing = @(Invoke-Recv ([pscustomobject]@{ Mode='Plan'; CacheDir=$cache; StageDir=$stage; Items=$items }))
if ($missing.Count -ne 0) { throw "Plan après mise en cache : attendu 0 manquant, obtenu $($missing.Count)" }
Write-Host "OK  Plan ne redemande pas un fichier déjà en cache"

# 5. Materialize : reconstitution avec sous-dossier et dossier vide.
Invoke-Recv ([pscustomobject]@{ Mode='Materialize'; CacheDir=$cache; StageDir=$stage; Items=$items; Dirs=@('vide') })
$built = Join-Path $stage 'sub\install.ps1'
if (-not (Test-Path $built)) { throw "Materialize n'a pas créé $built" }
$rebuilt = [IO.File]::ReadAllBytes($built)
if ([BitConverter]::ToString($rebuilt) -ne [BitConverter]::ToString($payload)) { throw "Contenu reconstitué différent de l'original" }
if (-not (Test-Path (Join-Path $stage 'vide') -PathType Container)) { throw "Le sous-dossier vide n'a pas été recréé" }
Write-Host "OK  Materialize reconstitue le fichier à l'octet près, sous-dossier vide compris"

# 6. Cache empoisonné : l'entrée est ignorée, pas utilisée.
[IO.File]::WriteAllText((Join-Path $cache $sha), 'charge utile hostile')
$missing = @(Invoke-Recv ([pscustomobject]@{ Mode='Plan'; CacheDir=$cache; StageDir=$stage; Items=$items }))
if ($missing.Count -ne 1) { throw "Une entrée de cache modifiée doit être redemandée" }
Write-Host "OK  Une entrée de cache modifiée est redemandée, jamais réutilisée"

# 7. Bloc corrompu : Commit refuse.
Invoke-Recv ([pscustomobject]@{ Mode='Chunk'; CacheDir=$cache; Sha=$sha; Data=[Text.Encoding]::UTF8.GetBytes('tronque'); Compressed=$false; Append=$false })
try {
    Invoke-Recv ([pscustomobject]@{ Mode='Commit'; CacheDir=$cache; Sha=$sha })
    throw "Commit aurait dû refuser un bloc corrompu"
} catch {
    if ($_.Exception.Message -notmatch 'corrompu') { throw "Message inattendu : $($_.Exception.Message)" }
}
Write-Host "OK  Commit refuse un transfert corrompu"

Remove-Item -LiteralPath $root -Recurse -Force
Write-Host ""
Write-Host "ReceiveFiles.ps1 : 7/7"
