namespace Alarmino.Core.Models;

/// <summary>Metadatos de un sonido predeterminado.</summary>
public sealed record SoundPreset(string Id, string DisplayName);

/// <summary>Biblioteca de sonidos predeterminados (sintetizados en código, sin copyright).</summary>
public static class SoundPresets
{
    public static readonly IReadOnlyList<SoundPreset> All =
    [
        new("bell", "Campana"),
        new("ring", "Timbre"),
        new("schoolTone", "Tono escolar"),
    ];

    public static bool IsValidPreset(string? id) => All.Any(p => p.Id == id);
}
