using System.Windows;
using Alarmino.Core.Models;

namespace Alarmino.Services;

/// <summary>Cambia el diccionario de recursos de tema (claro/oscuro) en vivo.</summary>
public sealed class ThemeService
{
    public void Apply(ThemeMode mode)
    {
        var app = Application.Current;
        var merged = app.Resources.MergedDictionaries;

        var existing = merged.FirstOrDefault(d =>
            d.Source != null &&
            d.Source.OriginalString.Contains("Theme/", StringComparison.Ordinal));

        string source = $"Theme/{(mode == ThemeMode.Dark ? "Dark" : "Light")}.xaml";

        if (existing is null)
        {
            merged.Insert(0, new ResourceDictionary { Source = new Uri(source, UriKind.Relative) });
            return;
        }

        int index = merged.IndexOf(existing);
        merged.RemoveAt(index);
        merged.Insert(index, new ResourceDictionary { Source = new Uri(source, UriKind.Relative) });
    }
}
