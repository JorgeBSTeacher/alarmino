namespace Alarmino.Core.Models;

public enum PlaybackMode
{
    /// <summary>Reproduce el sonido completo de principio a fin.</summary>
    Full,

    /// <summary>Reproduce durante un número de segundos configurable.</summary>
    CustomDuration,
}

public enum SoundSourceKind
{
    /// <summary>Sonido predeterminado de la biblioteca (sintetizado).</summary>
    Preset,

    /// <summary>Archivo de audio local (.mp3 / .wav).</summary>
    File,
}

/// <summary>Selección de sonido de una alarma: preset o archivo personalizado.</summary>
public class SoundSelection
{
    public SoundSourceKind Kind { get; set; } = SoundSourceKind.Preset;

    public string? PresetId { get; set; }

    public string? FilePath { get; set; }

    public string DisplayText
    {
        get
        {
            if (Kind == SoundSourceKind.File && !string.IsNullOrWhiteSpace(FilePath))
            {
                return System.IO.Path.GetFileName(FilePath);
            }

            if (Kind == SoundSourceKind.Preset)
            {
                var preset = SoundPresets.All.FirstOrDefault(p => p.Id == PresetId);
                return preset?.DisplayName ?? "Sonido predeterminado";
            }

            return "Sin sonido";
        }
    }
}

public class Alarm
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Alias o nombre de la alarma (obligatorio). Ej.: «Recreo».</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Hora del día (HH:mm).</summary>
    public TimeSpan Time { get; set; }

    public DayFlags Days { get; set; } = DayFlags.Weekdays;

    public SoundSelection Sound { get; set; } = new();

    public PlaybackMode Mode { get; set; } = PlaybackMode.Full;

    public int CustomDurationSeconds { get; set; } = 15;

    /// <summary>Número de toques seguidos (con pausa entre ellos). Entre 1 y 20.</summary>
    public int RepeatCount { get; set; } = 1;

    public int PauseBetweenRepeatSeconds { get; set; } = 1;

    public bool IsActive { get; set; } = true;

    public string TimeText => Time.ToString(@"hh\:mm");
}
