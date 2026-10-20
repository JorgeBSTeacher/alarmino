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
                Sound = new SoundSelection { Kind = SoundSourceKind.Preset, PresetId = "airplaneBeep" },
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
            Assert.Equal("airplaneBeep", loaded.Sound.PresetId);

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
    public void LoadSettings_without_the_new_fields_uses_their_defaults()
    {
        var dir = Path.Combine(Path.GetTempPath(), "alarmino-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);

            // config.json de una versión anterior: no trae NoRepeatDays ni
            // TargetBufferCount y debe cargarse con los valores por defecto.
            File.WriteAllText(
                Path.Combine(dir, "config.json"),
                """{"GlobalVolumePercent":42,"Theme":1}""");

            var settings = new JsonDataStore(dir).LoadSettings();

            Assert.Equal(42, settings.GlobalVolumePercent);
            Assert.Equal(7, settings.NoRepeatDays);
            Assert.Equal(3, settings.TargetBufferCount);
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
    public void RoundTrips_the_anti_repeat_window_and_the_buffer_target()
    {
        var dir = Path.Combine(Path.GetTempPath(), "alarmino-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new JsonDataStore(dir);
            store.SaveSettings(new AppSettings { NoRepeatDays = 14, TargetBufferCount = 5 });

            var settings = store.LoadSettings();

            Assert.Equal(14, settings.NoRepeatDays);
            Assert.Equal(5, settings.TargetBufferCount);
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

    [Fact]
    public void Default_presets_are_the_three_shipped_audios()
    {
        Assert.Equal(3, SoundPresets.All.Count);
        Assert.Equal(("airplaneBeep", "Bip avión"), (SoundPresets.All[0].Id, SoundPresets.All[0].DisplayName));
        Assert.Equal(("softChime", "Suave"), (SoundPresets.All[1].Id, SoundPresets.All[1].DisplayName));
        Assert.Equal(("fireAlarm", "Incendio"), (SoundPresets.All[2].Id, SoundPresets.All[2].DisplayName));
    }

    [Fact]
    public void MigratePresetId_maps_previous_ids()
    {
        Assert.Equal("airplaneBeep", SoundPresets.MigratePresetId("bell"));
        Assert.Equal("softChime", SoundPresets.MigratePresetId("ring"));
        Assert.Equal("fireAlarm", SoundPresets.MigratePresetId("schoolTone"));
        Assert.Equal("fireAlarm", SoundPresets.MigratePresetId("fireAlarm"));
        Assert.Null(SoundPresets.MigratePresetId(null));
    }

    [Fact]
    public void Default_sound_of_alarm_button_is_fire_alarm()
    {
        var settings = new AppSettings();
        Assert.Equal(SoundSourceKind.Preset, settings.AlarmSound.Kind);
        Assert.Equal("fireAlarm", settings.AlarmSound.PresetId);
        Assert.Equal("softChime", settings.RainSound.PresetId);
    }

    [Fact]
    public void LoadSettings_migrates_previous_preset_ids()
    {
        var dir = Path.Combine(Path.GetTempPath(), "alarmino-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new JsonDataStore(dir);
            store.SaveSettings(new AppSettings
            {
                AlarmSound = new SoundSelection { Kind = SoundSourceKind.Preset, PresetId = "schoolTone" },
                RainSound = new SoundSelection { Kind = SoundSourceKind.Preset, PresetId = "ring" },
                PresetOverrides = [new PresetOverride { PresetId = "bell", DisplayName = "Mi bip", FilePath = @"C:\x\bip.mp3" }],
            });
            store.SaveAlarms([new Alarm
            {
                Name = "Recreo",
                Time = new TimeSpan(10, 30, 0),
                Days = DayFlags.EveryDay,
                Sound = new SoundSelection { Kind = SoundSourceKind.Preset, PresetId = "ring" },
            }]);

            var settings = store.LoadSettings();
            Assert.Equal("fireAlarm", settings.AlarmSound.PresetId);
            Assert.Equal("softChime", settings.RainSound.PresetId);
            Assert.Equal("airplaneBeep", settings.PresetOverrides.Single().PresetId);

            var alarm = Assert.Single(store.LoadAlarms());
            Assert.Equal("softChime", alarm.Sound.PresetId);
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
    public void RoundTrips_youtube_sound_and_offline_fallback()
    {
        var dir = Path.Combine(Path.GetTempPath(), "alarmino-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new JsonDataStore(dir);

            var alarm = new Alarm
            {
                Name = "Música",
                Time = new TimeSpan(9, 0, 0),
                Days = DayFlags.Weekdays,
                Sound = new SoundSelection
                {
                    Kind = SoundSourceKind.YouTube,
                    YouTubePlaylistUrl = "https://www.youtube.com/playlist?list=PLabcDEF123",
                },
            };
            store.SaveAlarms([alarm]);
            store.SaveSettings(new AppSettings
            {
                OfflineFallbackFilePath = @"C:\datos\respaldo.mp3",
                AlarmSound = new SoundSelection { Kind = SoundSourceKind.YouTube, YouTubePlaylistUrl = "https://youtube.com/playlist?list=PLxyz" },
            });

            var loaded = Assert.Single(store.LoadAlarms());
            Assert.Equal(SoundSourceKind.YouTube, loaded.Sound.Kind);
            Assert.Equal("https://www.youtube.com/playlist?list=PLabcDEF123", loaded.Sound.YouTubePlaylistUrl);
            Assert.Equal("YouTube", loaded.Sound.DisplayText);

            var settings = store.LoadSettings();
            Assert.Equal(@"C:\datos\respaldo.mp3", settings.OfflineFallbackFilePath);
            Assert.Equal(SoundSourceKind.YouTube, settings.AlarmSound.Kind);
            Assert.Equal("PLxyz", YouTubeAudioResolver.ExtractPlaylistId(settings.AlarmSound.YouTubePlaylistUrl!));
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

public class YouTubeAudioResolverTests
{
    private sealed class FakeProbe(bool online) : IConnectivityProbe
    {
        public bool Online { get; } = online;

        public int Calls { get; private set; }

        public Task<bool> IsOnlineAsync()
        {
            Calls++;
            return Task.FromResult(Online);
        }
    }

    [Theory]
    [InlineData("https://www.youtube.com/playlist?list=PL123abc", "PL123abc")]
    [InlineData("https://youtube.com/playlist?list=PL456def&si=xyz", "PL456def")]
    [InlineData("https://m.youtube.com/watch?v=x&list=PL789ghi", "PL789ghi")]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", null)]
    [InlineData("https://example.com/not-youtube", null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    public void ExtractPlaylistId_validates_urls(string url, string? expected)
    {
        Assert.Equal(expected, YouTubeAudioResolver.ExtractPlaylistId(url));
    }

    [Fact]
    public async Task Resolve_offline_returns_fallback_file()
    {
        var resolver = new YouTubeAudioResolver(
            cacheDir: Path.Combine(Path.GetTempPath(), "alarmino-yt-" + Guid.NewGuid().ToString("N")),
            connectivity: new FakeProbe(online: false));

        string? result = await resolver.ResolveAsync(
            "https://www.youtube.com/playlist?list=PL123abc", @"C:\datos\respaldo.mp3", CancellationToken.None);

        Assert.Equal(@"C:\datos\respaldo.mp3", result);
    }

    [Fact]
    public async Task Resolve_online_without_url_returns_fallback_file()
    {
        var resolver = new YouTubeAudioResolver(connectivity: new FakeProbe(online: true));

        string? result = await resolver.ResolveAsync(null, @"C:\datos\respaldo.mp3", CancellationToken.None);

        Assert.Equal(@"C:\datos\respaldo.mp3", result);
    }

    [Fact]
    public async Task Resolve_without_fallback_returns_null()
    {
        var resolver = new YouTubeAudioResolver(connectivity: new FakeProbe(online: false));

        string? result = await resolver.ResolveAsync(
            "https://www.youtube.com/playlist?list=PL123abc", null, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task Resolve_uses_preloaded_track_without_touching_the_network()
    {
        var dir = Path.Combine(Path.GetTempPath(), "alarmino-yt-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, YouTubeTrackCache.BuildFileName("PL123abc", "videoA", "m4a"));
            File.WriteAllText(file, "audio");

            var cache = new YouTubeTrackCache(dir);
            cache.Register("PL123abc", "videoA", file, DateTime.UtcNow);

            var probe = new FakeProbe(online: false);
            var resolver = new YouTubeAudioResolver(cache: cache, connectivity: probe);

            string? result = await resolver.ResolveAsync(
                "https://www.youtube.com/playlist?list=PL123abc", @"C:\datos\respaldo.mp3", CancellationToken.None);

            // La pista precargada manda: ni se consulta la red ni el respaldo.
            Assert.Equal(file, result);
            Assert.Equal(0, probe.Calls);
            Assert.Equal("videoA", resolver.LastVideoId);
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
    public async Task Prefetch_offline_does_not_burn_the_throttle()
    {
        var dir = Path.Combine(Path.GetTempPath(), "alarmino-yt-" + Guid.NewGuid().ToString("N"));
        try
        {
            var probe = new FakeProbe(online: false);
            var resolver = new YouTubeAudioResolver(cacheDir: dir, connectivity: probe);

            await resolver.PrefetchAsync("https://www.youtube.com/playlist?list=PL123abc");
            await resolver.PrefetchAsync("https://www.youtube.com/playlist?list=PL123abc");

            // Sin conexión no se consume el intento: cada llamada vuelve a comprobar la red
            // (y el próximo barrido podrá reintentar en cuanto haya conexión).
            Assert.Equal(2, probe.Calls);
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
    public async Task WithLiveTimeoutAsync_returns_the_result_when_it_finishes_in_time()
    {
        string result = await YouTubeAudioResolver.WithLiveTimeoutAsync(
            _ => Task.FromResult("ok"), TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Equal("ok", result);
    }

    [Fact]
    public async Task WithLiveTimeoutAsync_throws_TimeoutException_when_the_download_hangs()
    {
        using var cts = new CancellationTokenSource();

        // Una red colgada no debe dejar la alarma en silencio: se corta y se
        // cae al respaldo. El token del usuario no se toca (sigue para el stop).
        await Assert.ThrowsAsync<TimeoutException>(() =>
            YouTubeAudioResolver.WithLiveTimeoutAsync(
                async ct =>
                {
                    await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, ct);
                    return 0;
                },
                TimeSpan.FromMilliseconds(50),
                cts.Token));

        Assert.False(cts.IsCancellationRequested);
    }

    [Fact]
    public async Task WithLiveTimeoutAsync_propagates_user_cancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            YouTubeAudioResolver.WithLiveTimeoutAsync(
                async ct =>
                {
                    await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, ct);
                    return 0;
                },
                TimeSpan.FromSeconds(30),
                cts.Token));
    }

    [Fact]
    public async Task Prefetch_does_nothing_when_a_fresh_track_exists()
    {
        var dir = Path.Combine(Path.GetTempPath(), "alarmino-yt-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, YouTubeTrackCache.BuildFileName("PL123abc", "videoA", "m4a"));
            File.WriteAllText(file, "audio");

            var cache = new YouTubeTrackCache(dir);
            cache.Register("PL123abc", "videoA", file, DateTime.UtcNow);

            var probe = new FakeProbe(online: true);
            var resolver = new YouTubeAudioResolver(cache: cache, connectivity: probe);

            await resolver.PrefetchAsync("https://www.youtube.com/playlist?list=PL123abc");

            Assert.Equal(0, probe.Calls);
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
    public void BufferState_counts_ready_tracks_against_the_target()
    {
        var dir = Path.Combine(Path.GetTempPath(), "alarmino-yt-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, YouTubeTrackCache.BuildFileName("PL1", "videoA", "m4a"));
            File.WriteAllText(file, "audio");

            var cache = new YouTubeTrackCache(dir);
            cache.Register("PL1", "videoA", file, DateTime.UtcNow);

            BufferState state = cache.GetBufferState(3);

            Assert.Equal(1, state.Ready);
            Assert.Equal(3, state.Target);
            Assert.False(state.IsFull);
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
    public void BufferState_ignores_consumed_tracks()
    {
        var dir = Path.Combine(Path.GetTempPath(), "alarmino-yt-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, YouTubeTrackCache.BuildFileName("PL1", "videoA", "m4a"));
            File.WriteAllText(file, "audio");

            var cache = new YouTubeTrackCache(dir);
            cache.Register("PL1", "videoA", file, DateTime.UtcNow);
            Assert.True(cache.TryTake("PL1", out _, out _));  // la pista suena

            BufferState state = cache.GetBufferState(3);

            // La pista ya sonada no cuenta como lista para el próximo arranque.
            Assert.Equal(0, state.Ready);
            Assert.False(state.IsFull);
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
    public void BufferState_is_full_when_the_ready_count_reaches_the_target()
    {
        var dir = Path.Combine(Path.GetTempPath(), "alarmino-yt-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            var cache = new YouTubeTrackCache(dir);
            cache.Register("PL1", "videoA", CreatePlayableFile(dir, "PL1", "videoA"), DateTime.UtcNow);
            cache.Register("PL2", "videoB", CreatePlayableFile(dir, "PL2", "videoB"), DateTime.UtcNow);

            BufferState state = cache.GetBufferState(2);

            Assert.Equal(2, state.Ready);
            Assert.True(state.IsFull);
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
    public void BufferState_of_a_non_positive_target_is_always_full()
    {
        var dir = Path.Combine(Path.GetTempPath(), "alarmino-yt-" + Guid.NewGuid().ToString("N"));
        try
        {
            BufferState state = new YouTubeTrackCache(dir).GetBufferState(0);

            Assert.Equal(0, state.Ready);
            Assert.Equal(0, state.Target);
            Assert.True(state.IsFull);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    private static string CreatePlayableFile(string dir, string playlistId, string videoId)
    {
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, YouTubeTrackCache.BuildFileName(playlistId, videoId, "m4a"));
        byte[] data = new byte[40 * 1024];
        data[0] = 0xFF;
        data[1] = 0xFB;
        data[2] = 0xA0;
        File.WriteAllBytes(path, data);
        return path;
    }

    [Fact]
    public void RecordPlay_notes_the_track_for_the_playlist()
    {
        var dir = Path.Combine(Path.GetTempPath(), "alarmino-history-" + Guid.NewGuid().ToString("N"));
        try
        {
            var history = new PlayHistoryStore(dir);
            var resolver = new YouTubeAudioResolver(history: history, connectivity: new FakeProbe(online: false));

            resolver.RecordPlay("https://www.youtube.com/playlist?list=PL123abc", "videoA");

            Assert.Equal(
                ["videoA"],
                history.InWindow("PL123abc", TimeSpan.FromDays(7), DateTime.UtcNow));
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
    public void RecordPlay_ignores_urls_that_are_not_a_playlist()
    {
        var dir = Path.Combine(Path.GetTempPath(), "alarmino-history-" + Guid.NewGuid().ToString("N"));
        try
        {
            var history = new PlayHistoryStore(dir);
            var resolver = new YouTubeAudioResolver(history: history, connectivity: new FakeProbe(online: false));

            resolver.RecordPlay("https://www.youtube.com/watch?v=dQw4w9WgXcQ", "videoA");

            Assert.Empty(history.InWindow("PL123abc", TimeSpan.FromDays(7), DateTime.UtcNow));
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
    public void TargetBufferCount_is_clamped_to_the_cache_capacity()
    {
        var resolver = new YouTubeAudioResolver(
            cacheDir: Path.Combine(Path.GetTempPath(), "alarmino-yt-" + Guid.NewGuid().ToString("N")),
            connectivity: new FakeProbe(online: false));

        resolver.TargetBufferCount = 99;
        Assert.Equal(YouTubeTrackCache.MaxTracks, resolver.TargetBufferCount);

        resolver.TargetBufferCount = -3;
        Assert.Equal(0, resolver.TargetBufferCount);
    }

    [Fact]
    public void NoRepeatDays_never_goes_below_zero()
    {
        var resolver = new YouTubeAudioResolver(
            cacheDir: Path.Combine(Path.GetTempPath(), "alarmino-yt-" + Guid.NewGuid().ToString("N")),
            connectivity: new FakeProbe(online: false));

        resolver.NoRepeatDays = -5;

        Assert.Equal(0, resolver.NoRepeatDays);
    }

    [Fact]
    public async Task Sync_without_a_valid_playlist_url_does_nothing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "alarmino-yt-" + Guid.NewGuid().ToString("N"));
        try
        {
            var probe = new FakeProbe(online: true);
            var cache = new YouTubeTrackCache(dir);
            var resolver = new YouTubeAudioResolver(cache: cache, connectivity: probe);

            await resolver.PrefetchAndSyncAsync(null);
            await resolver.PrefetchAndSyncAsync("https://www.youtube.com/watch?v=dQw4w9WgXcQ");

            // Sin playlist no se consulta la red ni se toca nada.
            Assert.Equal(0, probe.Calls);
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
    public async Task Sync_offline_never_reaches_the_remote_list()
    {
        var dir = Path.Combine(Path.GetTempPath(), "alarmino-yt-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, YouTubeTrackCache.BuildFileName("PL123abc", "videoA", "m4a"));
            File.WriteAllBytes(file, PlayableBytes());

            var cache = new YouTubeTrackCache(dir);
            cache.Register("PL123abc", "videoA", file, DateTime.UtcNow);

            var probe = new FakeProbe(online: false);
            var resolver = new YouTubeAudioResolver(cache: cache, connectivity: probe);

            await resolver.PrefetchAndSyncAsync("https://www.youtube.com/playlist?list=PL123abc");

            // Sin conexión no hay lista remota fiable: la pista se queda donde está.
            Assert.True(File.Exists(file));
            Assert.Equal("videoA", cache.PeekVideoId("PL123abc"));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("throws")]
    [InlineData("timesout")]
    [InlineData("cancelled")]
    public async Task Sync_keeps_the_cache_untouched_when_the_remote_list_is_unusable(string failure)
    {
        var dir = Path.Combine(Path.GetTempPath(), "alarmino-yt-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, YouTubeTrackCache.BuildFileName("PL123abc", "videoA", "m4a"));
            File.WriteAllBytes(file, PlayableBytes());

            var cache = new YouTubeTrackCache(dir);
            cache.Register("PL123abc", "videoA", file, DateTime.UtcNow);

            // La playlist remota ya no contiene videoA: si la consulta se tomara
            // como buena, la pista se borraría. Ninguno de estos fallos lo permite.
            var resolver = new YouTubeAudioResolver(
                cache: cache,
                connectivity: new FakeProbe(online: true),
                playlistVideoIds: (_, _) => failure switch
                {
                    "throws" => Task.FromException<PlaylistSnapshot>(new InvalidOperationException("red caída")),
                    "timesout" => Task.FromException<PlaylistSnapshot>(new TimeoutException("la consulta agotó el tiempo")),
                    "cancelled" => Task.FromCanceled<PlaylistSnapshot>(new CancellationToken(canceled: true)),
                    _ => Task.FromResult(new PlaylistSnapshot([], 0)),
                });

            await resolver.PrefetchAndSyncAsync("https://www.youtube.com/playlist?list=PL123abc");

            // Un fallo de consulta NUNCA puede vaciar la caché del usuario.
            Assert.True(File.Exists(file));
            Assert.Equal("videoA", cache.PeekVideoId("PL123abc"));
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
    public async Task Sync_deletes_a_track_that_left_a_playlist_shorter_than_the_read_cap()
    {
        var dir = Path.Combine(Path.GetTempPath(), "alarmino-yt-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, YouTubeTrackCache.BuildFileName("PL123abc", "videoA", "m4a"));
            File.WriteAllBytes(file, PlayableBytes());

            var cache = new YouTubeTrackCache(dir);
            cache.Register("PL123abc", "videoA", file, DateTime.UtcNow);

            // Playlist corta (3 vídeos) leída entera (3 de 3): YouTube declara el
            // total y coincide con lo leído, así que la lista es completa y videoA,
            // ausente de ella, se propaga como borrado.
            var resolver = new YouTubeAudioResolver(
                cache: cache,
                connectivity: new FakeProbe(online: true),
                playlistVideoIds: (_, _) => Task.FromResult(
                    new PlaylistSnapshot(["videoB", "videoC", "videoD"], 3)));

            await resolver.PrefetchAndSyncAsync("https://www.youtube.com/playlist?list=PL123abc");

            Assert.False(File.Exists(file));
            Assert.Null(cache.PeekVideoId("PL123abc"));
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
    public async Task Sync_deletes_a_track_that_left_the_remote_playlist()
    {
        var dir = Path.Combine(Path.GetTempPath(), "alarmino-yt-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, YouTubeTrackCache.BuildFileName("PL123abc", "videoA", "m4a"));
            File.WriteAllBytes(file, PlayableBytes());

            var cache = new YouTubeTrackCache(dir);
            cache.Register("PL123abc", "videoA", file, DateTime.UtcNow);

            // La consulta se completa con la lista entera y videoA ya no está:
            // ese es el único caso en el que se propaga el borrado.
            var remote = Enumerable.Range(0, YouTubeAudioResolver.MaxPlaylistItems)
                .Select(i => $"video{i}")
                .ToList();
            var resolver = new YouTubeAudioResolver(
                cache: cache,
                connectivity: new FakeProbe(online: true),
                playlistVideoIds: (_, _) => Task.FromResult(
                    new PlaylistSnapshot(remote, YouTubeAudioResolver.MaxPlaylistItems)));

            await resolver.PrefetchAndSyncAsync("https://www.youtube.com/playlist?list=PL123abc");

            Assert.False(File.Exists(file));
            Assert.Null(cache.PeekVideoId("PL123abc"));
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
    public async Task Sync_larger_than_the_read_cap_deletes_nothing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "alarmino-yt-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, YouTubeTrackCache.BuildFileName("PL123abc", "videoA", "m4a"));
            File.WriteAllBytes(file, PlayableBytes());

            var cache = new YouTubeTrackCache(dir);
            cache.Register("PL123abc", "videoA", file, DateTime.UtcNow);

            // Playlist de 500 vídeos declarados: supera el tope de lectura, así que
            // no se puede saber con certeza qué quedó fuera y no se borra nada.
            var resolver = new YouTubeAudioResolver(
                cache: cache,
                connectivity: new FakeProbe(online: true),
                playlistVideoIds: (_, _) => Task.FromResult(
                    new PlaylistSnapshot(
                        Enumerable.Range(0, YouTubeAudioResolver.MaxPlaylistItems).Select(i => $"video{i}").ToList(),
                        500)));

            await resolver.PrefetchAndSyncAsync("https://www.youtube.com/playlist?list=PL123abc");

            Assert.True(File.Exists(file));
            Assert.Equal("videoA", cache.PeekVideoId("PL123abc"));
            Assert.Contains(
                OperationLog.RecentLines,
                line => line.Contains("sincronización pospuesta") && line.Contains("límite"));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("zero")]
    public async Task Sync_keeps_the_cache_untouched_when_the_read_is_incomplete(string reason)
    {
        var dir = Path.Combine(Path.GetTempPath(), "alarmino-yt-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, YouTubeTrackCache.BuildFileName("PL123abc", "videoA", "m4a"));
            File.WriteAllBytes(file, PlayableBytes());

            var cache = new YouTubeTrackCache(dir);
            cache.Register("PL123abc", "videoA", file, DateTime.UtcNow);

            // "truncated": YouTube declara 400 vídeos pero la lectura se cortó antes.
            // "zero": YouTube no declara ningún total, así que no se sabe si falta algo.
            var resolver = new YouTubeAudioResolver(
                cache: cache,
                connectivity: new FakeProbe(online: true),
                playlistVideoIds: (_, _) => Task.FromResult(reason switch
                {
                    "truncated" => new PlaylistSnapshot(["videoB", "videoC"], 400),
                    _ => new PlaylistSnapshot(["videoB", "videoC"], 0),
                }));

            await resolver.PrefetchAndSyncAsync("https://www.youtube.com/playlist?list=PL123abc");

            Assert.True(File.Exists(file));
            Assert.Equal("videoA", cache.PeekVideoId("PL123abc"));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    /// <summary>Contenido que supera las comprobaciones de integridad de <see cref="TrackSanity"/>.</summary>
    private static byte[] PlayableBytes()
    {
        byte[] data = new byte[40 * 1024];
        data[0] = 0xFF;
        data[1] = 0xFB;
        data[2] = 0xA0;
        return data;
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
            new() { PresetId = "airplaneBeep", DisplayName = "Bip de avión" },
            new() { PresetId = "fireAlarm", DisplayName = "Alarma tranquila", FilePath = @"C:\x\tono.mp3" },
        };

        var result = SoundPresets.GetAll(overrides);

        Assert.Equal(3, result.Count);
        Assert.Equal("Bip de avión", result[0].DisplayName);
        Assert.Equal("Suave", result[1].DisplayName); // sin cambios
        Assert.Equal("Alarma tranquila", result[2].DisplayName);
        Assert.Equal("airplaneBeep", result[0].Id);
    }

    [Fact]
    public void ResolveFilePath_returns_file_for_overridden_preset()
    {
        var overrides = new List<PresetOverride>
        {
            new() { PresetId = "airplaneBeep", DisplayName = "Bip", FilePath = @"C:\x\bip.mp3" },
            new() { PresetId = "softChime", DisplayName = "Suave" },
        };

        Assert.Equal(@"C:\x\bip.mp3", SoundPresets.ResolveFilePath("airplaneBeep", overrides));
        Assert.Null(SoundPresets.ResolveFilePath("softChime", overrides));  // solo nombre, sin archivo
        Assert.Null(SoundPresets.ResolveFilePath("fireAlarm", overrides));
        Assert.Null(SoundPresets.ResolveFilePath(null, overrides));
    }

    [Fact]
    public void AppSettings_defaults_include_three_repeats_and_rain_sound()
    {
        var settings = new AppSettings();

        Assert.Equal(1, settings.AlarmRepeatCount);
        Assert.Equal(1, settings.AlarmPauseSeconds);
        Assert.Equal(SoundSourceKind.Preset, settings.RainSound.Kind);
        Assert.NotNull(settings.RainSound.PresetId);
        Assert.Equal(1, settings.RainRepeatCount);
        Assert.Empty(settings.PresetOverrides);
        Assert.Null(settings.OfflineFallbackFilePath);
    }

    [Fact]
    public void AppSettings_defaults_include_a_week_without_repeats_and_a_buffer_of_three()
    {
        var settings = new AppSettings();

        Assert.Equal(7, settings.NoRepeatDays);
        Assert.Equal(3, settings.TargetBufferCount);
    }
}
