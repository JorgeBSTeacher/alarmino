using System.Threading;
using System.Windows;
using Alarmino.Services;
using Alarmino.ViewModels;
using Alarmino.Views;

namespace Alarmino;

public partial class App : Application
{
    /// <summary>Verdadero cuando el usuario elige «Salir» en la bandeja.</summary>
    public static bool IsExiting;

    private Mutex? _singleInstanceMutex;
    private TrayIconService? _trayIcon;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(true, @"Global\Alarmino", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("Alarmino ya está en ejecución.", "Alarmino",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        var mainViewModel = new MainViewModel();
        _trayIcon = new TrayIconService(mainViewModel);
        mainViewModel.AttachTrayIcon(_trayIcon);
        mainViewModel.Initialize();

        var window = new MainWindow
        {
            DataContext = mainViewModel,
        };

        _trayIcon.NotifyIcon.Visible = true;
        MainWindow = window;

        bool startMinimized = Environment.GetCommandLineArgs().Any(a =>
            a.Equals("/min", StringComparison.OrdinalIgnoreCase));

        if (startMinimized)
        {
            // Autostart: abrir en bandeja sin ventana visible.
            window.WindowState = WindowState.Minimized;
            window.Show();
            window.Hide();
        }
        else
        {
            window.Show();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
