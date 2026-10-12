# Exécuté sur le poste cible dans la session WinRM (Windows PowerShell 5.1).
# Réception du dossier d'un paquet, avec un cache adressé par empreinte.
#
# Un fichier est identifié par son SHA-256, pas par son chemin : il n'est envoyé que s'il
# n'est pas déjà dans le cache du poste. Un même paquet redéployé, une relance après échec
# ou deux paquets qui partagent un fichier ne coûtent donc rien sur le réseau.
# Le cache vit sous le dossier de travail protégé (SYSTEM et administrateurs seulement),
# et l'empreinte de chaque entrée est revérifiée avant usage : un fichier modifié dans le
# cache est ignoré, pas exécuté.
#
# $R.Mode        : 'Plan' | 'Chunk' | 'Commit' | 'Materialize'
# $R.CacheDir    : dossier du cache
# $R.StageDir    : dossier où le paquet est reconstitué (Materialize)
# $R.Items       : objets { Rel, Sha } (Plan, Materialize)
# $R.Dirs        : sous-dossiers relatifs à recréer, même vides (Materialize)
# $R.Sha         : empreinte du fichier en cours (Chunk, Commit)
# $R.Data        : bloc d'octets (Chunk)
# $R.Compressed  : bloc compressé en gzip (Chunk)
# $R.Append      : ajouter au fichier en cours au lieu de le créer (Chunk)
# $R.Text        : messages d'erreur, dans la langue de l'interface (Commit, Materialize)
param($R)

$ErrorActionPreference = 'Stop'
$T = $R.Text

# Messages dans la langue de l'interface : table { clé = modèle {0}, {1}... } fournie par WinPloy.
# Sans table, la clé et les valeurs restent lisibles.
function Format-Text {
    param([string]$Key)
    $template = if ($T) { [string]$T[$Key] } else { '' }
    if ($template) { $template -f $args } else { (@($Key) + $args) -join ' ' }
}

function Get-Sha256 {
    param([string]$Path)
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Test-CacheEntry {
    param([string]$Path, [string]$Sha)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $false }
    return (Get-Sha256 $Path) -eq $Sha.ToUpperInvariant()
}

$cache = [string]$R.CacheDir
[void][IO.Directory]::CreateDirectory($cache)

switch ([string]$R.Mode) {

    'Plan' {
        # Blocs d'un envoi interrompu : ils ne sont jamais réutilisés tels quels.
        Get-ChildItem -LiteralPath $cache -Filter '*.part' -File -ErrorAction SilentlyContinue |
            Remove-Item -Force -ErrorAction SilentlyContinue

        $missing = New-Object System.Collections.Generic.List[string]
        $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
        foreach ($item in @($R.Items)) {
            $sha = [string]$item.Sha
            if (-not $seen.Add($sha)) { continue }
            if (-not (Test-CacheEntry (Join-Path $cache $sha) $sha)) { $missing.Add($sha) }
        }
        return $missing.ToArray()
    }

    'Chunk' {
        $part = Join-Path $cache ([string]$R.Sha + '.part')
        # object[] possible selon la sérialisation de la session : la conversion fixe le type.
        [byte[]]$bytes = $R.Data

        if ([bool]$R.Compressed -and $bytes.Length -gt 0) {
            $inStream = New-Object IO.MemoryStream(, $bytes)
            $outStream = New-Object IO.MemoryStream
            try {
                $gzip = New-Object IO.Compression.GzipStream($inStream, [IO.Compression.CompressionMode]::Decompress)
                try { $gzip.CopyTo($outStream) } finally { $gzip.Dispose() }
                $bytes = $outStream.ToArray()
            } finally {
                $outStream.Dispose()
                $inStream.Dispose()
            }
        }

        $mode = if ([bool]$R.Append) { [IO.FileMode]::Append } else { [IO.FileMode]::Create }
        $stream = [IO.File]::Open($part, $mode, [IO.FileAccess]::Write)
        try {
            if ($bytes.Length) { $stream.Write($bytes, 0, $bytes.Length) }
        } finally {
            $stream.Dispose()
        }
        return
    }

    'Commit' {
        $sha = [string]$R.Sha
        $part = Join-Path $cache ($sha + '.part')
        $final = Join-Path $cache $sha
        $actual = Get-Sha256 $part
        if ($actual -ne $sha.ToUpperInvariant()) {
            Remove-Item -LiteralPath $part -Force -ErrorAction SilentlyContinue
            throw (Format-Text TransferCorrupted $actual $sha)
        }
        [IO.File]::Delete($final)
        [IO.File]::Move($part, $final)
        return
    }

    'Materialize' {
        $stage = [string]$R.StageDir
        foreach ($dir in @($R.Dirs)) {
            [void][IO.Directory]::CreateDirectory((Join-Path $stage ([string]$dir)))
        }
        foreach ($item in @($R.Items)) {
            $sha = [string]$item.Sha
            $source = Join-Path $cache $sha
            if (-not (Test-CacheEntry $source $sha)) {
                throw (Format-Text NotInCache $item.Rel)
            }
            $destination = Join-Path $stage ([string]$item.Rel)
            [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
            [IO.File]::Copy($source, $destination, $true)
        }
        return
    }

    default { throw (Format-Text UnknownMode $R.Mode) }
}
