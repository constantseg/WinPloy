using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinPloy.Core.Deployment;
using WinPloy.Core.Models;
using WinPloy.Core.Services;
using WinPloy.Services;
using PSCredential = System.Management.Automation.PSCredential;

namespace WinPloy.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDeploymentObserver
{
    private const string NoHostSelectedText = "Sélectionnez un poste pour afficher son détail.";
    // Affiché après « Compte » dans la barre de commandes.
    private const string CurrentContextText = "courant";

    private readonly LogService _log;
    private readonly IDialogService _dialogs;
    private readonly SettingsService _settingsService;
    private readonly CatalogueService _catalogue;
    private readonly HistoryService _history;
    private readonly TrustedHostsService _trustedHosts;
    private readonly ActiveDirectoryService _activeDirectory = new();
    private readonly DeploymentEngine _engine = new(new TargetRunner());
    private readonly DispatcherTimer _timer;

    private AppSettings _settings;
    private PSCredential? _credential;
    private RunState? _run;
    private LastRun? _lastRun;

    [ObservableProperty]
    private string _credentialLabel = CurrentContextText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInstall))]
    private bool _isUninstall;

    [ObservableProperty]
    private string _pcSearchText = "";

    [ObservableProperty]
    private IReadOnlyList<string> _foundComputers = [];

    [ObservableProperty]
    private string _pcFoundLabel = "Aucune recherche";

    [ObservableProperty]
    private string _pkgSearchText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDeploy))]
    private bool _isRunning;

    [ObservableProperty]
    private bool _canStop;

    [ObservableProperty]
    private bool _canRetry;

    [ObservableProperty]
    private bool _canExport;

    [ObservableProperty]
    private string _statusText = "Prêt.";

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private bool _hasFailures;

    [ObservableProperty]
    private int _totalCount;

    [ObservableProperty]
    private int _runningCount;

    [ObservableProperty]
    private int _okCount;

    [ObservableProperty]
    private int _failedCount;

    [ObservableProperty]
    private string _progressText = "";

    [ObservableProperty]
    private CatalogueEntry? _selectedPackage;

    [ObservableProperty]
    private int _hostFilterMode;

    [ObservableProperty]
    private string _hostFilterText = "";

    [ObservableProperty]
    private HostRow? _selectedHost;

    [ObservableProperty]
    private string _hostDetail = NoHostSelectedText;

    public MainViewModel(AppPaths paths, LogService log, IDialogService dialogs)
    {
        _log = log;
        _dialogs = dialogs;
        _settingsService = new SettingsService(paths.ConfigFile, log);
        _settings = _settingsService.Load();
        _catalogue = new CatalogueService(() => _settings.CataloguePath, log);
        _history = new HistoryService(paths.HistoryFile, log);
        _trustedHosts = new TrustedHostsService(log);
        _log.LineWritten += line => LogLineAdded?.Invoke(line);

        CatalogueView = CollectionViewSource.GetDefaultView(Catalogue);
        CatalogueView.SortDescriptions.Add(new SortDescription(nameof(CatalogueEntry.Name), ListSortDirection.Ascending));
        CatalogueView.Filter = FilterPackage;

        HostsView = CollectionViewSource.GetDefaultView(HostRows);
        HostsView.Filter = FilterHost;
        if (HostsView is ICollectionViewLiveShaping live && live.CanChangeLiveFiltering)
        {
            // L'état d'un poste change pendant l'opération : le filtre est réévalué à chaque changement.
            live.LiveFilteringProperties.Add(nameof(HostRow.Etat));
            live.IsLiveFiltering = true;
        }

        Targets.CollectionChanged += (_, _) => OnPropertyChanged(nameof(TargetCount));
        Basket.CollectionChanged += (_, _) => OnPropertyChanged(nameof(BasketCount));
        Catalogue.CollectionChanged += (_, _) => OnPropertyChanged(nameof(CatalogueCount));

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += OnTimerTick;
    }

    /// <summary>Ligne ajoutée au journal, éventuellement depuis un autre thread.</summary>
    public event Action<string>? LogLineAdded;

    public static string Version { get; } = typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "2.0.0";

    public string WindowTitle => $"WinPloy {Version}";

    public bool IsInstall
    {
        get => !IsUninstall;
        set => IsUninstall = !value;
    }

    public bool CanDeploy => !IsRunning;

    public ObservableCollection<string> Targets { get; } = [];

    public int TargetCount => Targets.Count;

    public ObservableCollection<CatalogueEntry> Catalogue { get; } = [];

    public int CatalogueCount => Catalogue.Count;

    public ICollectionView CatalogueView { get; }

    public ObservableCollection<CatalogueEntry> Basket { get; } = [];

    public int BasketCount => Basket.Count;

    public ObservableCollection<HostRow> HostRows { get; } = [];

    public ICollectionView HostsView { get; }

    public ObservableCollection<PackageResult> Results { get; } = [];

    // ============================================================
    // Démarrage et fermeture
    // ============================================================

    public void Initialize()
    {
        _log.Write($"WinPloy {Version} démarré.");
        UpdateSummary();
        _timer.Start();
        _ = RefreshCatalogueAsync();
    }

    /// <summary>Fermer WinPloy arrête les opérations en cours : confirmation demandée.</summary>
    public bool ConfirmClose()
    {
        return _run is null || _dialogs.Confirm(
            "Des opérations sont en cours. Fermer WinPloy les arrête.\r\n\r\nFermer quand même ?",
            "Fermer",
            MessageBoxImage.Warning);
    }

    public void Shutdown()
    {
        _timer.Stop();
        _run?.Stop.Cancel();
    }

    // ============================================================
    // Barre du haut
    // ============================================================

    [RelayCommand]
    private void SetCredential()
    {
        _credential = _dialogs.PromptCredential(_credential?.UserName);
        CredentialLabel = _credential is null ? CurrentContextText : _credential.UserName;
    }

    [RelayCommand]
    private async Task OpenSettingsAsync()
    {
        var updated = _dialogs.EditSettings(_settings, TrySaveSettings);
        if (updated is null)
        {
            return;
        }

        _settings = updated;
        _log.Write($"Réglages enregistrés. Catalogue : {updated.CataloguePath}");
        await RefreshCatalogueAsync();
    }

    [RelayCommand]
    private void ShowAbout()
    {
        _dialogs.ShowMessage(
            $"WinPloy {Version}\r\n\r\nDéploiement d'applications WinGet et de paquets personnalisés sur des postes Active Directory.\r\n\r\nAuteur : Constant Segretain",
            "À propos de WinPloy");
    }

    private bool TrySaveSettings(AppSettings settings)
    {
        if (_settingsService.TrySave(settings, out var error))
        {
            return true;
        }

        _dialogs.ShowMessage($"Impossible d'enregistrer les réglages : {error}", "Réglages", MessageBoxImage.Error);
        return false;
    }

    // ============================================================
    // 1 · Postes Active Directory
    // ============================================================

    [RelayCommand]
    private async Task SearchComputersAsync()
    {
        var text = PcSearchText;
        try
        {
            var computers = await Task.Run(() => _activeDirectory.SearchComputers(text));
            FoundComputers = computers;
            PcFoundLabel = $"{computers.Count} poste(s) trouvé(s)";
            _log.Write($"Recherche AD : {computers.Count} poste(s).");
        }
        catch (Exception ex)
        {
            _log.Write(ex.Message, LogService.Error);
        }
    }

    public void AddComputers(IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            if (!Targets.Any(t => TextMatch.SameHost(t, name)))
            {
                Targets.Add(name);
            }
        }
    }

    [RelayCommand]
    private void AddSelectedComputers(IList? items) => AddComputers(Items<string>(items));

    [RelayCommand]
    private void AddAllComputers() => AddComputers(FoundComputers);

    // ============================================================
    // 3 · Postes ciblés
    // ============================================================

    [RelayCommand]
    private void RemoveTargets(IList? items)
    {
        foreach (var computer in Items<string>(items))
        {
            Targets.Remove(computer);
        }
    }

    [RelayCommand]
    private void ClearTargets() => Targets.Clear();

    // ============================================================
    // 2 · Applications (catalogue partagé)
    // ============================================================

    [RelayCommand]
    private async Task RefreshCatalogueAsync()
    {
        var entries = await Task.Run(() => _catalogue.Import());
        ApplyCatalogue(entries);
    }

    [RelayCommand]
    private void AddWingetPackage()
    {
        var entry = _dialogs.EditWingetPackage(null);
        if (entry is not null)
        {
            ChangeCatalogue(() => _catalogue.Add(entry));
        }
    }

    [RelayCommand]
    private void AddCustomPackage()
    {
        var entry = _dialogs.EditCustomPackage(null, _settings.DepotRoot);
        if (entry is not null)
        {
            ChangeCatalogue(() => _catalogue.Add(entry));
        }
    }

    [RelayCommand]
    private void EditSelectedPackage()
    {
        if (SelectedPackage is { } entry)
        {
            EditPackage(entry);
        }
    }

    public void EditPackage(CatalogueEntry item)
    {
        var oldId = item.Id;
        var entry = item.IsCustom
            ? _dialogs.EditCustomPackage(item, _settings.DepotRoot)
            : _dialogs.EditWingetPackage(item);
        if (entry is not null)
        {
            ChangeCatalogue(() => _catalogue.Update(oldId, entry));
        }
    }

    [RelayCommand]
    private void RemoveFromCatalogue(IList? items)
    {
        var selected = Items<CatalogueEntry>(items);
        if (selected.Count == 0)
        {
            return;
        }

        var list = string.Join("\r\n", selected.Select(e => $"- {e.Name}"));
        var question = $"Supprimer {selected.Count} paquet(s) du catalogue ?\r\n\r\n{list}\r\n\r\nLes scripts sur le partage ne sont pas supprimés.";
        if (_dialogs.Confirm(question, "Confirmation", MessageBoxImage.Warning))
        {
            var ids = selected.Select(e => e.Id).ToList();
            ChangeCatalogue(() => _catalogue.Remove(ids));
        }
    }

    [RelayCommand]
    private void ClearPackageSearch() => PkgSearchText = "";

    partial void OnPkgSearchTextChanged(string value) => CatalogueView.Refresh();

    private bool FilterPackage(object item)
    {
        var text = PkgSearchText.Trim();
        return text.Length == 0
            || (item is CatalogueEntry entry
                && (entry.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase)
                    || entry.Id.Contains(text, StringComparison.CurrentCultureIgnoreCase)));
    }

    /// <summary>Affiche le catalogue et remplace les paquets du panier par leur version à jour (ou les retire).</summary>
    private void ApplyCatalogue(IReadOnlyList<CatalogueEntry> entries)
    {
        Catalogue.Clear();
        foreach (var entry in entries)
        {
            Catalogue.Add(entry);
        }

        for (var i = Basket.Count - 1; i >= 0; i--)
        {
            var match = entries.FirstOrDefault(e => e.HasSameId(Basket[i].Id));
            if (match is null)
            {
                Basket.RemoveAt(i);
            }
            else
            {
                Basket[i] = match;
            }
        }
    }

    /// <summary>Applique une modification du catalogue ; en cas d'erreur, recharge l'état réel du fichier.</summary>
    private void ChangeCatalogue(Func<IReadOnlyList<CatalogueEntry>> change)
    {
        try
        {
            ApplyCatalogue(change());
        }
        catch (Exception ex)
        {
            _log.Write(ex.Message, LogService.Error);
            _dialogs.ShowMessage(ex.Message, "Catalogue", MessageBoxImage.Error);
            ApplyCatalogue(_catalogue.Import());
        }
    }

    // ============================================================
    // Panier
    // ============================================================

    [RelayCommand]
    private void AddToBasket(IList? items)
    {
        foreach (var entry in Items<CatalogueEntry>(items))
        {
            if (!Basket.Any(b => b.HasSameId(entry.Id)))
            {
                Basket.Add(entry);
            }
        }
    }

    [RelayCommand]
    private void RemoveFromBasket(IList? items)
    {
        foreach (var entry in Items<CatalogueEntry>(items))
        {
            Basket.Remove(entry);
        }
    }

    [RelayCommand]
    private void ClearBasket() => Basket.Clear();

    // ============================================================
    // Exécution
    // ============================================================

    [RelayCommand]
    private async Task DeployAsync()
    {
        if (_run is not null)
        {
            return;
        }

        var targets = Targets.ToList();
        var packages = Basket.ToList();
        if (targets.Count == 0)
        {
            _log.Write("Aucun poste sélectionné.", LogService.Error);
            StatusText = "Aucun poste sélectionné.";
            return;
        }
        if (packages.Count == 0)
        {
            _log.Write("Panier vide.", LogService.Error);
            StatusText = "Panier vide.";
            return;
        }

        var action = IsUninstall ? DeployAction.Uninstall : DeployAction.Install;
        if (action == DeployAction.Uninstall || targets.Count > 5)
        {
            var verb = action == DeployAction.Install ? "Installer" : "Désinstaller";
            var names = string.Join(", ", packages.Select(p => p.Name));
            var question = $"{verb} {packages.Count} application(s) ({names}) sur {targets.Count} poste(s) ?";
            if (!_dialogs.Confirm(question, "Confirmer l'opération", MessageBoxImage.Question))
            {
                return;
            }
        }

        await RunDeploymentAsync(targets, packages, action, retry: false);
    }

    /// <summary>Relance la même opération uniquement sur les postes en échec ou arrêtés.</summary>
    [RelayCommand]
    private async Task RetryAsync()
    {
        if (_lastRun is null || _run is not null)
        {
            return;
        }

        var failed = HostRows.Where(r => HostStates.IsFailure(r.Etat)).Select(r => r.Poste).ToList();
        if (failed.Count > 0)
        {
            await RunDeploymentAsync(failed, _lastRun.Packages, _lastRun.Action, retry: true);
        }
    }

    [RelayCommand]
    private void Stop()
    {
        if (_run is null)
        {
            return;
        }

        var question = "Arrêter les opérations en cours ?\r\n\r\nUne installation déjà lancée sur un poste peut continuer et doit être vérifiée sur ce poste.";
        var run = _run;
        if (!_dialogs.Confirm(question, "Arrêter", MessageBoxImage.Warning) || _run != run)
        {
            return;
        }

        run.StopRequested = true;
        CanStop = false;
        StatusText = "Arrêt en cours…";
        _log.Write("=== Arrêt demandé par l'utilisateur. ===");
        run.Stop.Cancel();
    }

    private async Task RunDeploymentAsync(IReadOnlyList<string> targets, IReadOnlyList<CatalogueEntry> packages, DeployAction action, bool retry)
    {
        // Les hôtes approuvés ne sont touchés qu'en dernier recours, poste par poste, et seulement
        // si le client WinRM désigne TrustedHosts comme la cause du refus : sur un domaine, Kerberos
        // suffit et la liste reste intacte.
        var trustedHosts = new TrustedHostsGate(
            _trustedHosts.Add,
            message => _log.Write(message, LogService.Warning));

        var request = new DeploymentRequest
        {
            Targets = targets,
            Packages = packages,
            Action = action,
            Credential = _credential,
            PackageTimeoutMin = _settings.PackageTimeoutMin,
            MaxParallel = _settings.MaxParallel,
            DepotRoot = _settings.DepotRoot,
            WinRm = WinRmOptions.From(_settings),
            TransferMode = _settings.TransferMode,
            TrustedHosts = trustedHosts,
        };

        // Contrôlé avant de toucher au suivi : rien n'est lancé, donc rien à remettre en état.
        if (request.Validate() is { } invalid)
        {
            _log.Write(invalid, LogService.Error);
            _dialogs.ShowMessage(invalid, "Déploiement", MessageBoxImage.Warning);
            return;
        }

        var run = new RunState(targets.Count);
        _run = run;
        IsRunning = true;
        CanStop = true;

        if (!retry)
        {
            Results.Clear();
            HostRows.Clear();
        }
        foreach (var target in targets)
        {
            foreach (var old in Results.Where(r => TextMatch.SameHost(r.Ordinateur, target)).ToList())
            {
                Results.Remove(old);
            }

            var row = FindRow(target);
            if (row is null)
            {
                row = new HostRow(target);
                HostRows.Add(row);
            }
            row.Etat = HostStates.Pending;
            row.WinRM = "";
            row.Info = "En file d'attente.";
            row.Duree = "";
        }

        _lastRun = new LastRun(packages, action);

        var transfer = request.TransferMode == TransferMode.SmbPull ? "SMB" : "WinRM";
        _log.Write(
            $"=== {action} : {packages.Count} paquet(s) sur {targets.Count} poste(s), {request.MaxParallel} en parallèle, "
            + $"WinRM {request.WinRm.Label}, transfert {transfer} ===");
        UpdateRunStatus();
        UpdateSummary();

        try
        {
            await _engine.RunAsync(request, this, run.Stop.Token);
        }
        catch (Exception ex)
        {
            _log.Write($"Erreur interne du suivi : {ex.Message}", LogService.Error);
        }
        finally
        {
            // Le plus souvent il n'y a rien à retirer : aucun poste n'a eu besoin de la liste.
            _trustedHosts.Remove(trustedHosts.Added);
            FinishRun(run);
        }
    }

    private void FinishRun(RunState run)
    {
        var verb = run.StopRequested ? "Arrêté" : "Terminé";
        StatusText = $"{verb} : {run.Done}/{run.Total} poste(s).";
        _run = null;
        IsRunning = false;
        CanStop = false;
        _history.Append(Results.ToList());
        _log.Write($"=== Opération {verb.ToLowerInvariant()}. ===");
        UpdateSummary();
    }

    public void OnTargetStarted(string computer)
    {
        _run?.Running.TryAdd(computer, Stopwatch.StartNew());
        if (FindRow(computer) is { } row)
        {
            row.Etat = HostStates.Running;
            row.WinRM = "Vérification…";
            row.Info = "Contrôle WinRM…";
        }
        _log.Write($"{computer} : démarré.");
        UpdateSummary();
    }

    public void OnTargetStatus(string computer, TargetStatus status)
    {
        _log.Write($"{computer} : {status.Message}");
        if (FindRow(computer) is { } row)
        {
            row.Info = status.Message;
            if (status.WinRM.Length > 0)
            {
                row.WinRM = status.WinRM;
            }
        }
    }

    public void OnTargetCompleted(string computer, IReadOnlyList<TargetResult> results, TimeSpan? elapsed)
    {
        var now = DateTime.Now;
        foreach (var result in results)
        {
            Results.Add(new PackageResult(computer, result.Name, result.Id, result.Statut, result.Message, now));
        }

        if (FindRow(computer) is { } row)
        {
            var outcome = HostOutcome.Compute(Results.Where(r => TextMatch.SameHost(r.Ordinateur, computer)).ToList());
            if (outcome is not null)
            {
                row.Etat = outcome.Etat;
                row.Info = outcome.Info;
            }
            if (elapsed is { } duration)
            {
                row.Duree = TextMatch.FormatElapsed(duration);
            }
        }

        if (_run is not null)
        {
            _run.Done++;
            _run.Running.Remove(computer);
        }
        if (SelectedHost is not null && TextMatch.SameHost(SelectedHost.Poste, computer))
        {
            UpdateHostDetail();
        }
        if (elapsed is not null)
        {
            _log.Write($"{computer} : terminé.");
        }
        UpdateRunStatus();
        UpdateSummary();
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        if (_run is null)
        {
            return;
        }

        foreach (var (computer, clock) in _run.Running)
        {
            if (FindRow(computer) is { } row)
            {
                row.Duree = TextMatch.FormatElapsed(clock.Elapsed);
            }
        }
    }

    private void UpdateRunStatus()
    {
        if (_run is null)
        {
            return;
        }

        var prefix = _run.StopRequested ? "Arrêt en cours…" : "Traitement :";
        StatusText = $"{prefix} {_run.Done}/{_run.Total} poste(s) terminé(s)";
    }

    private void UpdateSummary()
    {
        var total = HostRows.Count;
        var running = HostRows.Count(r => HostStates.IsActive(r.Etat));
        var ok = HostRows.Count(r => r.Etat == HostStates.Ok);
        var failed = HostRows.Count(r => HostStates.IsFailure(r.Etat));

        TotalCount = total;
        RunningCount = running;
        OkCount = ok;
        FailedCount = failed;
        ProgressValue = total > 0 ? Math.Round(100.0 * (total - running) / total) : 0;
        ProgressText = total > 0 ? $"{total - running}/{total}" : "";
        HasFailures = failed > 0;
        CanRetry = failed > 0 && !IsRunning;
        CanExport = Results.Count > 0;
    }

    // ============================================================
    // Suivi par poste, détail, export
    // ============================================================

    partial void OnHostFilterModeChanged(int value) => HostsView.Refresh();

    partial void OnHostFilterTextChanged(string value) => HostsView.Refresh();

    partial void OnSelectedHostChanged(HostRow? value) => UpdateHostDetail();

    private bool FilterHost(object item)
    {
        if (item is not HostRow row)
        {
            return false;
        }

        var text = HostFilterText.Trim();
        if (text.Length > 0 && !TextMatch.ContainsLike(row.Poste, text))
        {
            return false;
        }

        return HostFilterMode switch
        {
            1 => HostStates.IsActive(row.Etat),
            2 => HostStates.IsFailure(row.Etat),
            3 => row.Etat == HostStates.Ok,
            _ => true,
        };
    }

    private void UpdateHostDetail()
    {
        var row = SelectedHost;
        if (row is null)
        {
            HostDetail = NoHostSelectedText;
            return;
        }

        var head = $"{row.Poste} — {row.Etat}" + (row.Duree.Length > 0 ? $" ({row.Duree})" : "");
        var results = Results.Where(r => TextMatch.SameHost(r.Ordinateur, row.Poste)).ToList();
        HostDetail = results.Count == 0
            ? $"{head}\r\n{row.Info}"
            : head + "\r\n" + string.Join("\r\n", results.Select(r => $"[{r.Statut}] {r.Paquet} : {r.Message}"));
    }

    [RelayCommand]
    private void CopyHostDetail() => ClipboardHelper.Copy(HostDetail, _log);

    public void ShowResultDetails(PackageResult result) => _dialogs.ShowResultDetails(result);

    [RelayCommand]
    private void Export()
    {
        var path = _dialogs.AskCsvPath($"WinPloy-resultats-{DateTime.Now:yyyyMMdd-HHmm}.csv");
        if (path is null)
        {
            return;
        }

        try
        {
            CsvExporter.Export(path, Results);
            _log.Write($"Résultats exportés : {path}");
        }
        catch (Exception ex)
        {
            _log.Write($"Export impossible : {ex.Message}", LogService.Error);
        }
    }

    // ============================================================
    // Outils
    // ============================================================

    private HostRow? FindRow(string computer) => HostRows.FirstOrDefault(r => TextMatch.SameHost(r.Poste, computer));

    // La sélection d'une liste change quand on retire des éléments : copie avant traitement.
    private static List<T> Items<T>(IList? items) => items?.OfType<T>().ToList() ?? new List<T>();

    private sealed record LastRun(IReadOnlyList<CatalogueEntry> Packages, DeployAction Action);

    private sealed class RunState(int total)
    {
        public int Total { get; } = total;

        public int Done { get; set; }

        public bool StopRequested { get; set; }

        public CancellationTokenSource Stop { get; } = new();

        public Dictionary<string, Stopwatch> Running { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
