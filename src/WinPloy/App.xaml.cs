using System.Windows;
using System.Windows.Threading;
using WinPloy.Core.Services;
using WinPloy.Properties;
using WinPloy.Services;
using WinPloy.ViewModels;
using WinPloy.Views;

namespace WinPloy;

public partial class App : Application
{
    private LogService? _log;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) => args.SetObserved();

        var paths = AppPaths.Default;
        try
        {
            paths.EnsureDataDir();
        }
        catch (Exception)
        {
            // Journal et réglages indisponibles : l'interface reste utilisable avec les valeurs par défaut.
        }

        _log = new LogService(paths.LogFile);
        UiLanguage.Apply(new SettingsService(paths.ConfigFile, _log).Load().Language);
        var viewModel = new MainViewModel(paths, _log, new DialogService(_log));
        var window = new MainWindow(viewModel);
        MainWindow = window;
        window.Show();
        viewModel.Initialize();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _log?.Write(string.Format(Strings.App_UnexpectedError, e.Exception.Message), LogService.Error);
        MessageBox.Show(e.Exception.Message, "WinPloy", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
