namespace Alarmino.Core.Models;

/// <summary>Metadatos de un sonido predeterminado.</summary>
public sealed record SoundPreset(string Id, string DisplayName);

/// <summary>
/// Cambio del usuario sobre un preset: nombre personalizado y, opcionalmente,
/// un archivo de audio que lo sustituye (si no, se sigue sintetizando).
/// </summary>
public class PresetOverride
{
    public string PresetId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string? FilePath { get; set; }
}

/// <summary>Biblioteca de los tres sonidos predeterminados que incluye la app.</summary>
public static class SoundPresets
{
    /// <summary>
    /// Ids de las versiones anteriores (campana, timbre, tono escolar) y su
    /// equivalente actual, para no perder la elección del usuario.
    /// </summary>
    private static readonly Dictionary<string, string> LegacyIds = new(StringComparer.Ordinal)
    {
        ["bell"] = "airplaneBeep",
        ["ring"] = "softChime",
        ["schoolTone"] = "fireAlarm",
    };

    public static readonly IReadOnlyList<SoundPreset> All =
    [
        new("airplaneBeep", "Bip avión"),
        new("softChime", "Suave"),
        new("fireAlarm", "Incendio"),
    ];

    public static bool IsValidPreset(string? id) => All.Any(p => p.Id == id);

    /// <summary>Convierte un id antiguo al actual; devuelve el mismo si ya es válido.</summary>
    public static string? MigratePresetId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return id;
        }

        return LegacyIds.TryGetValue(id, out string? current) ? current : id;
    }

    /// <summary>Presets visibles aplicando los cambios del usuario (nombre personalizado).</summary>
    public static IReadOnlyList<SoundPreset> GetAll(IReadOnlyList<PresetOverride>? overrides = null)
    {
        if (overrides is null || overrides.Count == 0)
        {
            return All;
        }

        return All
            .Select(builtin =>
            {
                var ov = overrides.FirstOrDefault(o => o.PresetId == builtin.Id);
                return ov is not null ? new SoundPreset(builtin.Id, ov.DisplayName) : builtin;
            })
            .ToList();
    }

    /// <summary>Archivo que sustituye a un preset, si el usuario lo ha elegido.</summary>
    public static string? ResolveFilePath(string? presetId, IReadOnlyList<PresetOverride>? overrides)
    {
        if (string.IsNullOrWhiteSpace(presetId) || overrides is null)
        {
            return null;
        }

        var ov = overrides.FirstOrDefault(o => o.PresetId == presetId);
        return string.IsNullOrWhiteSpace(ov?.FilePath) ? null : ov.FilePath;
    }
}
