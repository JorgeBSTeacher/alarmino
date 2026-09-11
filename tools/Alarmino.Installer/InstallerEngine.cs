using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace Alarmino.Installer;

public static class InstallerEngine
{
    private const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Alarmino";

    public static string DefaultInstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Alarmino");

    public static void Install(string targetDir, bool autoStart, bool desktopShortcut)
    {
        SelfExtractor.ExtractTo(targetDir);
        ShortcutHelper.CreateStartMenuShortcut(targetDir);

        if (desktopShortcut)
        {
            ShortcutHelper.CreateDesktopShortcut(targetDir);
        }

        if (autoStart)
        {
            ShortcutHelper.SetAutoStart(true, Path.Combine(targetDir, "Alarmino.exe"));
        }

        ShortcutHelper.RegisterUninstall(targetDir);

        // Copia de sí mismo como desinstalador dentro de la carpeta instalada.
        File.Copy(Environment.ProcessPath!, Path.Combine(targetDir, "UninstallAlarmino.exe"), overwrite: true);
    }

    /// <param name="deleteData">Si es true, borra también %APPDATA%\Alarmino (alarmas, configuración y audios).</param>
    public static void Uninstall(string installDir, bool deleteData = false)
    {
        ShortcutHelper.RemoveShortcuts();
        ShortcutHelper.RemoveAutoStartIfPointsTo(installDir);
        ShortcutHelper.RemoveUninstallEntry();

        // Un instalador no puede borrarse a sí mismo (fichero en uso):
        // se copia a temp, se arranca y termina; el ayudante borra la carpeta.
        string helper = Path.Combine(Path.GetTempPath(), "AlarminoUninstallHelper.exe");
        File.Copy(Environment.ProcessPath!, helper, overwrite: true);

        var psi = new ProcessStartInfo(helper, $"--delete-dir \"{installDir}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using (Process.Start(psi))
        {
        }

        if (deleteData)
        {
            // Datos de la app fuera de la carpeta de instalación.
            string dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Alarmino");
            try
            {
                if (Directory.Exists(dataDir))
                {
                    Directory.Delete(dataDir, recursive: true);
                }
            }
            catch
            {
                // Si Alarmino sigue en ejecución, el borrado se intenta en el
                // siguiente reinicio manual (no bloqueamos la desinstalación).
            }
        }
    }

    public static string? GetInstalledLocation()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKeyPath);
            return key?.GetValue("InstallLocation") as string;
        }
        catch
        {
            return null;
        }
    }
}
