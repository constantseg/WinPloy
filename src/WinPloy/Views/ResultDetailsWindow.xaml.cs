using System.Globalization;
using System.Windows;
using WinPloy.Converters;
using WinPloy.Core.Models;
using WinPloy.Core.Services;
using WinPloy.Properties;
using WinPloy.Services;

namespace WinPloy.Views;

public partial class ResultDetailsWindow : Window
{
    private readonly LogService _log;

    public ResultDetailsWindow(PackageResult result, LogService log)
    {
        InitializeComponent();
        _log = log;
        TxtDetails.Text = string.Join("\r\n",
            string.Format(Strings.Details_Computer, result.Ordinateur),
            string.Format(Strings.Details_Package, result.Paquet),
            string.Format(Strings.Details_Id, result.Id),
            string.Format(Strings.Details_Status, StatusLabelConverter.Label(result.Statut)),
            string.Format(Strings.Details_Date, result.Horodatage.ToString(CultureInfo.CurrentCulture)),
            "",
            Strings.Details_Message,
            result.Message);
    }

    private void BtnCopy_Click(object sender, RoutedEventArgs e) => ClipboardHelper.Copy(TxtDetails.Text, _log);
}
