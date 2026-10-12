using System.Globalization;
using System.Windows;
using Microsoft.Win32;
using WinPloy.Core.Models;
using WinPloy.Core.Services;
using WinPloy.Properties;
using WinPloy.Services;

namespace WinPloy.Views;

public partial class SettingsWindow : Window
{
    private readonly Func<AppSettings, bool> _trySave;
    private bool _loaded;

    public SettingsWindow(AppSettings current, Func<AppSettings, bool> trySave)
    {
        InitializeComponent();
        _trySave = trySave;
        TxtCataloguePath.Text = current.CataloguePath;
        TxtTimeout.Text = current.PackageTimeoutMin.ToString(CultureInfo.InvariantCulture);
        TxtParallel.Text = current.MaxParallel.ToString(CultureInfo.InvariantCulture);
        TxtWinRmPort.Text = current.WinRmPort.ToString(CultureInfo.InvariantCulture);
        ChkUseSsl.IsChecked = current.UseSsl;
        ChkSkipCertificate.IsChecked = current.SkipCertificateCheck;
        RadTransferSmb.IsChecked = current.TransferMode == TransferMode.SmbPull;
        RadTransferWinRm.IsChecked = !RadTransferSmb.IsChecked;
        CmbLanguage.ItemsSource = AppSettings.SupportedLanguages
            .Select(language => new KeyValuePair<string, string>(language, UiLanguage.DisplayName(language)))
            .ToList();
        CmbLanguage.SelectedValue = current.Language;
        LblTimeoutHint.Text = string.Format(Strings.Settings_Range, AppSettings.MinTimeoutMin, AppSettings.MaxTimeoutMin);
        _loaded = true;
        UpdateParallelHint();
    }

    /// <summary>Réglages enregistrés, renseignés quand la fenêtre se ferme par « Enregistrer ».</summary>
    public AppSettings? Result { get; private set; }

    private void BtnBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = Strings.Settings_CatalogueFilter,
            CheckFileExists = false,
        };
        if (dialog.ShowDialog(this) == true)
        {
            TxtCataloguePath.Text = dialog.FileName;
        }
    }

    /// <summary>Le port suit le protocole tant qu'il porte la valeur par défaut de l'autre protocole.</summary>
    private void ChkUseSsl_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded)
        {
            return;
        }

        var useSsl = ChkUseSsl.IsChecked == true;
        var current = SettingsService.ParseClamped(TxtWinRmPort.Text.Trim(), AppSettings.MinPort, AppSettings.MaxPort, -1);
        if (current == AppSettings.DefaultPortFor(!useSsl))
        {
            TxtWinRmPort.Text = AppSettings.DefaultPortFor(useSsl).ToString(CultureInfo.InvariantCulture);
        }
    }

    private void TxtParallel_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => UpdateParallelHint();

    private void UpdateParallelHint()
    {
        if (!_loaded)
        {
            return;
        }

        var parallel = SettingsService.ParseClamped(TxtParallel.Text.Trim(), AppSettings.MinParallel, AppSettings.MaxParallelLimit, -1);
        LblParallelHint.Text = parallel > AppSettings.RecommendedMaxParallel
            ? string.Format(Strings.Settings_ParallelHigh, AppSettings.MinParallel, AppSettings.MaxParallelLimit, AppSettings.RecommendedMaxParallel)
            : string.Format(Strings.Settings_Range, AppSettings.MinParallel, AppSettings.MaxParallelLimit);
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        var path = TxtCataloguePath.Text.Trim();
        var timeout = SettingsService.ParseClamped(TxtTimeout.Text.Trim(), AppSettings.MinTimeoutMin, AppSettings.MaxTimeoutMin, -1);
        var parallel = SettingsService.ParseClamped(TxtParallel.Text.Trim(), AppSettings.MinParallel, AppSettings.MaxParallelLimit, -1);
        var port = SettingsService.ParseClamped(TxtWinRmPort.Text.Trim(), AppSettings.MinPort, AppSettings.MaxPort, -1);

        if (path.Length == 0)
        {
            Warn(Strings.Settings_CatalogueRequired);
            return;
        }
        if (timeout < 0)
        {
            Warn(string.Format(Strings.Settings_TimeoutInvalid, AppSettings.MinTimeoutMin, AppSettings.MaxTimeoutMin));
            return;
        }
        if (parallel < 0)
        {
            Warn(string.Format(Strings.Settings_ParallelInvalid, AppSettings.MinParallel, AppSettings.MaxParallelLimit));
            return;
        }
        if (port < 0)
        {
            Warn(string.Format(Strings.Settings_PortInvalid, AppSettings.MinPort, AppSettings.MaxPort));
            return;
        }

        var updated = new AppSettings
        {
            CataloguePath = path,
            PackageTimeoutMin = timeout,
            MaxParallel = parallel,
            WinRmPort = port,
            UseSsl = ChkUseSsl.IsChecked == true,
            SkipCertificateCheck = ChkSkipCertificate.IsChecked == true,
            TransferMode = RadTransferSmb.IsChecked == true ? TransferMode.SmbPull : TransferMode.WinRm,
            Language = CmbLanguage.SelectedValue as string ?? AppSettings.SystemLanguage,
        };
        if (!_trySave(updated))
        {
            return;
        }

        Result = updated;
        DialogResult = true;
    }

    private void Warn(string text) => MessageBox.Show(this, text, Strings.Settings_Title, MessageBoxButton.OK, MessageBoxImage.Warning);
}
