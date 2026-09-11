using System.Collections;
using System.Windows;
using Alarmino.Core.Models;

namespace Alarmino.Services;

/// <summary>
/// Cambia el tema (claro/oscuro) en vivo. Inyecta los pinceles del tema activo
/// directamente en <see cref="Application.Resources"/> para que cualquier
/// DynamicResource resuelva siempre los colores del tema vigente.
/// </summary>
public sealed class ThemeService
{
    public void Apply(ThemeMode mode)
    {
        var app = Application.Current;
        string source = $"Theme/{(mode == ThemeMode.Dark ? "Dark" : "Light")}.xaml";

        var dictionary = new ResourceDictionary
        {
            Source = new Uri(source, UriKind.Relative),
        };

        // 1) Quita diccionarios de tema antiguos de la cadena mergeada (App.xaml).
        var merged = app.Resources.MergedDictionaries;
        for (int i = merged.Count - 1; i >= 0; i--)
        {
            var d = merged[i];
            if (d.Source is null)
            {
                continue;
            }

            string s = d.Source.OriginalString;
            if (s.EndsWith("/Dark.xaml", StringComparison.Ordinal) ||
                s.EndsWith("/Light.xaml", StringComparison.Ordinal))
            {
                merged.RemoveAt(i);
            }
        }

        // 2) Publica los pinceles en Resources: máxima precedencia y actualización
        //    fiable de DynamicResource (evita texto «pegado» al tema anterior).
        foreach (DictionaryEntry entry in dictionary)
        {
            if (entry.Key is string key)
            {
                app.Resources[key] = entry.Value;
            }
        }
    }
}
