# Plan de conversion WinPloy : PowerShell/WPF vers C# .NET 8 + WPF

Source : `Winploy-GUI.ps1` (v1.0.0, 1679 lignes). Cible : solution `WinPloy.sln`, application WPF `net8.0-windows`.

> Ce document décrit la conversion telle qu'elle a été menée pour la version 2.0. Les évolutions
> ultérieures (contrôle d'intégrité, réglages WinRM, modes de transfert) sont décrites dans le README.


## 1. Objectifs

- Reproduire toutes les fonctions du script, avec la même interface (mêmes zones, libellés, couleurs, raccourcis).
- Garder la compatibilité des fichiers : `config.json`, `catalogue.json` partagé, `historique-deploiements.json`, `gui.log`.
  Les deux versions peuvent tourner en parallèle sur le même catalogue pendant la transition.
- Garder le comportement réseau : WinRM port 5985, authentification Negotiate, essais de comptes locaux, TrustedHosts.
- Supprimer la dépendance aux RSAT (module ActiveDirectory) : requêtes LDAP natives.

## 2. Choix techniques

| Sujet | Script PowerShell | Version C# |
|---|---|---|
| Élévation | relance `-Verb RunAs` | `app.manifest` `requireAdministrator` |
| Interface | XAML en chaîne + `FindName` | XAML compilé, MVVM (CommunityToolkit.Mvvm) |
| Active Directory | `Get-ADComputer`, `Get-ADOrganizationalUnit` (RSAT) | `System.DirectoryServices` (`DirectorySearcher`, pagination) |
| Identifiants | `Get-Credential` | fenêtre `CredentialWindow` (PasswordBox, SecureString) |
| Session WinRM | `New-PSSession` dans un `Start-Job` | `Microsoft.PowerShell.SDK` : runspace distant `WSManConnectionInfo` |
| Copie du dossier du paquet | `Copy-Item -ToSession` | copie par blocs de 2 Mo dans le runspace distant (`RemoteFileCopier`) |
| Exécution sur le poste | script block PowerShell | même script, embarqué en ressource (`Remote/RemoteAction.ps1`) |
| Parallélisme | file + `DispatcherTimer` + jobs | `async`/`await`, `SemaphoreSlim(MaxParallel)` |
| Arrêt / délai global | `Stop-Job` | `CancellationToken` + `PowerShell.BeginStop` |
| TrustedHosts | `Set-Item WSMan:\...` | API COM `WSMan.Automation` (`winrm/config/client`) |
| Export CSV | `Export-Csv -Delimiter ';'` | `CsvExporter` (même format : guillemets, `;`, UTF-8 BOM) |

Le code exécuté sur le poste cible reste en PowerShell : le poste n'a pas WinPloy installé,
et ce code pilote `winget.exe` et le Planificateur de tâches. Il est identique à la version 1.0.0.

## 3. Structure

```
WinPloy.sln
src/WinPloy/
  App.xaml(.cs), app.manifest, WinPloy.csproj
  Models/        AppSettings, CatalogueEntry, HostRow, PackageResult, DeployAction
  Services/      AppPaths, LogService, SettingsService, HistoryService, CatalogueService,
                 ActiveDirectoryService, TrustedHostsService, CsvExporter, ClipboardHelper
  Deployment/    DeploymentEngine, TargetRunner, RemoteFileCopier, TcpProbe,
                 CredentialCandidates, FriendlyErrors, Remote/RemoteAction.ps1
  ViewModels/    MainViewModel
  Views/         MainWindow, SettingsWindow, WingetPackageWindow, CustomPackageWindow,
                 CredentialWindow, ResultDetailsWindow, DialogService
  Themes/        Styles.xaml
tests/WinPloy.Tests/  xUnit : catalogue, réglages, erreurs, LDAP, CSV, comptes
```

## 4. Étapes

1. Squelette : solution, projet WPF, manifeste admin, styles (boutons, cartes, grilles).
2. Modèles et services fichiers : chemins ProgramData, journal avec rotation 5 Mo, réglages bornés
   (délai 1-240 min, parallélisme 1-50), historique limité à 200 entrées.
3. Catalogue : lecture tolérante (objet seul ou tableau), type déduit du préfixe `custom:`,
   écriture atomique (fichier temporaire + `File.Replace`), relecture avant chaque modification.
4. Active Directory : recherche des postes activés par nom partiel dans tout le domaine (le choix d'OU a été retiré à la demande),
   échappement LDAP du texte saisi.
5. Déploiement :
   - test TCP 5985 (3 s) ;
   - comptes candidats (`user` puis `POSTE\user`, `.\user` vers `POSTE\user`) ;
   - session WinRM (ouverture 30 s, pas de délai d'opération) ;
   - préparation des paquets personnalisés (vérifications, refus de la racine du dépôt, copie) ;
   - exécution du script distant, lecture des résultats ;
   - délai global par poste = délai par paquet x nombre de paquets + 5 min ;
   - messages d'erreur lisibles (0x8009030e, accès refusé).
6. Interface principale : panneaux postes AD, catalogue filtrable, postes ciblés, panier,
   exécution, arrêt, relance des échecs, compteurs, barre de progression, filtres du suivi,
   détail par poste, détail par paquet (double-clic), journal, export CSV.
7. Dialogues : réglages, application WinGet, paquet personnalisé (validation UNC, extension,
   sous-dossier), identifiants, détail du résultat, confirmations (désinstallation, plus de 5 postes,
   suppression catalogue, arrêt, fermeture pendant une opération).
8. Tests unitaires de la logique sans réseau.
9. Documentation : `README.md` (compilation, publication, prérequis).

## 5. Écarts assumés

- Plus besoin des RSAT sur le poste d'administration.
- Les jobs PowerShell (un processus par poste) deviennent des tâches dans le même processus : plus léger.
- Fenêtre d'identifiants propre à WinPloy au lieu de la boîte `Get-Credential`.
- L'historique garde les anciennes entrées telles quelles ; les nouvelles dates sont au format ISO 8601.

## 6. Vérification

- `dotnet build` sans avertissement bloquant, `dotnet test` vert.
- Lancement de l'application : chargement du catalogue, réglages, filtres, dialogues.
- Test réel WinRM à faire par l'administrateur sur un poste de test (non faisable depuis cet environnement).

## 7. État au 9 octobre 2026

Toutes les étapes 1 à 9 sont faites.

- Build : 0 avertissement, 0 erreur. Tests : 72/72 réussis.
- Paquets NuGet : aucune vulnérabilité connue (`Microsoft.PowerShell.SDK` 7.4.20).
- Essai manuel (sans élévation) : recherche AD, chargement des OUs, catalogue sur le partage, panier,
  test du port 5985 et ouverture de session WinRM vers un poste réel. La session a été refusée
  (« Accès refusé », contexte courant) et le message s'affiche correctement.
- Reste à valider en conditions réelles, en administrateur et avec un compte autorisé sur le poste :
  installation WinGet, paquet personnalisé (copie par blocs), exécution en SYSTEM, arrêt, relance,
  mise à jour de TrustedHosts.
- Suivi du script : TrustedHosts reçoit désormais toutes les cibles (plus seulement les comptes locaux),
  le service WinRM local est démarré si besoin, et un échec affiche un avertissement.
- Interface refaite pour les écrans 14 pouces (1280 x 720) : étapes numérotées, compteurs colorés qui filtrent le suivi, recherche par OU retirée.
