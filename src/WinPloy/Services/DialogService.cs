using System.Windows;
using Microsoft.Win32;
using WinPloy.Core.Models;
using WinPloy.Core.Services;
using WinPloy.Properties;
using WinPloy.Views;
using PSCredential = System.Management.Automation.PSCredential;

namespace WinPloy.Services;

/// <summary>Boîtes de dialogue utilisées par le ViewModel principal.</summary>
public interface IDialogService
{
    void ShowMessage(string text, string title, MessageBoxImage icon = MessageBoxImage.Information);

    bool Confirm(string text, string title, MessageBoxImage icon);

    /// <summary>Retourne les réglages enregistrés, ou null si l'utilisateur annule.</summary>
    AppSettings? EditSettings(AppSettings current, Func<AppSettings, bool> trySave);

    CatalogueEntry? EditWingetPackage(CatalogueEntry? existing);

    CatalogueEntry? EditCustomPackage(CatalogueEntry? existing, string depotRoot);

    /// <summary>Null = Annuler, donc contexte courant.</summary>
    PSCredential? PromptCredential(string? userName);

    void ShowResultDetails(PackageResult result);

    string? AskCsvPath(string defaultFileName);
}

public sealed class DialogService : IDialogService
{
    private readonly LogService _log;

    public DialogService(LogService log) => _log = log;

    private static Window? ActiveOwner => Application.Current?.MainWindow is { IsVisible: true } window ? window : null;

    public void ShowMessage(string text, string title, MessageBoxImage icon = MessageBoxImage.Information)
        => Show(text, title, MessageBoxButton.OK, icon);

    public bool Confirm(string text, string title, MessageBoxImage icon)
        => Show(text, title, MessageBoxButton.YesNo, icon) == MessageBoxResult.Yes;

    public AppSettings? EditSettings(AppSettings current, Func<AppSettings, bool> trySave)
    {
        var dialog = new SettingsWindow(current, trySave) { Owner = ActiveOwner };
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    public CatalogueEntry? EditWingetPackage(CatalogueEntry? existing)
    {
        var dialog = new WingetPackageWindow(existing, _log) { Owner = ActiveOwner };
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    public CatalogueEntry? EditCustomPackage(CatalogueEntry? existing, string depotRoot)
    {
        var dialog = new CustomPackageWindow(existing, depotRoot) { Owner = ActiveOwner };
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    public PSCredential? PromptCredential(string? userName)
    {
        var dialog = new CredentialWindow(userName) { Owner = ActiveOwner };
        return dialog.ShowDialog() == true ? dialog.Credential : null;
    }

    public void ShowResultDetails(PackageResult result)
        => new ResultDetailsWindow(result, _log) { Owner = ActiveOwner }.ShowDialog();

    public string? AskCsvPath(string defaultFileName)
    {
        var dialog = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = defaultFileName };
        var owner = ActiveOwner;
        var accepted = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        return accepted == true ? dialog.FileName : null;
    }

    private static MessageBoxResult Show(string text, string title, MessageBoxButton buttons, MessageBoxImage icon)
    {
        var owner = ActiveOwner;
        return owner is null
            ? MessageBox.Show(text, title, buttons, icon)
            : MessageBox.Show(owner, text, title, buttons, icon);
    }
}

public static class ClipboardHelper
{
    public static void Copy(string text, LogService log)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            log.Write(string.Format(Strings.Clipboard_CopyFailed, ex.Message), LogService.Warning);
        }
    }
}
