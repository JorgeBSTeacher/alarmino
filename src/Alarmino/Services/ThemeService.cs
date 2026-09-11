using System.Collections;
using System.Windows;
using Alarmino.Core.Models;

namespace Alarmino.Services;

/// <summary>
/// Cambia el tema (claro/oscuro) en vivo con doble mecanismo:
/// 1) Reemplaza el diccionario de tema en las MergedDictionaries (App.xaml).
/// 2) Re-publica los pinceles en <see cref="Application.Resources"/>.
/// Así cualquier DynamicResource resuelve siempre los colores del tema vigente.
/// </summary>
public sealed class ThemeService
{
    public void Apply(ThemeMode mode)
    {
        var app = Application.Current;
        string themeName = mode == ThemeMode.Dark ? "Dark" : "Light";

        var dictionary = new ResourceDictionary
        {
            Source = new Uri($"Theme/{themeName}.xaml", UriKind.Relative),
        };

        // 1) Cambia el diccionario de tema en la cadena mergeada (App.xaml).
        var merged = app.Resources.MergedDictionaries;
        int existing = -1;
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
                existing = i;
                break;
            }
        }

        if (existing >= 0)
        {
            merged[existing] = dictionary;
        }
        else
        {
            merged.Insert(0, dictionary);
        }

        // 2) Re-publica los pinceles en Resources: máxima precedencia y
        //    actualización fiable de DynamicResource.
        foreach (System.Collections.DictionaryEntry entry in dictionary)
        {
            if (entry.Key is string key)
            {
                app.Resources[key] = entry.Value;
            }
        }
    }
}
