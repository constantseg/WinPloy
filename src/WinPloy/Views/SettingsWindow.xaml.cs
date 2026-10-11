using System.Globalization;
using System.Windows;
using Microsoft.Win32;
using WinPloy.Core.Models;
using WinPloy.Core.Services;

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
        _loaded = true;
        UpdateParallelHint();
    }

    /// <summary>Réglages enregistrés, renseignés quand la fenêtre se ferme par « Enregistrer ».</summary>
    public AppSettings? Result { get; private set; }

    private void BtnBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Catalogue JSON (*.json)|*.json|Tous les fichiers (*.*)|*.*",
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
            ? $"De {AppSettings.MinParallel} à {AppSettings.MaxParallelLimit}. Au-delà de {AppSettings.RecommendedMaxParallel}, "
                + "le poste d'administration devient le goulot d'étranglement en transfert WinRM : préférer le mode SMB."
            : $"De {AppSettings.MinParallel} à {AppSettings.MaxParallelLimit}.";
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        var path = TxtCataloguePath.Text.Trim();
        var timeout = SettingsService.ParseClamped(TxtTimeout.Text.Trim(), AppSettings.MinTimeoutMin, AppSettings.MaxTimeoutMin, -1);
        var parallel = SettingsService.ParseClamped(TxtParallel.Text.Trim(), AppSettings.MinParallel, AppSettings.MaxParallelLimit, -1);
        var port = SettingsService.ParseClamped(TxtWinRmPort.Text.Trim(), AppSettings.MinPort, AppSettings.MaxPort, -1);

        if (path.Length == 0)
        {
            Warn("Le chemin du catalogue est obligatoire.");
            return;
        }
        if (timeout < 0)
        {
            Warn($"Le délai doit être compris entre {AppSettings.MinTimeoutMin} et {AppSettings.MaxTimeoutMin} minutes.");
            return;
        }
        if (parallel < 0)
        {
            Warn($"Le parallélisme doit être compris entre {AppSettings.MinParallel} et {AppSettings.MaxParallelLimit}.");
            return;
        }
        if (port < 0)
        {
            Warn($"Le port WinRM doit être compris entre {AppSettings.MinPort} et {AppSettings.MaxPort}.");
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
        };
        if (!_trySave(updated))
        {
            return;
        }

        Result = updated;
        DialogResult = true;
    }

    private void Warn(string text) => MessageBox.Show(this, text, "Réglages", MessageBoxButton.OK, MessageBoxImage.Warning);
}
