using System.Windows;
using System.Windows.Controls;
using WinPloy.Properties;
using PSCredential = System.Management.Automation.PSCredential;

namespace WinPloy.Views;

/// <summary>Remplace Get-Credential : le mot de passe reste en SecureString.</summary>
public partial class CredentialWindow : Window
{
    public CredentialWindow(string? userName)
    {
        InitializeComponent();
        TxtUser.Text = userName ?? "";
        Loaded += (_, _) => (TxtUser.Text.Length > 0 ? (Control)PwdBox : TxtUser).Focus();
    }

    public PSCredential? Credential { get; private set; }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        var user = TxtUser.Text.Trim();
        if (user.Length == 0)
        {
            MessageBox.Show(this, Strings.Cred_UserRequired, Strings.Cred_Credentials, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var password = PwdBox.SecurePassword;
        password.MakeReadOnly();
        Credential = new PSCredential(user, password);
        DialogResult = true;
    }
}
