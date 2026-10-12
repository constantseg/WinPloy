# Exécuté sur le poste cible dans la session WinRM (Windows PowerShell 5.1).
# Paramètres envoyés par WinPloy :
#   WingetPkgs : objets { Id, Name }
#   CustomPkgs : objets { Id, Name, Leaf, StageDir, RunAs } (dossier déjà copié dans StageDir)
#   Action     : 'Install' ou 'Uninstall'
#   TimeoutMin : délai par paquet, en minutes
#   WorkRoot   : dossier de travail déjà créé et protégé par WinPloy (SYSTEM et administrateurs seulement) ;
#                les scripts intermédiaires y sont écrits avant d'être exécutés en SYSTEM
#   Text       : messages renvoyés, dans la langue de l'interface
param($WingetPkgs, $CustomPkgs, $Action, [int]$TimeoutMin, [string]$WorkRoot, $Text)

$ErrorActionPreference = 'Stop'
$results = New-Object System.Collections.Generic.List[object]
$workRoot = $WorkRoot
$T = $Text

# Messages dans la langue de l'interface : table { clé = modèle {0}, {1}... } fournie par WinPloy.
# Sans table, la clé et les valeurs restent lisibles.
function Format-Text {
    param([string]$Key)
    $template = if ($T) { [string]$T[$Key] } else { '' }
    if ($template) { $template -f $args } else { (@($Key) + $args) -join ' ' }
}

function New-Result {
    param($Pkg, [bool]$Ok, [string]$Message)
    [pscustomobject]@{
        Id = $Pkg.Id; Name = $Pkg.Name
        Statut = $(if ($Ok) { 'OK' } else { 'ECHEC' })
        Message = $Message
    }
}

function ConvertTo-Literal {
    param([string]$Value)
    "'" + $Value.Replace("'", "''") + "'"
}

function Get-LogSummary {
    param([string[]]$Files)
    foreach ($file in $Files) {
        if (-not (Test-Path -LiteralPath $file)) { continue }
        $lines = @(Get-Content -LiteralPath $file -Encoding UTF8 -ErrorAction SilentlyContinue |
            ForEach-Object { $_.Trim() } |
            Where-Object { $_ -and $_ -notmatch '^[\|/\\\-\s]+$' -and $_ -notmatch '^[█▒\s\d.%KMGB/]+$' })
        if ($lines.Count) {
            $text = (($lines | Select-Object -Last 3) -join ' | ')
            $text = [regex]::Replace($text, '[\x00-\x08\x0B\x0C\x0E-\x1F]', '')
            if ($text.Length -gt 500) { $text = $text.Substring(0, 500) + '...' }
            return $text
        }
    }
    return ''
}

