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

    /// <summary>Cuántas veces seguidas suena el botón ALARMA.</summary>
    public int AlarmRepeatCount { get; set; } = 3;

    /// <summary>Pausa (segundos) entre toques del botón ALARMA.</summary>
    public int AlarmPauseSeconds { get; set; } = 1;

    /// <summary>Sonido del botón LLUVIA de la ventana principal.</summary>
    public SoundSelection RainSound { get; set; } = new()
    {
        Kind = SoundSourceKind.Preset,
        PresetId = "ring",
    };

    /// <summary>Cuántas veces seguidas suena el botón LLUVIA.</summary>
    public int RainRepeatCount { get; set; } = 1;

    /// <summary>Cambios del usuario sobre los sonidos predeterminados (nombre/archivo).</summary>
    public List<PresetOverride> PresetOverrides { get; set; } = [];

    public AlarmSortMode SortMode { get; set; } = AlarmSortMode.ByTime;
}
