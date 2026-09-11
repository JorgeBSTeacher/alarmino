namespace Alarmino.Core.Models;

public enum ThemeMode
{
    Light,
    Dark,
}

public enum AlarmSortMode
{
    ByTime,
    ByDay,
}

public class AppSettings
{
    public int GlobalVolumePercent { get; set; } = 80;

    public ThemeMode Theme { get; set; } = ThemeMode.Dark;

    public bool AutoStartEnabled { get; set; } = true;

    /// <summary>Al autoejecutarse al iniciar sesión, mostrar la ventana (en vez de minimizar a la bandeja).</summary>
    public bool StartVisibleOnLogin { get; set; }

    public bool NotificationSoundEnabled { get; set; } = true;

    /// <summary>Sonido del botón ALARMA (rojo) de la ventana principal.</summary>
    public SoundSelection AlarmSound { get; set; } = new()
    {
        Kind = SoundSourceKind.Preset,
        PresetId = "schoolTone",
    };

    public AlarmSortMode SortMode { get; set; } = AlarmSortMode.ByTime;
}
