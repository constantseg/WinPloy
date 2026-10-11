# Exécuté sur le poste cible dans la session WinRM (Windows PowerShell 5.1).
# Crée un dossier de travail dont seuls SYSTEM et les administrateurs locaux ont les droits,
# et retourne son chemin.
#
# Pourquoi : les scripts posés dans ce dossier sont exécutés en SYSTEM par une tâche planifiée.
# C:\Windows\Temp est accessible en écriture aux utilisateurs standards, et ses sous-dossiers
# héritent de ces droits : sans DACL explicite, un utilisateur du poste pourrait remplacer un
# script juste avant son exécution et obtenir SYSTEM.
#
# $R.Parent    : dossier parent. Vide = racine de travail ($env:windir\Temp\WinPloy).
# $R.PruneDays : âge au-delà duquel les fichiers du cache sont supprimés (0 = pas de purge).
param($R)

$ErrorActionPreference = 'Stop'

$systemSid = New-Object System.Security.Principal.SecurityIdentifier 'S-1-5-18'
$adminsSid = New-Object System.Security.Principal.SecurityIdentifier 'S-1-5-32-544'

function Protect-Directory {
    param([string]$Path)

    $acl = New-Object System.Security.AccessControl.DirectorySecurity
    # $true : l'héritage est coupé. $false : les règles héritées ne sont pas recopiées.
    $acl.SetAccessRuleProtection($true, $false)
    $inherit = [System.Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    foreach ($sid in @($systemSid, $adminsSid)) {
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
            $sid, 'FullControl', $inherit, [System.Security.AccessControl.PropagationFlags]::None, 'Allow')))
    }
    [IO.Directory]::SetAccessControl($Path, $acl)

    # Le propriétaire garde un droit implicite de modifier la DACL : il doit être le groupe
    # Administrateurs, pas celui qui a créé le dossier le premier. Demande un privilège
    # que la session peut ne pas détenir : la DACL ci-dessus reste l'essentiel.
    try {
        $owner = New-Object System.Security.AccessControl.DirectorySecurity
        $owner.SetOwner($adminsSid)
        [IO.Directory]::SetAccessControl($Path, $owner)
    } catch {
    }
}

function Test-Trustworthy {
    param([string]$Path)

    $item = Get-Item -LiteralPath $Path -Force
    # Lien ou jonction : la cible réelle est ailleurs, donc hors de tout contrôle.
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { return $false }

    $sid = (Get-Acl -LiteralPath $Path).GetOwner([System.Security.Principal.SecurityIdentifier]).Value
    return $sid -eq $systemSid.Value -or $sid -eq $adminsSid.Value
}

$parent = [string]$R.Parent
if ([string]::IsNullOrEmpty($parent)) {
    $path = Join-Path $env:windir 'Temp\WinPloy'
} else {
    $path = Join-Path $parent ("stage_{0}" -f [guid]::NewGuid().ToString('N'))
}

# Un dossier déjà présent peut avoir été créé par n'importe qui : il n'est gardé que
# s'il appartient à SYSTEM ou aux administrateurs.
if (Test-Path -LiteralPath $path) {
    if (-not (Test-Trustworthy $path)) {
        Remove-Item -LiteralPath $path -Recurse -Force
    }
}

[void][IO.Directory]::CreateDirectory($path)
Protect-Directory $path

$pruneDays = [int]$R.PruneDays
if ($pruneDays -gt 0) {
    $cache = Join-Path $path 'cache'
    if (Test-Path -LiteralPath $cache) {
        $limit = (Get-Date).AddDays(-$pruneDays)
        Get-ChildItem -LiteralPath $cache -File -ErrorAction SilentlyContinue |
            Where-Object { $_.LastWriteTime -lt $limit } |
            Remove-Item -Force -ErrorAction SilentlyContinue
    }
}

$path
