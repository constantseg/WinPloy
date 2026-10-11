using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using WinPloy.Core.Deployment;
using WinPloy.Core.Models;

namespace WinPloy.Views;

public partial class CustomPackageWindow : Window
{
    private readonly CatalogueEntry? _existing;
    private readonly string _depotRoot;

    private IReadOnlyDictionary<string, string> _files = new Dictionary<string, string>();

    public CustomPackageWindow(CatalogueEntry? existing, string depotRoot)
    {
        InitializeComponent();
        _existing = existing;
        _depotRoot = depotRoot;

        if (existing is null)
        {
            Title = "Créer un paquet personnalisé";
            UpdateIntegrityState();
            return;
        }

        Title = "Modifier le paquet personnalisé";
        TxtName.Text = existing.Name;
        TxtInstall.Text = existing.InstallScript;
        TxtUninstall.Text = existing.UninstallScript;
        ChkRunAsSystem.IsChecked = existing.RunsAsSystem;
        _files = existing.Files;
        ChkIntegrity.IsChecked = existing.HasIntegrityManifest;
        UpdateIntegrityState();
    }

    public CatalogueEntry? Result { get; private set; }

    private void BtnBrowseInstall_Click(object sender, RoutedEventArgs e) => Browse(TxtInstall);

    private void BtnBrowseUninstall_Click(object sender, RoutedEventArgs e) => Browse(TxtUninstall);

    private void Browse(TextBox target)
    {
        var dialog = new OpenFileDialog
        {
            Filter = PackageValidation.FileDialogFilter,
            InitialDirectory = _depotRoot,
        };
        if (dialog.ShowDialog(this) == true)
        {
            target.Text = dialog.FileName;
        }
    }

    private void ChkIntegrity_Changed(object sender, RoutedEventArgs e) => UpdateIntegrityState();

    private void UpdateIntegrityState()
    {
        var enabled = ChkIntegrity.IsChecked == true;
        BtnComputeHash.IsEnabled = enabled;
        LblIntegrity.Text = enabled
            ? _files.Count > 0
                ? $"{_files.Count} fichier(s) dans l'empreinte."
                : "Empreinte non calculée : elle le sera à l'enregistrement."
            : "";
    }

    private async void BtnComputeHash_Click(object sender, RoutedEventArgs e)
    {
        if (await ComputeManifestAsync() is { } error)
        {
            MessageBox.Show(this, error, "Empreinte", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Calcule l'empreinte du dossier du script d'installation. Retourne le message d'erreur, ou null.</summary>
    private async Task<string?> ComputeManifestAsync()
    {
        var install = TxtInstall.Text.Trim();
        if (install.Length == 0)
        {
            return "Indiquer d'abord le script d'installation : son dossier est celui dont l'empreinte est calculée.";
        }

        var directory = Path.GetDirectoryName(install) ?? "";
        if (!Directory.Exists(directory))
        {
            return $"Dossier du paquet inaccessible : {directory}";
        }

        LblIntegrity.Text = "Calcul en cours…";
        BtnComputeHash.IsEnabled = false;
        BtnOk.IsEnabled = false;
        try
        {
            _files = await Task.Run(() => PackageIntegrity.Compute(directory));
        }
        catch (Exception ex)
        {
            _files = new Dictionary<string, string>();
            return $"Calcul de l'empreinte impossible : {ex.Message}";
        }
        finally
        {
            BtnOk.IsEnabled = true;
            UpdateIntegrityState();
        }

        return _files.Count == 0 ? $"Aucun fichier trouvé dans {directory}." : null;
    }

    private async void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        var name = TxtName.Text.Trim();
        var install = TxtInstall.Text.Trim();
        var uninstall = TxtUninstall.Text.Trim();
        var integrity = ChkIntegrity.IsChecked == true;

        string? error;
        if (name.Length == 0)
        {
            error = "Nom du paquet requis.";
        }
        else if (install.Length == 0)
        {
            error = "Script d'installation requis.";
        }
        else
        {
            error = PackageValidation.ValidateScriptPath(install, "Script d'installation", _depotRoot);
            if (error is null && uninstall.Length > 0)
            {
                error = PackageValidation.ValidateScriptPath(uninstall, "Script de désinstallation", _depotRoot);
            }
            if (error is null && integrity && uninstall.Length > 0
                && !PackageValidation.SameDirectory(Path.GetDirectoryName(install) ?? "", Path.GetDirectoryName(uninstall) ?? ""))
            {
                error = "Contrôle d'empreinte : les deux scripts doivent être dans le même dossier, "
                    + "puisque l'empreinte couvre un seul dossier de paquet.";
            }
            if (error is null && integrity && _files.Count == 0)
            {
                error = await ComputeManifestAsync();
            }
        }

        if (error is not null)
        {
            MessageBox.Show(this, error, "Erreur", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var id = _existing?.Id ?? PackageValidation.MakeCustomId(name);
        var runAs = ChkRunAsSystem.IsChecked == true ? CatalogueEntry.RunAsSystem : CatalogueEntry.RunAsUser;
        Result = CatalogueEntry.Create(id, name, install, uninstall, runAs, integrity ? _files : null);
        DialogResult = true;
    }
}
