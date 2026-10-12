using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using WinPloy.Core.Deployment;
using WinPloy.Core.Models;
using WinPloy.Properties;

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
            Title = Strings.Custom_CreateTitle;
            UpdateIntegrityState();
            return;
        }

        Title = Strings.Custom_EditTitle;
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
                ? string.Format(Strings.Custom_HashFileCount, _files.Count)
                : Strings.Custom_HashPending
            : "";
    }

    private async void BtnComputeHash_Click(object sender, RoutedEventArgs e)
    {
        if (await ComputeManifestAsync() is { } error)
        {
            MessageBox.Show(this, error, Strings.Custom_HashCaption, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Calcule l'empreinte du dossier du script d'installation. Retourne le message d'erreur, ou null.</summary>
    private async Task<string?> ComputeManifestAsync()
    {
        var install = TxtInstall.Text.Trim();
        if (install.Length == 0)
        {
            return Strings.Custom_InstallFirst;
        }

        var directory = Path.GetDirectoryName(install) ?? "";
        if (!Directory.Exists(directory))
        {
            return string.Format(Strings.Custom_FolderUnreachable, directory);
        }

        LblIntegrity.Text = Strings.Custom_Computing;
        BtnComputeHash.IsEnabled = false;
        BtnOk.IsEnabled = false;
        try
        {
            _files = await Task.Run(() => PackageIntegrity.Compute(directory));
        }
        catch (Exception ex)
        {
            _files = new Dictionary<string, string>();
            return string.Format(Strings.Custom_HashFailed, ex.Message);
        }
        finally
        {
            BtnOk.IsEnabled = true;
            UpdateIntegrityState();
        }

        return _files.Count == 0 ? string.Format(Strings.Custom_NoFiles, directory) : null;
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
            error = Strings.Custom_NameRequired;
        }
        else if (install.Length == 0)
        {
            error = Strings.Custom_InstallRequired;
        }
        else
        {
            error = PackageValidation.ValidateScriptPath(install, Strings.Custom_InstallScript, _depotRoot);
            if (error is null && uninstall.Length > 0)
            {
                error = PackageValidation.ValidateScriptPath(uninstall, Strings.Custom_UninstallScript, _depotRoot);
            }
            if (error is null && integrity && uninstall.Length > 0
                && !PackageValidation.SameDirectory(Path.GetDirectoryName(install) ?? "", Path.GetDirectoryName(uninstall) ?? ""))
            {
                error = Strings.Custom_SameFolderRequired;
            }
            if (error is null && integrity && _files.Count == 0)
            {
                error = await ComputeManifestAsync();
            }
        }

        if (error is not null)
        {
            MessageBox.Show(this, error, Strings.Common_Error, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var id = _existing?.Id ?? PackageValidation.MakeCustomId(name);
        var runAs = ChkRunAsSystem.IsChecked == true ? CatalogueEntry.RunAsSystem : CatalogueEntry.RunAsUser;
        Result = CatalogueEntry.Create(id, name, install, uninstall, runAs, integrity ? _files : null);
        DialogResult = true;
    }
}
