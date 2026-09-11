using System.Diagnostics;
using Alarmino.Core.Services;
using Microsoft.Win32;

namespace Alarmino.Services;

/// <summary>Gestiona la autoejecución de Alarmino al iniciar Windows (HKCU Run, con /min).</summary>
public static class AutostartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Alarmino";

    public static void SetEnabled(bool enabled)
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
                    key.SetValue(ValueName, $"\"{path}\" /min");
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
            return key?.GetValue(ValueName) is string value && value.Contains("/min", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }
}
