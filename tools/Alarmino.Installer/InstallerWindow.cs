using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace Alarmino.Installer;

/// <summary>Ventana principal del instalador/desinstalador (WPF en código, español).</summary>
public sealed class InstallerWindow : Window
{
    private readonly bool _uninstallMode;
    private TextBox? _pathBox;
    private CheckBox? _autoStartBox;
    private CheckBox? _desktopBox;
    private CheckBox? _runNowBox;
    private CheckBox? _deleteDataBox;
    private Button? _actionButton;

    /// <param name="uninstallMode">
    /// Si viene de <see cref="Program"/> (que también detecta el nombre del ejecutable
    /// «UninstallAlarmino»), se respeta su decisión en lugar de fiarse solo del argumento.
    /// </param>
    public InstallerWindow(string[] args, bool? uninstallMode = null)
    {
        _uninstallMode = uninstallMode ??
            args.Any(a => a.Equals("/Uninstall", StringComparison.OrdinalIgnoreCase));

        Title = "Alarmino — Instalador";
        Width = 470;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        BuildLayout();
        if (_uninstallMode)
        {
            PrepareUninstall();
        }
    }

    private void BuildLayout()
    {
        var root = new StackPanel { Margin = new Thickness(20) };

        var header = new TextBlock
        {
            Text = _uninstallMode ? "Desinstalar Alarmino" : "Instalar Alarmino",
            FontSize = 20,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 8),
        };
        root.Children.Add(header);

        var subtitle = new TextBlock
        {
            Text = _uninstallMode
                ? "Se eliminarán los accesos directos, el autostart y la carpeta de instalación. Tus alarmas y audios personalizados se conservan salvo que marques la opción de abajo."
                : "Sirenas del colegio · Windows 10/11 · v0.1.1",
            TextWrapping = TextWrapping.Wrap,
            Foreground = SystemColors.GrayTextBrush,
            Margin = new Thickness(0, 0, 0, 14),
        };
        root.Children.Add(subtitle);

        if (_uninstallMode)
        {
            _deleteDataBox = new CheckBox
            {
                Content = "Eliminar también mis alarmas y audios personalizados",
                IsChecked = false,
                Margin = new Thickness(0, 0, 0, 12),
            };
            root.Children.Add(_deleteDataBox);

            var runButton = new Button
            {
                Content = "Desinstalar",
                Height = 36,
                Margin = new Thickness(0, 0, 0, 8),
            };
            runButton.Click += (_, _) => RunUninstall();
            root.Children.Add(runButton);

            var cancelButton = new Button { Content = "Cancelar", Height = 36 };
            cancelButton.Click += (_, _) => Close();
            root.Children.Add(cancelButton);
        }
        else
        {
            var dirLabel = new TextBlock { Text = "Carpeta de instalación", Margin = new Thickness(0, 0, 0, 4) };
            root.Children.Add(dirLabel);

            _pathBox = new TextBox
            {
                Text = InstallerEngine.DefaultInstallDir,
                Margin = new Thickness(0, 0, 0, 12),
            };
            root.Children.Add(_pathBox);

            _autoStartBox = new CheckBox
            {
                Content = "Abrir Alarmino al iniciar Windows",
                IsChecked = true,
                Margin = new Thickness(0, 0, 0, 6),
            };
            root.Children.Add(_autoStartBox);

            _desktopBox = new CheckBox
            {
                Content = "Crear acceso directo en el escritorio",
                IsChecked = true,
                Margin = new Thickness(0, 0, 0, 6),
            };
            root.Children.Add(_desktopBox);

            _runNowBox = new CheckBox
            {
                Content = "Ejecutar Alarmino al terminar",
                IsChecked = true,
                Margin = new Thickness(0, 0, 0, 14),
            };
            root.Children.Add(_runNowBox);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };

            var cancelButton = new Button { Content = "Cancelar", Width = 100, Height = 34, Margin = new Thickness(0, 0, 8, 0) };
            cancelButton.Click += (_, _) => Close();
            buttons.Children.Add(cancelButton);

            _actionButton = new Button { Content = "Instalar", Width = 110, Height = 34 };
            _actionButton.Click += (_, _) => RunInstall();
            buttons.Children.Add(_actionButton);

            root.Children.Add(buttons);
        }

        Content = root;
    }

    private void PrepareUninstall()
    {
        string? installed = InstallerEngine.GetInstalledLocation();
        if (installed is null || !Directory.Exists(installed))
        {
            MessageBox.Show("Alarmino no está instalado o ya fue desinstalado.",
                "Alarmino", MessageBoxButton.OK, MessageBoxImage.Information);
            Close();
        }
    }

    private void RunInstall()
    {
        var target = Path.GetFullPath(_pathBox?.Text.Trim() ?? InstallerEngine.DefaultInstallDir);

        try
        {
            _actionButton!.IsEnabled = false;
            InstallerEngine.Install(
                target,
                autoStart: _autoStartBox?.IsChecked == true,
                desktopShortcut: _desktopBox?.IsChecked == true);

            string runPath = Path.Combine(target, "Alarmino.exe");
            if (_runNowBox?.IsChecked == true && File.Exists(runPath))
            {
                Process.Start(new ProcessStartInfo(runPath) { UseShellExecute = true });
            }

            MessageBox.Show($"Alarmino se instaló correctamente en:\n{target}",
                "Alarmino", MessageBoxButton.OK, MessageBoxImage.Information);
            Close();
        }
        catch (Exception ex)
        {
            _actionButton!.IsEnabled = true;
            MessageBox.Show($"Ocurrió un error durante la instalación:\n{ex.Message}",
                "Alarmino", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RunUninstall()
    {
        string? installed = InstallerEngine.GetInstalledLocation()
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Alarmino");

        if (MessageBox.Show($"¿Seguro que quieres desinstalar Alarmino de:\n{installed}?",
                "Alarmino", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            InstallerEngine.Uninstall(installed, deleteData: _deleteDataBox?.IsChecked == true);
            MessageBox.Show("Alarmino se ha desinstalado.", "Alarmino",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error al desinstalar:\n{ex.Message}", "Alarmino",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
