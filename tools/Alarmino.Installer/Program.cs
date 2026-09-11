using System;
using System.Diagnostics;
using System.Windows;

namespace Alarmino.Installer;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        bool helpRequested = args.Any(a => a is "/?" or "-h" or "--help");

        if (helpRequested)
        {
            MessageBox.Show("Alarmino Installer\n\n"
                + "Instala la app en %LocalAppData%\\Alarmino.\n\n"
                + "Opciones:\n"
                + "  /S          instalación silenciosa\n"
                + "  /Uninstall  desinstalar\n"
                + "  /D=dir      carpeta de instalación (con /S)",
                "Alarmino Installer", MessageBoxButton.OK, MessageBoxImage.Information);
            return 0;
        }

        // Modo ayudante: borra la carpeta de instalación y se autodeleimina.
        string? deleteDir = TryGetArgValue(args, "--delete-dir");
        if (deleteDir is not null)
        {
            Thread.Sleep(900);
            try
            {
                if (Directory.Exists(deleteDir))
                {
                    Directory.Delete(deleteDir, recursive: true);
                }
            }
            catch
            {
                // Si algo sigue en uso, se borra en el próximo reinicio.
            }

            try
            {
                File.Delete(Environment.ProcessPath!);
            }
            catch
            {
                // No pasa nada si el SO retiene el fichero.
            }

            return 0;
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };

        bool silent = args.Contains("/S", StringComparer.OrdinalIgnoreCase);
        bool uninstall = args.Contains("/Uninstall", StringComparer.OrdinalIgnoreCase);

        // Copia de sí mismo como desinstalador: si el ejecutable se llama
        // UninstallAlarmino, el modo correcto es desinstalar aunque no lleve /Uninstall.
        string ownName = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? string.Empty);
        uninstall |= ownName.Equals("UninstallAlarmino", StringComparison.OrdinalIgnoreCase);

        if (silent)
        {
            string? dir = TryGetArgValue(args, "/D");
            if (uninstall)
            {
                string? installed = InstallerEngine.GetInstalledLocation()
                    ?? InstallerEngine.DefaultInstallDir;
                InstallerEngine.Uninstall(installed);
            }
            else
            {
                InstallerEngine.Install(
                    dir ?? InstallerEngine.DefaultInstallDir,
                    autoStart: true,
                    desktopShortcut: true);
            }

            return 0;
        }

        var window = new InstallerWindow(args);
        app.Run(window);
        return 0;
    }

    private static string? TryGetArgValue(string[] args, string key)
    {
        foreach (var arg in args)
        {
            if (arg.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
            {
                return arg[(key.Length + 1)..].Trim('"');
            }
        }

        return null;
    }
}
