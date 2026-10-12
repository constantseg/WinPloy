# Exécuté sur le poste cible dans la session WinRM (Windows PowerShell 5.1).
# Mode de transfert SMB : le poste monte le partage avec les identifiants fournis et copie
# lui-même le dossier du paquet. Le fichier passe du serveur de fichiers au poste en un seul
# saut, au lieu de traverser le poste d'administration bloc par bloc.
#
# Les identifiants sont indispensables : la session WinRM n'a pas d'identité réseau à présenter
# au serveur de fichiers (problème du double saut). Ils arrivent ici en SecureString, chiffrés
# par la clé de session.
#
# Comme le poste lit le dépôt directement, l'empreinte du dossier est revérifiée ici, après
# la copie : le contrôle fait sur le poste d'administration ne couvrirait pas ce chemin.
#
# $R.Source     : chemin UNC du dossier du paquet
# $R.StageDir   : dossier de destination sur le poste
# $R.Credential : identifiants d'accès au partage
# $R.Items      : objets { Rel, Sha } attendus ; vide = pas de contrôle d'empreinte
# $R.Text       : messages d'erreur, dans la langue de l'interface
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

$source = [string]$R.Source
$stage = [string]$R.StageDir

if ($source -notmatch '^\\\\[^\\]+\\[^\\]+') {
    throw (Format-Text UncExpected $source)
}

# \\serveur\partage\Paquet -> racine \\serveur\partage, reste Paquet
$parts = $source.TrimEnd('\') -split '\\+' | Where-Object { $_ }
$shareRoot = '\\{0}\{1}' -f $parts[0], $parts[1]
$relative = if ($parts.Count -gt 2) { ($parts[2..($parts.Count - 1)]) -join '\' } else { '' }
if (-not $relative) {
    throw (Format-Text ShareRoot $source)
}

$driveName = 'WinPloySrc' + [guid]::NewGuid().ToString('N').Substring(0, 8)
$null = New-PSDrive -Name $driveName -PSProvider FileSystem -Root $shareRoot -Credential $R.Credential -ErrorAction Stop
try {
    $from = Join-Path "${driveName}:" $relative
    if (-not (Test-Path -LiteralPath $from -PathType Container)) {
        throw (Format-Text ShareFolderNotFound $source)
    }

    # Copy-Item -Recurse recrée le dossier source dans la destination. Le contenu est donc
    # déposé dans un dossier d'atterrissage puis remonté, sans supposer le nom du dossier.
    $landing = Join-Path $stage '_winploy_pull'
    [void][IO.Directory]::CreateDirectory($landing)
    Copy-Item -LiteralPath $from -Destination $landing -Recurse -Force

    $inner = @(Get-ChildItem -LiteralPath $landing -Force -Directory)
    if ($inner.Count -ne 1) {
        throw (Format-Text UnexpectedCopy $inner.Count)
    }
    Get-ChildItem -LiteralPath $inner[0].FullName -Force | Move-Item -Destination $stage -Force
    Remove-Item -LiteralPath $landing -Recurse -Force
} finally {
    Remove-PSDrive -Name $driveName -Force -ErrorAction SilentlyContinue
}

$expected = @($R.Items)
if ($expected.Count -gt 0) {
    $known = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($item in $expected) {
        $rel = [string]$item.Rel
        [void]$known.Add($rel)
        $file = Join-Path $stage $rel
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
            throw (Format-Text IntegrityMissing $rel)
        }
        $actual = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
        if ($actual -ne ([string]$item.Sha).ToUpperInvariant()) {
            throw (Format-Text IntegrityMismatch $rel)
        }
    }

    $prefix = $stage.TrimEnd('\') + '\'
    foreach ($file in Get-ChildItem -LiteralPath $stage -Recurse -File -Force) {
        $rel = $file.FullName.Substring($prefix.Length)
        if (-not $known.Contains($rel)) {
            throw (Format-Text IntegrityUnexpected $rel)
        }
    }
}
