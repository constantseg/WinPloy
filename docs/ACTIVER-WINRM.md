# Activer WinRM sur les postes cibles

WinPloy ne configure jamais WinRM ni le pare-feu des cibles : c'est au parc d'être prêt. Ce document
donne les deux façons de le préparer — par GPO pour un parc, à la main pour un poste isolé — en HTTP
(port 5985, le défaut de WinPloy) ou en HTTPS (port 5986).

Il faut trois choses sur chaque cible : le service WinRM démarré automatiquement, un écouteur sur le
port voulu, et une règle de pare-feu entrante qui l'ouvre.

## Par GPO (recommandé)

Dans une GPO liée à l'OU des postes cibles, sous *Configuration ordinateur > Stratégies* :

| Réglage | Chemin | Valeur |
|---|---|---|
| Service | *Paramètres Windows > Paramètres de sécurité > Services système > Gestion à distance de Windows (Gestion WSM)* | Automatique |
| Écouteur | *Modèles d'administration > Composants Windows > Gestion à distance de Windows (WinRM) > Service WinRM > Autoriser la gestion de serveurs à distance via WinRM* (*Allow remote server management through WinRM*) | Activé, filtre IPv4 `*` (filtre IPv6 `*` si besoin) |
| Pare-feu | *Paramètres Windows > Paramètres de sécurité > Pare-feu Windows Defender avec fonctions avancées de sécurité > Règles de trafic entrant > Nouvelle règle > Prédéfinie : Gestion à distance de Windows* | Ne garder que la règle du profil **Domaine** |

Dans les propriétés de la règle de pare-feu, onglet *Étendue*, restreindre les adresses distantes aux
postes d'administration : c'est la protection la plus efficace, et elle ne coûte rien.

Le filtre IPv4 du deuxième réglage désigne les adresses **locales** sur lesquelles le service écoute,
pas les clients autorisés ; le filtrage des clients se fait au pare-feu.

Appliquer, puis sur un poste cible :

```powershell
gpupdate /force
winrm enumerate winrm/config/listener    # un écouteur HTTP avec Source="GPO" doit apparaître
```

### En HTTPS

Il faut en plus un certificat machine sur chaque poste : *Authentification du serveur* dans l'usage
étendu de la clé, nom du poste (FQDN) en sujet ou en nom alternatif, émis par une autorité que le poste
d'administration approuve. Avec une autorité de certification d'entreprise (AD CS), le plus simple est
l'inscription automatique du modèle *Ordinateur* :

- *Configuration ordinateur > Stratégies > Paramètres Windows > Paramètres de sécurité > Stratégies de
  clé publique > Client des services de certificats - Inscription automatique* : Activé, avec
  renouvellement et mise à jour des certificats.