# Exécute un script PowerShell dans une tâche planifiée temporaire en SYSTEM.
# Retourne le code de sortie, ou $null en cas de dépassement du délai.
function Invoke-AsSystem {
    param([string]$ScriptPath, [int]$Minutes)
    Import-Module ScheduledTasks
    $taskName  = "WinPloy_$([guid]::NewGuid().ToString('N'))"
    $taskArgs  = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$ScriptPath`""
    $action    = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $taskArgs
    $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    $settings  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
                    -ExecutionTimeLimit (New-TimeSpan -Minutes $Minutes)
    Register-ScheduledTask -TaskName $taskName -Action $action -Principal $principal -Settings $settings -Force | Out-Null
    try {
        Start-ScheduledTask -TaskName $taskName
        $sw = [Diagnostics.Stopwatch]::StartNew()
        do {
            Start-Sleep -Seconds 2
            if ($sw.Elapsed.TotalMinutes -ge $Minutes) {
                Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
                return $null
            }
            $state = (Get-ScheduledTask -TaskName $taskName).State
            $last  = [uint32](Get-ScheduledTaskInfo -TaskName $taskName).LastTaskResult
            # 0x41301 = tâche en cours, 0x41303 = tâche pas encore lancée
        } while ($state -eq 'Running' -or $state -eq 'Queued' -or $last -eq 0x41301 -or $last -eq 0x41303)
        return $last
    } finally {
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    }
}

# ---------- Paquets WinGet (exécutés en SYSTEM : winget échoue souvent en session WinRM) ----------
if (@($WingetPkgs).Count -gt 0) {
    $wingetDir = Get-ChildItem (Join-Path $env:ProgramFiles 'WindowsApps') -Directory `
                    -Filter 'Microsoft.DesktopAppInstaller_*_x64__8wekyb3d8bbwe' -ErrorAction SilentlyContinue |
                 Sort-Object { try { [version]($_.Name -split '_')[1] } catch { [version]'0.0' } } |
                 Select-Object -Last 1
    $wingetExe = if ($wingetDir) { Join-Path $wingetDir.FullName 'winget.exe' } else { $null }

    foreach ($pkg in $WingetPkgs) {
        if (-not $wingetExe -or -not (Test-Path -LiteralPath $wingetExe -PathType Leaf)) {
            $results.Add((New-Result $pkg $false (Format-Text WingetMissing $env:COMPUTERNAME)))
            continue
        }
        if ([string]$pkg.Id -notmatch '^[A-Za-z0-9][A-Za-z0-9._+-]*$') {
            $results.Add((New-Result $pkg $false (Format-Text InvalidWingetId $pkg.Id)))
            continue
        }

        $base    = Join-Path $workRoot ("winget_{0}" -f [guid]::NewGuid().ToString('N'))
        $logFile = "$base.log"
        $psFile  = "$base.ps1"
        $wingetArgs = if ($Action -eq 'Install') {
            @('install', '--id', $pkg.Id, '--exact', '--source', 'winget', '--silent',
              '--accept-package-agreements', '--accept-source-agreements', '--disable-interactivity')
        } else {
            @('uninstall', '--id', $pkg.Id, '--exact', '--source', 'winget', '--silent', '--disable-interactivity')
        }
        $argsLiteral = ($wingetArgs | ForEach-Object { ConvertTo-Literal $_ }) -join ' '

        $wrapper = @(
            '$vclibs = Get-ChildItem (Join-Path $env:ProgramFiles ''WindowsApps'') -Directory -Filter ''Microsoft.VCLibs.140.00.UWPDesktop_*_x64__8wekyb3d8bbwe'' -ErrorAction SilentlyContinue | Sort-Object Name | Select-Object -Last 1'
            'if ($vclibs) { $env:PATH = "$($vclibs.FullName);$env:PATH" }'
            "& $(ConvertTo-Literal $wingetExe) $argsLiteral 2>&1 | Out-File -LiteralPath $(ConvertTo-Literal $logFile) -Encoding utf8"
            'exit $LASTEXITCODE'
        ) -join "`r`n"

        try {
            Set-Content -LiteralPath $psFile -Value $wrapper -Encoding UTF8
            $code = Invoke-AsSystem -ScriptPath $psFile -Minutes $TimeoutMin
            $summary = Get-LogSummary @($logFile)

            if ($null -eq $code) {
                $results.Add((New-Result $pkg $false (Format-Text "${Action}Timeout" $TimeoutMin)))
                continue
            }
            # 2316632161 = 0x8A150061 (déjà installé), 2316632084 = 0x8A150014 (aucun paquet trouvé),
            # 3221225781 = 0xC0000135 (DLL manquante). Écrits en décimal : 0x8A150061 serait un Int32 négatif.
            $hex = '0x{0:X8}' -f $code
            $detail = if ($summary) { " - $summary" } else { '' }
            switch ($code) {
                0          { $results.Add((New-Result $pkg $true  (Format-Text "${Action}Done" $hex $detail))) }
                3010       { $results.Add((New-Result $pkg $true  (Format-Text "${Action}DoneReboot" $hex $detail))) }
                2316632161 { $results.Add((New-Result $pkg $true  (Format-Text AlreadyInstalled $hex))) }
                2316632084 { $results.Add((New-Result $pkg ($Action -eq 'Uninstall') (Format-Text NoMatchingPackage $hex))) }
                3221225781 { $results.Add((New-Result $pkg $false (Format-Text "${Action}FailedVc" $hex))) }
                default    { $results.Add((New-Result $pkg $false (Format-Text "${Action}Failed" $hex $detail))) }
            }
        } catch {
            $results.Add((New-Result $pkg $false $_.Exception.Message))
        } finally {
            Remove-Item -LiteralPath $psFile, $logFile -Force -ErrorAction SilentlyContinue
        }
    }
}

