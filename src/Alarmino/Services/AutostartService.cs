using System.Diagnostics;
using Alarmino.Core.Services;
using Microsoft.Win32;

namespace Alarmino.Services;

/// <summary>
/// Gestiona la autoejecución de Alarmino al iniciar Windows (HKCU Run).
/// Con <paramref name="startVisible"/> arranca mostrando la ventana; si no, arranca minimizado a la bandeja (/min).
/// </summary>
public static class AutostartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Alarmino";

    public static void SetEnabled(bool enabled, bool startVisible = false)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key is null)
            {
                return;
            }

            if (enabled)
            {
                string path = Process.GetCurrentProcess().MainModule?.FileName ?? Environment.ProcessPath ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(path))
                {
                    string value = startVisible ? $"\"{path}\"" : $"\"{path}\" /min";
                    key.SetValue(ValueName, value);
                }
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            JsonDataStore.AppendLogLine(null, $"Error configurando autostart: {ex.Message}");
        }
    }

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is string value &&
                value.Contains("Alarmino", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