Quand un tel certificat est présent, le service WinRM configuré par la GPO crée aussi l'écouteur HTTPS
— le vérifier avec `winrm enumerate winrm/config/listener`. S'il n'apparaît pas, le créer par le script
de la section [HTTPS à la main](#https), déployé en script de démarrage de la GPO.

Ajouter enfin une règle de pare-feu entrante **personnalisée** (la règle prédéfinie ne couvre que 5985) :
TCP, port local 5986, profil Domaine, étendue limitée aux postes d'administration. Une fois l'HTTPS en
place sur tout le parc, la règle 5985 peut être retirée et la case **HTTPS** cochée dans les réglages
de WinPloy.

## À la main

Dans une console PowerShell **administrateur** sur le poste cible.

### HTTP

```powershell
Enable-PSRemoting -Force
```

La commande démarre le service en automatique, crée l'écouteur HTTP sur 5985 et ouvre le pare-feu.
Elle refuse de s'exécuter si une carte réseau est en profil *Public* — ce qui ne devrait pas arriver sur
un poste joint au domaine. Le vérifier avec `Get-NetConnectionProfile` ; `-SkipNetworkProfileCheck`
passe outre, mais n'ouvre alors le pare-feu qu'au sous-réseau local sur le profil Public.

### HTTPS

Après `Enable-PSRemoting`, avec un certificat machine déjà présent (voir plus haut) :

```powershell
$fqdn = [System.Net.Dns]::GetHostEntry($env:COMPUTERNAME).HostName
$cert = Get-ChildItem Cert:\LocalMachine\My |
    Where-Object { $_.NotAfter -gt (Get-Date) -and
                   $_.EnhancedKeyUsageList.ObjectId -contains '1.3.6.1.5.5.7.3.1' -and
                   $_.DnsNameList.Unicode -contains $fqdn } |
    Sort-Object NotAfter -Descending | Select-Object -First 1
if (-not $cert) { throw "Aucun certificat d'authentification serveur pour $fqdn" }

New-Item -Path WSMan:\localhost\Listener -Transport HTTPS -Address * `
    -HostName $fqdn -CertificateThumbPrint $cert.Thumbprint -Force
New-NetFirewallRule -Name WinRM-HTTPS-In -DisplayName 'WinRM HTTPS (5986)' -Direction Inbound `
    -Protocol TCP -LocalPort 5986 -Profile Domain -Action Allow
```

Sans autorité de certification, pour un essai seulement, un certificat autosigné fait l'affaire à
condition de cocher **Certificat non validable** dans WinPloy — ce qui retire à l'HTTPS l'authentification
du poste cible :

```powershell
$cert = New-SelfSignedCertificate -DnsName $fqdn -CertStoreLocation Cert:\LocalMachine\My
```

### À distance, sans WinRM

Quand le poste n'est pas sous la main mais accepte encore WMI par DCOM (RPC, port 135 et ports
dynamiques), on peut lancer l'activation depuis le poste d'administration :

```powershell
$poste = 'PC-COMPTA-01'
$cim = New-CimSession -ComputerName $poste -SessionOption (New-CimSessionOption -Protocol Dcom)
Invoke-CimMethod -CimSession $cim -ClassName Win32_Process -MethodName Create -Arguments @{
    CommandLine = 'powershell.exe -NoProfile -Command "Enable-PSRemoting -Force"'
}
Remove-CimSession $cim
```

Le processus est lancé sans attendre sa fin : vérifier une dizaine de secondes plus tard (section
suivante). `ReturnValue` à 0 signifie seulement que le processus a démarré.

## Vérifier

Depuis le poste d'administration :

```powershell
Test-NetConnection PC-COMPTA-01 -Port 5985    # le port répond
Test-WSMan PC-COMPTA-01                       # le service WinRM répond (HTTP)
Test-WSMan PC-COMPTA-01 -UseSSL               # idem en HTTPS, certificat compris
Invoke-Command -ComputerName PC-COMPTA-01 { hostname }   # une session s'ouvre vraiment
```

Si `Test-WSMan` répond mais que la session est refusée, le problème est d'authentification et non
d'activation — voir [Hôtes approuvés](../README.md#hôtes-approuvés).

## Comptes locaux

Avec un compte de domaine membre des administrateurs du poste, rien d'autre n'est nécessaire. Avec un
**compte local** administrateur autre que le compte intégré `Administrateur`, le contrôle de compte
d'utilisateur retire ses droits aux connexions distantes et la session s'ouvre sans privilèges. Le
lever demande la clé suivante sur la cible :

```powershell
New-ItemProperty -Path HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System `
    -Name LocalAccountTokenFilterPolicy -PropertyType DWord -Value 1 -Force
```

C'est un affaiblissement réel — c'est précisément ce que la restriction empêche (réutilisation d'un mot
de passe local commun d'un poste à l'autre). Préférer un compte de domaine ; à défaut, des mots de passe
locaux distincts par poste (LAPS).

## Revenir en arrière

```powershell
Disable-PSRemoting -Force
Remove-Item -Path WSMan:\localhost\Listener\* -Recurse
Get-NetFirewallRule -Name WINRM-HTTP-In-TCP*, WinRM-HTTPS-In -ErrorAction SilentlyContinue |
    Disable-NetFirewallRule
Set-Service WinRM -StartupType Manual
Stop-Service WinRM
```

Sur un parc géré par GPO, retirer ou désactiver la GPO : les écouteurs `Source="GPO"` ne se suppriment
pas à la main.
