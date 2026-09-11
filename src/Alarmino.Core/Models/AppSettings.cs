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

    public AlarmSortMode SortMode { get; set; } = AlarmSortMode.ByTime;
}
