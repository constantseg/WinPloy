# WinPloy

Pousser une application sur un lot de postes Windows, à la demande, depuis un poste d'administration.
Application de bureau Windows (C# .NET 8 + WPF).

On choisit des postes dans l'Active Directory, on choisit des applications WinGet ou ses propres paquets,
on clique sur **Exécuter** : WinPloy ouvre une session WinRM sur chaque poste, installe ou désinstalle, et
affiche l'avancement poste par poste.

Un outil d'installation ponctuelle, pas de gestion de parc : aucun agent sur les postes, aucun serveur à
monter, rien à préparer d'avance. Voir [Installation ponctuelle](#installation-ponctuelle).

![Interface de WinPloy : postes, applications et déploiement en trois volets, suivi en bas](docs/images/interface.png)

*Les trois étapes de gauche à droite — choisir les postes, choisir les applications, lancer — et le suivi
en bas : état par poste, détail par paquet et journal.*

- **Applications WinGet** : installées depuis la source `winget` du poste cible, en SYSTEM.
- **Paquets personnalisés** : un `.msi` ou un script (`ps1`, `bat`, `cmd`, `exe`) posé sur un partage.
  Le dossier du script est copié sur le poste, exécuté, puis supprimé. Par défaut la copie passe par la
  session WinRM, donc le poste cible n'a jamais besoin d'accéder au partage.

---

## Fonctions

| | |
|---|---|
| **Postes** | Recherche Active Directory par nom (partiel, joker `*`) dans tout le domaine, sans RSAT |
| **Catalogue** | Fichier JSON partagé entre administrateurs, modifiable depuis l'interface |
| **WinGet** | Recherche en ligne de l'identifiant depuis la fenêtre d'ajout — WinGet n'est pas requis sur le poste d'administration |
| **Personnalisé** | `.msi` lancé par `msiexec /i` (ou `/x`) `/qn /norestart`, ou script exécuté au choix sous le compte de la session WinRM ou en `NT AUTHORITY\SYSTEM` |
| **Exécution** | Panier mixte WinGet + personnalisé, postes traités en parallèle (1 à 200), interface jamais bloquée |
| **Suivi** | État par poste, détail par paquet, journal, filtres, arrêt en cours d'opération, relance des seuls échecs |
| **Traçabilité** | Export CSV des résultats, historique local, journal `gui.log` |
| **Intégrité** | Empreinte SHA-256 du dossier d'un paquet figée dans le catalogue, contrôlée avant chaque déploiement |
| **Transfert** | Cache par empreinte sur le poste cible et blocs compressés, ou copie SMB tirée par le poste |
| **WinRM** | Port et HTTPS réglables ; `TrustedHosts` laissé intact tant que Kerberos suffit |

## Installation ponctuelle

« Installer cette application sur ces douze postes, maintenant. » C'est le besoin que WinPloy couvre, et
c'est tout ce qu'il couvre. On décompresse, on lance, on choisit les postes, on choisit les applications,
on exécute, on lit le résultat. Rien à installer sur le poste d'administration, rien à déployer sur les
cibles, rien à préparer d'avance, rien qui tourne entre deux opérations.

Cette simplicité est le but, pas une étape vers autre chose. WinPloy ne gère donc pas l'état d'un parc :
ni inventaire, ni conformité, ni remédiation, ni planification, ni mise à jour automatique. Intune, SCCM,
Action1, PDQ Deploy, WAPT ou Chocolatey poussé par GPO font ce travail, et le font mieux — mais les
mobiliser pour poser une application sur quelques postes est disproportionné, quand on les a.

## Prérequis

**Poste d'administration**

- Windows 10 ou 11, membre du domaine.
- Runtime .NET 8 Desktop, ou publication autonome (voir [Compilation](#compilation)).
- Exécution en administrateur : demandée au lancement par le manifeste de l'application.
- Accès en lecture/écriture au catalogue, en lecture aux fichiers des paquets personnalisés.
- `TrustedHosts` du client WinRM n'est pas modifié — voir [Hôtes approuvés](#hôtes-approuvés).
- Facultatif : accès HTTPS à `api.winstall.app` pour la recherche d'identifiants WinGet. Sans cet accès,
  l'identifiant se saisit à la main et la fenêtre propose le lien `https://winget.run/` à copier.

**Postes cibles**

- WinRM activé et joignable sur le port réglé — 5985 en HTTP par défaut, 5986 en HTTPS (GPO recommandée).
  WinPloy ne configure jamais WinRM ni le pare-feu des cibles.
- WinGet (App Installer) présent pour les paquets WinGet.
- En mode de transfert SMB uniquement : accès en lecture au partage du dépôt depuis le poste cible.

## Installation

Télécharger l'archive `WinPloy-<version>-win-x64.zip` de la [dernière release](../../releases/latest),
la décompresser où l'on veut, puis lancer `WinPloy.exe` : l'élévation en administrateur est demandée
automatiquement. Le runtime .NET est inclus dans l'archive, il n'y a rien d'autre à installer.

L'exécutable n'étant pas signé, Windows SmartScreen affiche un avertissement au premier lancement :
« Informations complémentaires » puis « Exécuter quand même ».

## Compilation

Pour compiler soi-même, le SDK .NET 8 (ou plus récent) est nécessaire.

```bash
dotnet build WinPloy.sln -c Release
dotnet test WinPloy.sln
```

Le protocole de réception des fichiers a son propre contrôle, qui n'a besoin ni du réseau ni d'un
domaine :

```bash
pwsh -NoProfile -File tests/Remote/ReceiveFiles.Tests.ps1
```

Publication en un dossier, runtime .NET inclus — rien à installer sur le poste d'administration :

```bash
dotnet publish src/WinPloy/WinPloy.csproj -c Release -r win-x64 --self-contained true -o publish
```

Lancer ensuite `publish\WinPloy.exe`.

## Utilisation

1. **Réglages** (icône engrenage) : au premier lancement, indiquer le chemin UNC du catalogue partagé, par exemple
   `\\serveur\depot\catalogue.json`. Le dossier de ce fichier sert de racine au dépôt des paquets
   personnalisés. Régler au besoin le délai par paquet (1 à 240 min, 30 par défaut), le nombre de
   postes traités en parallèle (1 à 200, 10 par défaut), le port WinRM du parc et le mode de
   transfert — voir [Réglages WinRM et transfert](#réglages-winrm-et-transfert).
2. **Compte** : par défaut la session WinRM utilise le compte courant. Le bouton « Compte » permet de
   fournir d'autres identifiants d'administration.
3. **Étape 1 — Postes** : chercher les postes dans l'AD, les ajouter à la liste des postes ciblés.
4. **Étape 2 — Applications** : ajouter une application WinGet ou un paquet personnalisé au catalogue,
   puis l'ajouter au déploiement.
5. **Installer / Désinstaller**, puis **Exécuter**. Le suivi s'affiche en bas : état par poste, détail
   par paquet, journal. « Relancer les échecs » rejoue l'opération sur les seuls postes en échec.

### Paquets personnalisés

Chaque paquet doit vivre dans **son propre sous-dossier** du dépôt : le dossier entier est copié sur le
poste cible, donc un script posé à la racine du dépôt y copierait tout le dépôt. WinPloy refuse ce cas.

```
\\serveur\depot\
├── catalogue.json
├── MonApp\
│   ├── setup.msi
│   └── uninstall.ps1
└── AutreApp\
    └── install.ps1
```

Un paquet est considéré réussi si le code de sortie est `0` ou `3010` (redémarrage requis).

Sur le poste cible, le dossier est reconstitué sous `C:\Windows\Temp\WinPloy`, dans un dossier dont
l'héritage des droits est coupé : seuls `NT AUTHORITY\SYSTEM` et le groupe Administrateurs y ont accès.
Sans cela, `C:\Windows\Temp` étant accessible en écriture aux utilisateurs standards, un utilisateur du
poste pourrait remplacer un script juste avant son exécution en SYSTEM. Un dossier pré-créé par un tiers,
ou transformé en lien, est supprimé et recréé.

### Contrôle d'intégrité des paquets

Le dossier d'un paquet est copié puis exécuté sur le poste, souvent en SYSTEM : qui peut écrire dans le
dépôt peut faire exécuter du code sur tout le parc. La case **« Contrôler l'empreinte SHA-256 du dossier »**
de la fenêtre du paquet fige le contenu attendu du dossier dans le catalogue, fichier par fichier. Avant
chaque déploiement, WinPloy recalcule les empreintes et refuse le paquet si un fichier a été modifié,
retiré ou ajouté.

```json
"Files": {
  "setup.msi":    "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08",
  "install.ps1":  "60303ae22b998861bce3b28f33eec1be758a213c86c93c076dbe9f558c11c752"
}
```

Après chaque mise à jour volontaire du paquet, recalculer l'empreinte depuis la même fenêtre — sinon le
déploiement sera refusé, ce qui est le comportement voulu. Le contrôle exige que les scripts
d'installation et de désinstallation vivent dans le même dossier, puisque l'empreinte couvre un dossier.

Un paquet sans `Files` n'est pas contrôlé : les catalogues écrits avant cette version fonctionnent tels
quels, et les deux formes cohabitent dans le même fichier.

### Réglages WinRM et transfert

| Réglage | Effet |
|---|---|
| **Port** | Port du service WinRM des cibles. 5985 en HTTP, 5986 en HTTPS ; le port suit la case HTTPS tant qu'il garde la valeur par défaut de l'autre protocole |
| **HTTPS** | Session WinRM sur SSL, pour les parcs où le port 5985 est fermé par GPO |
| **Certificat non validable** | Accepte un certificat autosigné ou au nom différent. Sans effet en HTTP, affaiblit l'authentification du poste cible : à réserver aux parcs sans autorité de certification |
| **Transfert WinRM** *(défaut)* | Le dossier traverse le poste d'administration bloc par bloc. Le poste cible n'accède jamais au partage |
| **Transfert SMB** | Le poste cible monte le partage avec les identifiants fournis et copie lui-même le dossier |

En mode WinRM, chaque fichier est identifié par son empreinte et mis en cache sur le poste cible sous
`C:\Windows\Temp\WinPloy\cache` (purge à 30 jours) : un paquet redéployé, une relance des échecs ou deux
paquets qui partagent un fichier ne coûtent plus rien sur le réseau. Les blocs sont compressés quand cela
réduit leur taille, et l'empreinte est revérifiée sur le poste avant chaque usage du cache — une entrée
modifiée est redemandée, jamais exécutée.

En mode SMB, le fichier va du serveur de fichiers au poste en un seul saut, ce qui déplace la charge du
poste d'administration vers le serveur de fichiers : c'est le mode à choisir pour un parc large ou de gros
installeurs. Il impose deux choses — des identifiants explicites (bouton « Compte »), car la session WinRM
n'a pas d'identité réseau à présenter au serveur de fichiers, et un accès en lecture au partage depuis le
poste cible. L'empreinte est alors contrôlée sur le poste, après la copie, puisque c'est lui qui a lu le
dépôt.

Au-delà de 50 postes en parallèle, l'interface signale que le poste d'administration devient le goulot
d'étranglement en transfert WinRM ; la limite haute est de 200.

### Hôtes approuvés

Sur un domaine, `TrustedHosts` ne sert à rien : WinRM authentifie en Kerberos, et un poste joint au
domaine, joint par son nom ou son FQDN, porte la confiance par lui-même. La liste ne devient nécessaire
que lorsque Kerberos ne peut pas s'appliquer et que l'authentification retombe sur NTLM — connexion par
adresse IP, compte local, poste hors domaine ou hors forêt.

WinPloy n'y touche donc pas, et le mécanisme d'authentification est choisi pour que la question ne se
pose pas :

| Cible et compte | Mécanisme | `TrustedHosts` |
|---|---|---|
| Poste nommé, compte courant | `Default`, donc Kerberos | jamais touché |
| Poste nommé, compte de domaine (`DOMAINE\u`, `u@domaine`) | `Negotiate`, qui choisit Kerberos | jamais touché |
| Poste nommé, compte local (`POSTE\u`, `.\u`) ou nom de compte nu | `Negotiate`, donc NTLM | ajouté si l'ouverture échoue |
| Cible désignée par son adresse IP | pas de SPN, Kerberos impossible | ajouté si l'ouverture échoue |

Le premier cas méritait ce tableau : imposer `Negotiate` au compte courant suffit à faire refuser la
session sous HTTP — *« les informations d'identification par défaut avec négociation sous HTTP ne peuvent
être utilisées que si l'ordinateur cible figure dans la liste TrustedHosts »* — et WinPloy s'inventait
ainsi un besoin d'hôtes approuvés qu'il n'avait pas.

Un poste n'est donc ajouté que lorsque WinPloy sait, d'après la cible et le compte, que Kerberos ne peut
pas s'appliquer ; la session est alors retentée une fois, et le refus d'origine part dans le journal. Le
message d'erreur du client WinRM, lui, ne décide de rien : il joint le même texte d'aide citant
`TrustedHosts` à des échecs qui n'ont rien à voir avec la liste. Ce qui a été ajouté est retiré à la fin
de l'opération : les entrées déjà présentes ne sont jamais reprises, et un `TrustedHosts` à `*` est
laissé intact.

Dans un domaine correctement configuré, la liste reste donc vide d'un bout à l'autre, et le service WinRM
local n'est même pas démarré — il n'est nécessaire que pour lire et écrire cette configuration, jamais
pour les sessions sortantes.

Si le parc exige vraiment des cibles non Kerberos, mieux vaut pousser `TrustedHosts` par GPO
(*Configuration ordinateur > Modèles d'administration > Composants Windows > Gestion à distance de
Windows > Client WinRM*) et passer les écouteurs en HTTPS avec un vrai certificat : le chiffrement
devient la protection, plutôt que la liste. Une GPO qui impose `TrustedHosts` empêche toute modification
locale — WinPloy le détecte et le signale dans le journal au lieu d'échouer en silence.

### Catalogue partagé

Le catalogue est un simple tableau JSON, modifiable depuis l'interface ou à la main :

```json
[
  { "Type": "WinGet", "Id": "Git.Git", "Name": "Git" },
  {
    "Type": "Custom",
    "Id": "custom:MonApp",
    "Name": "MonApp",
    "InstallScript": "\\\\serveur\\depot\\MonApp\\setup.msi",
    "UninstallScript": "\\\\serveur\\depot\\MonApp\\uninstall.ps1",
    "RunAs": "System",
    "Files": { "setup.msi": "9f86d081…", "uninstall.ps1": "60303ae2…" }
  }
]
```

Le type est déduit de l'identifiant : le préfixe `custom:` marque un paquet personnalisé. `Files` est
facultatif : voir [Contrôle d'intégrité des paquets](#contrôle-dintégrité-des-paquets).

## Fichiers locaux

Dans `%ProgramData%\WinPloy` :

| Fichier | Contenu |
|---|---|
| `config.json` | Réglages du poste d'administration |
| `historique-deploiements.json` | Historique des résultats |
| `gui.log` | Journal de l'application (rotation à 5 Mo) |

Sur chaque poste cible, dans `C:\Windows\Temp\WinPloy` — dossier réservé à SYSTEM et aux
administrateurs :

| | |
|---|---|
| `cache\` | Fichiers des paquets, nommés par leur empreinte SHA-256. Purgés au bout de 30 jours |
| `stage_<guid>\` | Dossier d'un paquet pendant son exécution, supprimé juste après |

## Structure du dépôt

```
WinPloy.sln
Directory.Build.props          Propriétés communes aux trois projets (TFM, version, nullable)
src/WinPloy.Core/              Bibliothèque : modèles, services, moteur de déploiement WinRM
  Deployment/Remote/           Scripts exécutés sur le poste cible, embarqués dans l'assembly
    RemoteAction.ps1             Installation et désinstallation des paquets
    NewWorkDir.ps1               Dossier de travail aux droits restreints (SYSTEM, Administrateurs)
    ReceiveFiles.ps1             Réception des blocs et cache adressé par empreinte
    PullFromShare.ps1            Copie tirée par le poste depuis le partage (mode SMB)
src/WinPloy/                   Application WPF : fenêtres, ViewModel, thème
tests/WinPloy.Tests/           Tests unitaires xUnit, sans réseau ni domaine
tests/Remote/                  Contrôle du protocole de réception, exécuté par PowerShell
assets/                        Icône de l'application, utilisée à la compilation
docs/PLAN-CONVERSION.md        Plan de la conversion PowerShell → C#
docs/images/                   Captures d'écran du README
legacy/Winploy-GUI.ps1         Script PowerShell d'origine (v1.0.0), conservé pour référence
```

## Historique

WinPloy 1.0.0 était un script PowerShell + WinForms unique,
[`legacy/Winploy-GUI.ps1`](legacy/Winploy-GUI.ps1). La version 2.0 est une réécriture complète en
C# .NET 8 + WPF, décrite dans [docs/PLAN-CONVERSION.md](docs/PLAN-CONVERSION.md).

Différences avec la version PowerShell :

- Requêtes LDAP natives au lieu du module ActiveDirectory : plus besoin des RSAT.
- Un poste = une tâche dans le processus, au lieu d'un job PowerShell dans un processus séparé.
- Fenêtre d'identifiants propre à WinPloy au lieu de `Get-Credential`.
- Paquets personnalisés au format `.msi` et recherche en ligne des identifiants WinGet.
- Les nouvelles dates de l'historique sont au format ISO 8601.

Le format du catalogue n'a pas changé : les deux versions peuvent l'utiliser en même temps pendant la
transition.

## Licence

Distribué sous licence [Apache 2.0](LICENSE) — Copyright 2026 Constant Segretain.

---

Auteur : Constant Segretain
