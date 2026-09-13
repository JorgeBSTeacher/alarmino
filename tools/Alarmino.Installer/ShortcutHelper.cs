using System;
using System.IO;
using Microsoft.Win32;

namespace Alarmino.Installer;

/// <summary>Atajos (.lnk vía WScript.Shell COM), autostart y clave de desinstalación (HKCU).</summary>
public static class ShortcutHelper
{
    private static readonly Guid WshShellClassId = new("72C24DD5-D70A-438B-8A42-98424B88AFB8");

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Alarmino";

    public static void CreateStartMenuShortcut(string installDir)
    {
        var startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Alarmino");
        Directory.CreateDirectory(startMenu);
        CreateLnk(Path.Combine(startMenu, "Alarmino.lnk"), installDir);
    }

    public static void CreateDesktopShortcut(string installDir)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        if (string.IsNullOrWhiteSpace(desktop))
        {
            return;
        }

        CreateLnk(Path.Combine(desktop, "Alarmino.lnk"), installDir);
    }

    public static void RemoveShortcuts()
    {
        try
        {
            var startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Alarmino");
            if (Directory.Exists(startMenu))
            {
                Directory.Delete(startMenu, recursive: true);
            }
        }
        catch { /* ignorar */ }

        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            if (!string.IsNullOrWhiteSpace(desktop))
            {
                var path = Path.Combine(desktop, "Alarmino.lnk");
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
        catch { /* ignorar */ }
    }

    public static void SetAutoStart(bool enabled, string exePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (key is null)
        {
            return;
        }

        if (enabled)
        {
            key.SetValue("Alarmino", $"\"{exePath}\" /min");
        }
        else
        {
            key.DeleteValue("Alarmino", throwOnMissingValue: false);
        }
    }

    public static bool RemoveAutoStartIfPointsTo(string installDir)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key?.GetValue("Alarmino") is string value && value.Contains(installDir, StringComparison.OrdinalIgnoreCase))
            {
                key.DeleteValue("Alarmino", throwOnMissingValue: false);
                return true;
            }
        }
        catch { /* ignorar */ }

        return false;
    }

    public static void RegisterUninstall(string installDir)
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKeyPath);
        if (key is null)
        {
            return;
        }

        string exe = Path.Combine(installDir, "Alarmino.exe");
        key.SetValue("DisplayName", "Alarmino");
        key.SetValue("DisplayVersion", "0.1.2");
        key.SetValue("DisplayIcon", exe);
        key.SetValue("Publisher", "JorgeBSTeacher");
        key.SetValue("InstallLocation", installDir);
        key.SetValue("UninstallString", $"\"{Path.Combine(installDir, "UninstallAlarmino.exe")}\" /Uninstall");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("EstimatedSize", 200000, RegistryValueKind.DWord);
    }

    public static void RemoveUninstallEntry() =>
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKeyPath, throwOnMissingSubKey: false);

    private static void CreateLnk(string lnkPath, string installDir)
    {
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromCLSID(WshShellClassId)!)!;
            dynamic lnk = shell.CreateShortcut(lnkPath);
            string target = Path.Combine(installDir, "Alarmino.exe");
            lnk.TargetPath = target;
            lnk.WorkingDirectory = installDir;
            lnk.IconLocation = target + ",0";
            lnk.Description = "Alarmino — Sirenas del colegio";
            lnk.Save();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"No se pudo crear el acceso directo:\n{ex.Message}",
                "Alarmino", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }
}
