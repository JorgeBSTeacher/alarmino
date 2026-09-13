using Alarmino.Core.Models;
using Alarmino.Core.Services;

namespace Alarmino.Tests;

public class DayFlagsTests
{
    [Fact]
    public void Weekdays_excludes_weekend()
    {
        Assert.True(DayFlags.Weekdays.Matches(DayOfWeek.Monday));
        Assert.True(DayFlags.Weekdays.Matches(DayOfWeek.Friday));
        Assert.False(DayFlags.Weekdays.Matches(DayOfWeek.Saturday));
        Assert.False(DayFlags.Weekdays.Matches(DayOfWeek.Sunday));
    }

    [Fact]
    public void EveryDay_matches_all()
    {
        foreach (var day in Enum.GetValues<DayOfWeek>())
        {
            Assert.True(DayFlags.EveryDay.Matches(day));
        }
    }

    [Fact]
    public void ToLetters_non_marked_are_dots()
    {
        var letters = DayFlags.Monday.ToLetters();
        Assert.Equal('L', letters[0]);
        Assert.All(letters.Skip(1), c => Assert.Equal('·', c));
    }

    [Fact]
    public void ToDisplayText_full_week()
    {
        Assert.Equal("Cada día", DayFlags.EveryDay.ToDisplayText());
        Assert.Equal("De lunes a viernes", DayFlags.Weekdays.ToDisplayText());
    }
}

public class JsonDataStoreTests
{
    [Fact]
    public void RoundTrips_alarms_settings_and_events()
    {
        var dir = Path.Combine(Path.GetTempPath(), "alarmino-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new JsonDataStore(dir);

            var alarm = new Alarm
            {
                Name = "Recreo",
                Time = new TimeSpan(11, 30, 0),
                Days = DayFlags.Weekdays,
                Mode = PlaybackMode.CustomDuration,
                CustomDurationSeconds = 20,
                RepeatCount = 3,
                PauseBetweenRepeatSeconds = 2,
                IsActive = false,
                Sound = new SoundSelection { Kind = SoundSourceKind.Preset, PresetId = "bell" },
            };
            store.SaveAlarms([alarm]);
            store.SaveSettings(new AppSettings { GlobalVolumePercent = 55, Theme = ThemeMode.Light, SortMode = AlarmSortMode.ByDay });
            store.SaveEvents([new EventLogEntry { UtcTime = DateTime.UtcNow, AlarmId = alarm.Id, AlarmName = alarm.Name, State = EventState.Played }]);

            var alarms = store.LoadAlarms();
            var settings = store.LoadSettings();
            var events = store.LoadEvents();

            var loaded = Assert.Single(alarms);
            Assert.Equal(alarm.Id, loaded.Id);
            Assert.Equal("Recreo", loaded.Name);
            Assert.Equal(new TimeSpan(11, 30, 0), loaded.Time);
            Assert.Equal(DayFlags.Weekdays, loaded.Days);
            Assert.Equal(PlaybackMode.CustomDuration, loaded.Mode);
            Assert.Equal(20, loaded.CustomDurationSeconds);
            Assert.Equal(3, loaded.RepeatCount);
            Assert.False(loaded.IsActive);
            Assert.Equal(SoundSourceKind.Preset, loaded.Sound.Kind);
            Assert.Equal("bell", loaded.Sound.PresetId);

            Assert.Equal(55, settings.GlobalVolumePercent);
            Assert.Equal(ThemeMode.Light, settings.Theme);
            Assert.Equal(AlarmSortMode.ByDay, settings.SortMode);

            var entry = Assert.Single(events);
            Assert.Equal(EventState.Played, entry.State);
            Assert.Equal(alarm.Id, entry.AlarmId);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void Missing_files_return_defaults()
    {
        var dir = Path.Combine(Path.GetTempPath(), "alarmino-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new JsonDataStore(dir);
            Assert.Empty(store.LoadAlarms());
            Assert.NotNull(store.LoadSettings());
            Assert.Empty(store.LoadEvents());
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}

public class SoundPresetsTests
{
    [Fact]
    public void GetAll_returns_builtins_when_no_overrides()
    {
        Assert.Equal(SoundPresets.All, SoundPresets.GetAll(null));
        Assert.Equal(SoundPresets.All, SoundPresets.GetAll([]));
    }

    [Fact]
    public void GetAll_applies_custom_display_names()
    {
        var overrides = new List<PresetOverride>
        {
            new() { PresetId = "bell", DisplayName = "Campanón" },
            new() { PresetId = "schoolTone", DisplayName = "Tono suave", FilePath = @"C:\x\tono.mp3" },
        };

        var result = SoundPresets.GetAll(overrides);

        Assert.Equal(3, result.Count);
        Assert.Equal("Campanón", result[0].DisplayName);
        Assert.Equal("Timbre", result[1].DisplayName); // sin cambios
        Assert.Equal("Tono suave", result[2].DisplayName);
        Assert.Equal("bell", result[0].Id);
    }

    [Fact]
    public void ResolveFilePath_returns_file_for_overridden_preset()
    {
        var overrides = new List<PresetOverride>
        {
            new() { PresetId = "bell", DisplayName = "Campana", FilePath = @"C:\x\campana.mp3" },
            new() { PresetId = "ring", DisplayName = "Timbre" },
        };

        Assert.Equal(@"C:\x\campana.mp3", SoundPresets.ResolveFilePath("bell", overrides));
        Assert.Null(SoundPresets.ResolveFilePath("ring", overrides));  // solo nombre, sin archivo
        Assert.Null(SoundPresets.ResolveFilePath("schoolTone", overrides));
        Assert.Null(SoundPresets.ResolveFilePath(null, overrides));
    }

    [Fact]
    public void AppSettings_defaults_include_three_repeats_and_rain_sound()
    {
        var settings = new AppSettings();

        Assert.Equal(3, settings.AlarmRepeatCount);
        Assert.Equal(1, settings.AlarmPauseSeconds);
        Assert.Equal(SoundSourceKind.Preset, settings.RainSound.Kind);
        Assert.NotNull(settings.RainSound.PresetId);
        Assert.Equal(1, settings.RainRepeatCount);
        Assert.Empty(settings.PresetOverrides);
    }
}
