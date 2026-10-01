using Alarmino.Core.Services;

namespace Alarmino.Tests;

public sealed class YouTubeTrackCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "alarmino-yt-cache-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private string CreateFile(string playlistId, string videoId, string extension = "m4a")
    {
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, YouTubeTrackCache.BuildFileName(playlistId, videoId, extension));
        File.WriteAllText(path, "audio");
        return path;
    }

    [Fact]
    public void TryTake_returns_preloaded_track_once()
    {
        var cache = new YouTubeTrackCache(_dir);
        string file = CreateFile("PL1", "videoA");
        cache.Register("PL1", "videoA", file, DateTime.UtcNow);

        Assert.True(cache.TryTake("PL1", out string? taken, out string? videoId));
        Assert.Equal(file, taken);
        Assert.Equal("videoA", videoId);

        // La pista ya se usó: la siguiente vez hay que precargar otra.
        Assert.False(cache.TryTake("PL1", out _, out _));
    }

    [Fact]
    public void IsFresh_is_false_after_taking_and_when_old()
    {
        var cache = new YouTubeTrackCache(_dir);
        string file = CreateFile("PL1", "videoA");

        cache.Register("PL1", "videoA", file, DateTime.UtcNow);
        Assert.True(cache.IsFresh("PL1", TimeSpan.FromHours(6), DateTime.UtcNow));

        cache.TryTake("PL1", out _, out _);
        Assert.False(cache.IsFresh("PL1", TimeSpan.FromHours(6), DateTime.UtcNow));

        cache.Register("PL2", "videoB", CreateFile("PL2", "videoB"), DateTime.UtcNow.AddHours(-7));
        Assert.False(cache.IsFresh("PL2", TimeSpan.FromHours(6), DateTime.UtcNow));
    }

    [Fact]
    public void Register_replaces_previous_track_of_same_playlist()
    {
        var cache = new YouTubeTrackCache(_dir);
        cache.Register("PL1", "videoA", CreateFile("PL1", "videoA"), DateTime.UtcNow);
        string first = Path.Combine(_dir, YouTubeTrackCache.BuildFileName("PL1", "videoA", "m4a"));
        string second = CreateFile("PL1", "videoB");
        cache.Register("PL1", "videoB", second, DateTime.UtcNow.AddMinutes(1));

        Assert.True(cache.TryTake("PL1", out string? taken, out string? videoId));
        Assert.Equal(second, taken);
        Assert.Equal("videoB", videoId);
        Assert.False(File.Exists(first));
    }

    [Fact]
    public void Register_keeps_at_most_MaxTracks_and_deletes_the_oldest()
    {
        var cache = new YouTubeTrackCache(_dir);
        var files = new List<string>();
        DateTime when = DateTime.UtcNow;

        for (int i = 0; i < YouTubeTrackCache.MaxTracks + 1; i++)
        {
            string file = CreateFile($"PL{i}", $"video{i}");
            cache.Register($"PL{i}", $"video{i}", file, when.AddMinutes(i));
            files.Add(file);
        }

        // La más antigua (PL0) se descarta.
        Assert.False(File.Exists(files[0]));
        Assert.False(cache.IsFresh("PL0", TimeSpan.FromHours(6), when.AddHours(1)));
        Assert.True(cache.IsFresh($"PL{YouTubeTrackCache.MaxTracks}", TimeSpan.FromHours(6), when.AddHours(1)));
    }

    [Fact]
    public void LoadExisting_recovers_tracks_from_previous_session()
    {
        string file = CreateFile("PLabc", "videoZ");

        var cache = new YouTubeTrackCache(_dir);
        cache.LoadExisting();

        Assert.True(cache.IsFresh("PLabc", TimeSpan.FromHours(6), DateTime.UtcNow));
        Assert.True(cache.TryTake("PLabc", out string? taken, out string? videoId));
        Assert.Equal(file, taken);
        Assert.Equal("videoZ", videoId);
    }

    [Fact]
    public void BeginWrite_protects_a_download_in_progress_from_the_sweep()
    {
        var cache = new YouTubeTrackCache(_dir);
        cache.Register("PL1", "videoA", CreateFile("PL1", "videoA"), DateTime.UtcNow);

        string inFlight = CreateFile("PL2", "videoB");
        cache.BeginWrite(inFlight);

        // Al registrar otra pista se barren los huérfanos, menos la descarga en curso.
        cache.Register("PL3", "videoC", CreateFile("PL3", "videoC"), DateTime.UtcNow.AddMinutes(1));
        Assert.True(File.Exists(inFlight));

        cache.Register("PL2", "videoB", inFlight, DateTime.UtcNow.AddMinutes(2));
        Assert.True(cache.TryTake("PL2", out string? taken, out _));
        Assert.Equal(inFlight, taken);
    }

    [Fact]
    public void LoadExisting_keeps_tracks_already_registered()
    {
        var cache = new YouTubeTrackCache(_dir);
        string file = CreateFile("PLabc", "videoZ");
        cache.Register("PLabc", "videoZ", file, DateTime.UtcNow, consumed: true);

        // Como una sesión nueva: la pista ya consumida se recupera, sin perder el archivo.
        var restarted = new YouTubeTrackCache(_dir);
        restarted.Register("PLabc", "videoZ", file, DateTime.UtcNow.AddMinutes(-5));
        restarted.LoadExisting();

        Assert.True(File.Exists(file));
        Assert.True(restarted.IsFresh("PLabc", TimeSpan.FromHours(6), DateTime.UtcNow));
    }

    [Fact]
    public void LoadExisting_ignores_unrelated_files()
    {
        Directory.CreateDirectory(_dir);
        string junk = Path.Combine(_dir, "basura.txt");
        File.WriteAllText(junk, "x");

        var cache = new YouTubeTrackCache(_dir);
        cache.LoadExisting();

        Assert.False(File.Exists(junk));
        Assert.False(cache.TryTake("PL1", out _, out _));
    }
}