# ---------- Paquets personnalisés (dossier déjà copié dans StageDir par la machine d'administration) ----------
foreach ($pkg in $CustomPkgs) {
    $stage = [string]$pkg.StageDir
    try {
        $local = Join-Path $stage $pkg.Leaf
        $outLog = Join-Path $stage '_winploy_out.log'
        $errLog = Join-Path $stage '_winploy_err.log'
        switch ([IO.Path]::GetExtension($pkg.Leaf).ToLowerInvariant()) {
            '.msi'  { $exe = 'msiexec.exe'; $exeArgs = @($(if ($Action -eq 'Install') { '/i' } else { '/x' }), $local, '/qn', '/norestart') }
            '.ps1'  { $exe = 'powershell.exe'; $exeArgs = @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', $local) }
            '.bat'  { $exe = 'cmd.exe';        $exeArgs = @('/c', $local) }
            '.cmd'  { $exe = 'cmd.exe';        $exeArgs = @('/c', $local) }
            '.exe'  { $exe = $local;           $exeArgs = @() }
            default { throw (Format-Text UnsupportedExtension $pkg.Leaf) }
        }

        if ($pkg.RunAs -eq 'System') {
            $psFile = Join-Path $stage '_winploy_system.ps1'
            $argsLiteral = ($exeArgs | ForEach-Object { ConvertTo-Literal $_ }) -join ' '
            @(
                "Set-Location -LiteralPath $(ConvertTo-Literal $stage)"
                "& $(ConvertTo-Literal $exe) $argsLiteral 1> $(ConvertTo-Literal $outLog) 2> $(ConvertTo-Literal $errLog)"
                'exit $LASTEXITCODE'
            ) -join "`r`n" | Set-Content -LiteralPath $psFile -Encoding UTF8
            $code = Invoke-AsSystem -ScriptPath $psFile -Minutes $TimeoutMin
            if ($null -eq $code) { throw (Format-Text SystemTimeout $pkg.Leaf $TimeoutMin) }
            $mode = ' (SYSTEM)'
        } else {
            # stdin vide : un script qui attend une saisie se termine au lieu de bloquer la session.
            $stdin = Join-Path $stage '_winploy_stdin.txt'
            New-Item -Path $stdin -ItemType File -Force | Out-Null
            $sp = @{
                FilePath = $exe; WorkingDirectory = $stage; PassThru = $true; NoNewWindow = $true
                RedirectStandardInput = $stdin; RedirectStandardOutput = $outLog; RedirectStandardError = $errLog
            }
            if ($exeArgs.Count) { $sp.ArgumentList = ($exeArgs | ForEach-Object { if ($_ -match '\s') { "`"$_`"" } else { $_ } }) }
            $p = Start-Process @sp
            if (-not $p.WaitForExit($TimeoutMin * 60000)) {
                & taskkill.exe /PID $p.Id /T /F | Out-Null
                throw (Format-Text ProcessTimeout $pkg.Leaf $TimeoutMin)
            }
            $code = $p.ExitCode
            $mode = ''
        }

        if ($code -in 0, 3010) {
            $reboot = if ($code -eq 3010) { Format-Text RebootRequired } else { '' }
            $results.Add((New-Result $pkg $true "$($pkg.Leaf) ExitCode=$code$reboot$mode"))
        } else {
            $summary = Get-LogSummary @($errLog, $outLog)
            $results.Add((New-Result $pkg $false "$($pkg.Leaf) ExitCode=$code$mode $summary".Trim()))
        }
    } catch {
        $results.Add((New-Result $pkg $false $_.Exception.Message))
    } finally {
        if ($stage -and (Test-Path -LiteralPath $stage)) { Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

return $results.ToArray()
