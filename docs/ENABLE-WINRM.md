# Enable WinRM on the target computers

[Français](ACTIVER-WINRM.md) · **English**

WinPloy never configures WinRM or the targets' firewall: the fleet has to be ready. This document gives
the two ways to prepare it — by GPO for a fleet, by hand for a single computer — over HTTP (port 5985,
WinPloy's default) or HTTPS (port 5986).

Each target needs three things: the WinRM service started automatically, a listener on the chosen port,
and an inbound firewall rule that opens it.

## By GPO (recommended)

In a GPO linked to the OU of the target computers, under *Computer Configuration > Policies*:

| Setting | Path | Value |
|---|---|---|
| Service | *Windows Settings > Security Settings > System Services > Windows Remote Management (WS-Management)* | Automatic |
| Listener | *Administrative Templates > Windows Components > Windows Remote Management (WinRM) > WinRM Service > Allow remote server management through WinRM* | Enabled, IPv4 filter `*` (IPv6 filter `*` if needed) |
| Firewall | *Windows Settings > Security Settings > Windows Defender Firewall with Advanced Security > Inbound Rules > New Rule > Predefined: Windows Remote Management* | Keep only the **Domain** profile rule |

In the properties of the firewall rule, *Scope* tab, restrict the remote addresses to the admin
workstations: it is the most effective protection, and it costs nothing.

The IPv4 filter of the second setting designates the **local** addresses the service listens on, not the
allowed clients; clients are filtered at the firewall.

Apply, then on a target computer:

```powershell
gpupdate /force
winrm enumerate winrm/config/listener    # an HTTP listener with Source="GPO" must appear
```

### Over HTTPS

Each computer also needs a machine certificate: *Server Authentication* in the enhanced key usage, the
computer name (FQDN) as subject or subject alternative name, issued by an authority the admin workstation
trusts. With an enterprise certificate authority (AD CS), the simplest is auto-enrollment of the
*Computer* template:

- *Computer Configuration > Policies > Windows Settings > Security Settings > Public Key Policies >
  Certificate Services Client - Auto-Enrollment*: Enabled, with renewal and update of certificates.

When such a certificate is present, the WinRM service configured by the GPO also creates the HTTPS
listener — check it with `winrm enumerate winrm/config/listener`. If it does not appear, create it with
the script in the [HTTPS by hand](#https) section, deployed as a GPO startup script.

Finally add a **custom** inbound firewall rule (the predefined rule only covers 5985): TCP, local port
5986, Domain profile, scope limited to the admin workstations. Once HTTPS is in place on the whole fleet,
the 5985 rule can be removed and the **HTTPS** box checked in WinPloy's settings.

## By hand

In an **administrator** PowerShell console on the target computer.

### HTTP

```powershell
Enable-PSRemoting -Force
```

The command starts the service automatically, creates the HTTP listener on 5985 and opens the firewall.
It refuses to run if a network adapter is in the *Public* profile — which should not happen on a
domain-joined computer. Check it with `Get-NetConnectionProfile`; `-SkipNetworkProfileCheck` overrides
it, but then only opens the firewall to the local subnet on the Public profile.

### HTTPS

After `Enable-PSRemoting`, with a machine certificate already present (see above):

```powershell
$fqdn = [System.Net.Dns]::GetHostEntry($env:COMPUTERNAME).HostName
$cert = Get-ChildItem Cert:\LocalMachine\My |
    Where-Object { $_.NotAfter -gt (Get-Date) -and
                   $_.EnhancedKeyUsageList.ObjectId -contains '1.3.6.1.5.5.7.3.1' -and
                   $_.DnsNameList.Unicode -contains $fqdn } |
    Sort-Object NotAfter -Descending | Select-Object -First 1
if (-not $cert) { throw "No server authentication certificate for $fqdn" }

New-Item -Path WSMan:\localhost\Listener -Transport HTTPS -Address * `
    -HostName $fqdn -CertificateThumbPrint $cert.Thumbprint -Force
New-NetFirewallRule -Name WinRM-HTTPS-In -DisplayName 'WinRM HTTPS (5986)' -Direction Inbound `
    -Protocol TCP -LocalPort 5986 -Profile Domain -Action Allow
```

Without a certificate authority, for a test only, a self-signed certificate will do provided you check
**Accept a certificate that cannot be validated** in WinPloy — which removes the authentication of the
target computer from HTTPS:

```powershell
$cert = New-SelfSignedCertificate -DnsName $fqdn -CertStoreLocation Cert:\LocalMachine\My
```

### Remotely, without WinRM

When the computer is not at hand but still accepts WMI over DCOM (RPC, port 135 and dynamic ports), the
activation can be started from the admin workstation:

```powershell
$computer = 'PC-ACCOUNTING-01'
$cim = New-CimSession -ComputerName $computer -SessionOption (New-CimSessionOption -Protocol Dcom)
Invoke-CimMethod -CimSession $cim -ClassName Win32_Process -MethodName Create -Arguments @{
    CommandLine = 'powershell.exe -NoProfile -Command "Enable-PSRemoting -Force"'
}
Remove-CimSession $cim
```

The process is started without waiting for it to finish: check about ten seconds later (next section).
A `ReturnValue` of 0 only means the process started.

## Check

From the admin workstation:

```powershell
Test-NetConnection PC-ACCOUNTING-01 -Port 5985    # the port answers
Test-WSMan PC-ACCOUNTING-01                       # the WinRM service answers (HTTP)
Test-WSMan PC-ACCOUNTING-01 -UseSSL               # same over HTTPS, certificate included
Invoke-Command -ComputerName PC-ACCOUNTING-01 { hostname }   # a session really opens
```

If `Test-WSMan` answers but the session is refused, the problem is authentication, not activation — see
[Trusted hosts](../README.en.md#trusted-hosts).

## Local accounts

With a domain account that is a member of the computer's administrators, nothing else is needed. With a
**local** administrator account other than the built-in `Administrator` account, User Account Control
strips its rights from remote connections and the session opens without privileges. Lifting this
requires the following key on the target:

```powershell
New-ItemProperty -Path HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System `
    -Name LocalAccountTokenFilterPolicy -PropertyType DWord -Value 1 -Force
```

It is a real weakening — it is precisely what the restriction prevents (reuse of a common local password
from one computer to another). Prefer a domain account; failing that, distinct local passwords per
computer (LAPS).

## Rolling back

```powershell
Disable-PSRemoting -Force
Remove-Item -Path WSMan:\localhost\Listener\* -Recurse
Get-NetFirewallRule -Name WINRM-HTTP-In-TCP*, WinRM-HTTPS-In -ErrorAction SilentlyContinue |
    Disable-NetFirewallRule
Set-Service WinRM -StartupType Manual
Stop-Service WinRM
```

On a fleet managed by GPO, remove or disable the GPO: `Source="GPO"` listeners cannot be deleted by hand.
