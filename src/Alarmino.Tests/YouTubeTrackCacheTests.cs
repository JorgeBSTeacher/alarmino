using System.Text.Json;
using Alarmino.Core.Services;

namespace Alarmino.Tests;

// La comprobación de «no borra nada con un conjunto vacío» mira las últimas
// líneas del log, que OperationLog guarda en memoria y comparte con el resto de
// pruebas: se serializa con ellas para que la ventana no se llene por otro lado.
[Collection("operation-log")]
public sealed class YouTubeTrackCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "alarmino-yt-cache-" + Guid.NewGuid().ToString("N"));

    private string ManifestPath => Path.Combine(_dir, "manifest.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    /// <summary>Contenido que supera las comprobaciones de integridad de <see cref="TrackSanity"/>.</summary>
    private static byte[] PlayableContent()
    {
        // 40 KB con cabecera MPEG (160 kbps, 44,1 kHz → 2 s de audio válido).
        byte[] data = new byte[40 * 1024];
        data[0] = 0xFF;
        data[1] = 0xFB;
        data[2] = 0xA0;
        return data;
    }

    private string CreateFile(string playlistId, string videoId, string extension = "m4a")
    {
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, YouTubeTrackCache.BuildFileName(playlistId, videoId, extension));
        File.WriteAllBytes(path, PlayableContent());
        return path;
    }

    private void WriteManifest(params object[] tracks)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(ManifestPath, JsonSerializer.Serialize(new { Tracks = tracks }));
    }

    [Fact]
    public void Register_keeps_every_track_of_a_playlist()
    {
        var cache = new YouTubeTrackCache(_dir);
        cache.Register("PL1", "videoA", CreateFile("PL1", "videoA"), DateTime.UtcNow);
        cache.Register("PL1", "videoB", CreateFile("PL1", "videoB"), DateTime.UtcNow.AddMinutes(1));
        cache.Register("PL1", "videoC", CreateFile("PL1", "videoC"), DateTime.UtcNow.AddMinutes(2));

        Assert.Equal(3, cache.GetDownloadedCount("PL1"));
        Assert.Equal(3, cache.TotalDownloadedCount);
        Assert.Equal(
            ["videoA", "videoB", "videoC"],
            cache.GetVideoIds("PL1").OrderBy(id => id, StringComparer.Ordinal));
    }

    [Fact]
    public void Register_keeps_a_sibling_track_that_already_existed_on_disk()
    {
        // Las dos pistas ya están en la carpeta antes de registrarse (p. ej. una
        // sesión anterior): registrar una no puede barrer a la otra por «huérfana».
        string first = CreateFile("PL1", "videoA");
        string second = CreateFile("PL1", "videoB");
        var cache = new YouTubeTrackCache(_dir);

        cache.Register("PL1", "videoA", first, DateTime.UtcNow);
        cache.Register("PL1", "videoB", second, DateTime.UtcNow.AddMinutes(1));

        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
        Assert.Equal(2, cache.GetDownloadedCount("PL1"));
        Assert.Equal(
            ["videoA", "videoB"],
            cache.GetVideoIds("PL1").OrderBy(id => id, StringComparer.Ordinal));
    }

    [Fact]
    public void Register_replaces_the_same_track_without_duplicating_it()
    {
        var cache = new YouTubeTrackCache(_dir);
        string first = CreateFile("PL1", "videoA", "m4a");
        cache.Register("PL1", "videoA", first, DateTime.UtcNow);

        // La misma pista descargada otra vez con otra extensión sustituye la anterior.
        string second = CreateFile("PL1", "videoA", "mp3");
        cache.Register("PL1", "videoA", second, DateTime.UtcNow.AddMinutes(1));

        Assert.False(File.Exists(first));
        Assert.True(File.Exists(second));
        Assert.Equal(1, cache.GetDownloadedCount("PL1"));

        Assert.True(cache.TryTakeRandom("PL1", new HashSet<string>(), out string? taken, out string? videoId));
        Assert.Equal(second, taken);
        Assert.Equal("videoA", videoId);
    }

    [Fact]
    public void DescribeEntries_reports_every_registered_track()
    {
        var cache = new YouTubeTrackCache(_dir);
        cache.Register("PL1", "videoA", CreateFile("PL1", "videoA"), DateTime.UtcNow);
        cache.Register("PL2", "videoB", CreateFile("PL2", "videoB"), DateTime.UtcNow, consumed: true);

        IReadOnlyList<CachedTrackInfo> entries = cache.DescribeEntries();

        Assert.Equal(2, entries.Count);

        CachedTrackInfo first = entries.Single(e => e.PlaylistId == "PL1");
        Assert.Equal("videoA", first.VideoId);
        Assert.False(first.Consumed);
        Assert.True(first.FileExists);
        Assert.Equal(40 * 1024, first.Bytes);

        CachedTrackInfo second = entries.Single(e => e.PlaylistId == "PL2");
        Assert.True(second.Consumed);
    }

    [Fact]
    public void DescribeEntries_is_empty_without_tracks()
    {
        var cache = new YouTubeTrackCache(_dir);

        Assert.Empty(cache.DescribeEntries());
        Assert.Equal(0, cache.TotalDownloadedCount);
    }

    [Fact]
    public void TryTakeRandom_returns_a_downloaded_track_and_marks_it_consumed()
    {
        var cache = new YouTubeTrackCache(_dir);
        cache.Register("PL1", "videoA", CreateFile("PL1", "videoA"), DateTime.UtcNow);
        cache.Register("PL1", "videoB", CreateFile("PL1", "videoB"), DateTime.UtcNow.AddMinutes(1));

        Assert.True(cache.TryTakeRandom("PL1", new HashSet<string>(), out string? taken, out string? videoId));
        Assert.NotNull(taken);
        Assert.True(File.Exists(taken));
        Assert.Contains(videoId, new[] { "videoA", "videoB" });

        // La elegida queda marcada solo para el diagnóstico; la otra sigue disponible.
        CachedTrackInfo chosen = cache.DescribeEntries().Single(e => e.VideoId == videoId);
        Assert.True(chosen.Consumed);

        Assert.True(cache.TryTakeRandom(
            "PL1", new HashSet<string> { videoId! }, out string? otherFile, out string? otherId));
        Assert.NotEqual(videoId, otherId);
        Assert.True(File.Exists(otherFile));
    }

    [Fact]
    public void TryTakeRandom_respects_excluded_video_ids()
    {
        var cache = new YouTubeTrackCache(_dir);
        cache.Register("PL1", "videoA", CreateFile("PL1", "videoA"), DateTime.UtcNow);
        cache.Register("PL1", "videoB", CreateFile("PL1", "videoB"), DateTime.UtcNow.AddMinutes(1));

        for (int i = 0; i < 5; i++)
        {
            Assert.True(cache.TryTakeRandom(
                "PL1", new HashSet<string> { "videoA" }, out _, out string? videoId));
            Assert.Equal("videoB", videoId);
        }
    }

    [Fact]
    public void TryTakeRandom_returns_false_when_all_tracks_are_excluded()
    {
        var cache = new YouTubeTrackCache(_dir);
        cache.Register("PL1", "videoA", CreateFile("PL1", "videoA"), DateTime.UtcNow);
        cache.Register("PL1", "videoB", CreateFile("PL1", "videoB"), DateTime.UtcNow.AddMinutes(1));

        Assert.False(cache.TryTakeRandom(
            "PL1", new HashSet<string> { "videoA", "videoB" }, out string? taken, out string? videoId));
        Assert.Null(taken);
        Assert.Null(videoId);
    }

    [Fact]
    public void TryTakeRandom_returns_false_without_tracks()
    {
        var cache = new YouTubeTrackCache(_dir);

        Assert.False(cache.TryTakeRandom("PL1", new HashSet<string>(), out string? taken, out string? videoId));
        Assert.Null(taken);
        Assert.Null(videoId);
    }

    [Fact]
    public void TryTakeRandom_ignores_tracks_whose_file_is_missing()
    {
        var cache = new YouTubeTrackCache(_dir);
        string missing = CreateFile("PL1", "videoA");
        cache.Register("PL1", "videoA", missing, DateTime.UtcNow);
        cache.Register("PL1", "videoB", CreateFile("PL1", "videoB"), DateTime.UtcNow.AddMinutes(1));
        File.Delete(missing);

        // Solo cuenta lo que sigue en disco: la pista fantasma no se sirve nunca.
        for (int i = 0; i < 5; i++)
        {
            Assert.True(cache.TryTakeRandom("PL1", new HashSet<string>(), out _, out string? videoId));
            Assert.Equal("videoB", videoId);
        }

        Assert.Equal(1, cache.GetDownloadedCount("PL1"));
        Assert.Equal(1, cache.TotalDownloadedCount);
        Assert.Equal(["videoB"], cache.GetVideoIds("PL1"));
    }

    [Fact]
    public void BeginWrite_protects_a_download_in_progress_from_the_sweep()
    {
        var cache = new YouTubeTrackCache(_dir);
        cache.Register("PL1", "videoA", CreateFile("PL1", "videoA"), DateTime.UtcNow);

        string inFlight = CreateFile("PL2", "videoB");
        cache.BeginWrite(inFlight);

        // Registrar otra pista no puede tocar una descarga en curso de otra playlist.
        cache.Register("PL3", "videoC", CreateFile("PL3", "videoC"), DateTime.UtcNow.AddMinutes(1));
        Assert.True(File.Exists(inFlight));

        cache.Register("PL2", "videoB", inFlight, DateTime.UtcNow.AddMinutes(2));
        Assert.True(cache.TryTakeRandom("PL2", new HashSet<string>(), out string? taken, out _));
        Assert.Equal(inFlight, taken);
    }

    [Fact]
    public void LoadExisting_recovers_every_track_from_previous_session()
    {
        var original = new YouTubeTrackCache(_dir);
        original.Register("PLabc", "videoA", CreateFile("PLabc", "videoA"), DateTime.UtcNow);
        original.Register("PLabc", "videoB", CreateFile("PLabc", "videoB"), DateTime.UtcNow.AddMinutes(1));

        var restarted = new YouTubeTrackCache(_dir);
        restarted.LoadExisting();

        Assert.Equal(2, restarted.GetDownloadedCount("PLabc"));
        Assert.Equal(
            ["videoA", "videoB"],
            restarted.GetVideoIds("PLabc").OrderBy(id => id, StringComparer.Ordinal));
    }

    [Fact]
    public void LoadExisting_restores_the_consumed_state_of_each_track()
    {
        var original = new YouTubeTrackCache(_dir);
        original.Register("PL1", "videoA", CreateFile("PL1", "videoA"), DateTime.UtcNow);
        original.Register("PL1", "videoB", CreateFile("PL1", "videoB"), DateTime.UtcNow.AddMinutes(1), consumed: true);

        var restarted = new YouTubeTrackCache(_dir);
        restarted.LoadExisting();

        IReadOnlyList<CachedTrackInfo> entries = restarted.DescribeEntries();
        Assert.False(entries.Single(e => e.VideoId == "videoA").Consumed);
        Assert.True(entries.Single(e => e.VideoId == "videoB").Consumed);
    }

    [Fact]
    public void LoadExisting_migrates_a_manifest_with_one_track_per_playlist()
    {
        string first = CreateFile("PLold", "videoA");
        string second = CreateFile("PLold", "videoB");

        // Manifiesto de una versión anterior: una sola entrada por playlist,
        // aunque en la carpeta ya haya más pistas de esa misma playlist.
        WriteManifest(new
        {
            PlaylistId = "PLold",
            VideoId = "videoA",
            FileName = Path.GetFileName(first),
            DownloadedAtUtc = DateTime.UtcNow,
            Consumed = true,
            SizeBytes = (long?)new FileInfo(first).Length,
        });

        var cache = new YouTubeTrackCache(_dir);
        cache.LoadExisting();

        Assert.Equal(2, cache.GetDownloadedCount("PLold"));
        Assert.Contains("videoA", cache.GetVideoIds("PLold"));
        Assert.Contains("videoB", cache.GetVideoIds("PLold"));

        IReadOnlyList<CachedTrackInfo> entries = cache.DescribeEntries();
        Assert.True(entries.Single(e => e.VideoId == "videoA").Consumed);
        Assert.False(entries.Single(e => e.VideoId == "videoB").Consumed);
        Assert.True(File.Exists(second));
    }

    [Fact]
    public void LoadExisting_matches_an_old_manifest_entry_without_video_id_by_file_name()
    {
        string file = CreateFile("PLold", "videoA");

        WriteManifest(new
        {
            PlaylistId = "PLold",
            VideoId = "",
            FileName = Path.GetFileName(file),
            DownloadedAtUtc = DateTime.UtcNow,
            Consumed = true,
            SizeBytes = (long?)new FileInfo(file).Length,
        });

        var cache = new YouTubeTrackCache(_dir);
        cache.LoadExisting();

        CachedTrackInfo entry = Assert.Single(cache.DescribeEntries());
        Assert.Equal("videoA", entry.VideoId);
        Assert.True(entry.Consumed);
    }

    [Fact]
    public void LoadExisting_discards_a_recovered_file_below_the_minimum_size()
    {
        // Una descarga interrumpida a media transmisión deja un archivo casi vacío.
        Directory.CreateDirectory(_dir);
        string junk = Path.Combine(_dir, YouTubeTrackCache.BuildFileName("PL1", "videoA", "m4a"));
        File.WriteAllText(junk, "cortada");

        var cache = new YouTubeTrackCache(_dir);
        cache.LoadExisting();

        Assert.Empty(cache.GetVideoIds("PL1"));
        Assert.Equal(0, cache.TotalDownloadedCount);
        Assert.False(File.Exists(junk));
    }

    [Fact]
    public void LoadExisting_discards_a_file_truncated_below_the_manifest_size()
    {
        string file = CreateFile("PL1", "videoA");
        var cache = new YouTubeTrackCache(_dir);
        cache.Register("PL1", "videoA", file, DateTime.UtcNow);

        // Corte de luz a media descarga: el archivo queda por debajo del tamaño
        // registrado en el manifiesto, así que no es una pista utilizable.
        byte[] full = File.ReadAllBytes(file);
        File.WriteAllBytes(file, full[..(int)(full.Length * 0.6)]);

        var restarted = new YouTubeTrackCache(_dir);
        restarted.LoadExisting();

        Assert.Empty(restarted.GetVideoIds("PL1"));
        Assert.False(File.Exists(file));
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
        Assert.Empty(cache.GetVideoIds("PL1"));
    }

    [Fact]
    public void RemoveTracksNotIn_removes_every_stale_track_of_the_playlist()
    {
        var cache = new YouTubeTrackCache(_dir);
        string first = CreateFile("PL1", "videoA");
        string second = CreateFile("PL1", "videoB");
        string other = CreateFile("PL2", "videoC");
        cache.LoadExisting();

        IReadOnlyList<string> removed = cache.RemoveTracksNotIn("PL1", new HashSet<string> { "videoX" });

        Assert.Equal(2, removed.Count);
        Assert.Contains(first, removed);
        Assert.Contains(second, removed);
        Assert.False(File.Exists(first));
        Assert.False(File.Exists(second));
        Assert.Empty(cache.GetVideoIds("PL1"));
        Assert.Equal(["videoC"], cache.GetVideoIds("PL2"));
    }

    [Fact]
    public void RemoveTracksNotIn_keeps_tracks_still_present_remotely()
    {
        var cache = new YouTubeTrackCache(_dir);
        string first = CreateFile("PL1", "videoA");
        string second = CreateFile("PL1", "videoB");
        cache.LoadExisting();

        IReadOnlyList<string> removed = cache.RemoveTracksNotIn(
            "PL1", new HashSet<string> { "videoA", "videoB", "videoC" });

        Assert.Empty(removed);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
        Assert.Equal(
            ["videoA", "videoB"],
            cache.GetVideoIds("PL1").OrderBy(id => id, StringComparer.Ordinal));
    }

    [Fact]
    public void RemoveTracksNotIn_refuses_an_empty_allowed_set()
    {
        var cache = new YouTubeTrackCache(_dir);
        string file = CreateFile("PL1", "videoA");
        cache.Register("PL1", "videoA", file, DateTime.UtcNow);

        // Un conjunto vacío es indistinguible de «la descarga devolvió nada»:
        // la caché se niega a borrar nada por su cuenta y lo deja anotado.
        IReadOnlyList<string> removed = cache.RemoveTracksNotIn("PL1", new HashSet<string>());

        Assert.Empty(removed);
        Assert.True(File.Exists(file));
        Assert.Equal(["videoA"], cache.GetVideoIds("PL1"));
    }

    [Fact]
    public void RemoveTracksNotIn_touches_only_the_given_playlist()
    {
        var cache = new YouTubeTrackCache(_dir);
        string first = CreateFile("PL1", "videoA");
        string second = CreateFile("PL2", "videoB");
        cache.LoadExisting();

        IReadOnlyList<string> removed = cache.RemoveTracksNotIn("PL1", new HashSet<string> { "videoC" });

        Assert.Equal([first], removed);
        Assert.False(File.Exists(first));
        Assert.Equal(["videoB"], cache.GetVideoIds("PL2"));
        // La otra playlist conserva su pista en el manifiesto.
        Assert.Contains("PL2", File.ReadAllText(ManifestPath));
    }

    [Fact]
    public void RemoveTracksNotIn_forgets_removed_tracks_in_the_persisted_manifest()
    {
        var cache = new YouTubeTrackCache(_dir);
        cache.Register("PL1", "videoA", CreateFile("PL1", "videoA"), DateTime.UtcNow);
        cache.Register("PL1", "videoB", CreateFile("PL1", "videoB"), DateTime.UtcNow.AddMinutes(1));

        cache.RemoveTracksNotIn("PL1", new HashSet<string> { "videoC" });

        var restarted = new YouTubeTrackCache(_dir);
        restarted.LoadExisting();

        Assert.Empty(restarted.GetVideoIds("PL1"));
        Assert.DoesNotContain("videoA", File.ReadAllText(ManifestPath));
        Assert.DoesNotContain("videoB", File.ReadAllText(ManifestPath));
    }

    [Fact]
    public void RemoveTracksNotIn_never_deletes_a_file_being_written()
    {
        var cache = new YouTubeTrackCache(_dir);

        // La descarga en curso usa el mismo nombre que el registro de la playlist,
        // pero todavía no está terminada: borrarla dejaría la descarga a medias.
        string inFlight = Path.Combine(_dir, YouTubeTrackCache.BuildFileName("PL1", "videoA", "m4a"));
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(inFlight, PlayableContent());
        cache.BeginWrite(inFlight);

        IReadOnlyList<string> removed = cache.RemoveTracksNotIn("PL1", new HashSet<string> { "videoB" });

        Assert.Empty(removed);
        Assert.True(File.Exists(inFlight));
    }

    [Fact]
    public void RemovePlaylistsNotIn_removes_every_track_of_abandoned_playlists()
    {
        var cache = new YouTubeTrackCache(_dir);
        string used = CreateFile("PLused", "videoA");
        string abandonedFirst = CreateFile("PLgone", "videoB");
        string abandonedSecond = CreateFile("PLgone", "videoC");
        cache.Register("PLused", "videoA", used, DateTime.UtcNow);
        cache.Register("PLgone", "videoB", abandonedFirst, DateTime.UtcNow.AddMinutes(1));
        cache.Register("PLgone", "videoC", abandonedSecond, DateTime.UtcNow.AddMinutes(2));

        IReadOnlyList<string> removed = cache.RemovePlaylistsNotIn(new HashSet<string> { "PLused" });

        Assert.Equal(2, removed.Count);
        Assert.Contains(abandonedFirst, removed);
        Assert.Contains(abandonedSecond, removed);
        Assert.False(File.Exists(abandonedFirst));
        Assert.False(File.Exists(abandonedSecond));
        Assert.Empty(cache.GetVideoIds("PLgone"));
        Assert.True(File.Exists(used));
        Assert.Equal(["videoA"], cache.GetVideoIds("PLused"));
    }

    [Fact]
    public void RemovePlaylistsNotIn_refuses_an_empty_allowed_set()
    {
        var cache = new YouTubeTrackCache(_dir);
        string file = CreateFile("PL1", "videoA");
        cache.Register("PL1", "videoA", file, DateTime.UtcNow);
        OperationLog.ResetRoot(Path.Combine(_dir, "logs"));

        // Un conjunto vacío es indistinguible de «los ajustes no cargaron»: la
        // caché se niega a borrar por su cuenta y lo deja anotado.
        IReadOnlyList<string> removed = cache.RemovePlaylistsNotIn(new HashSet<string>());

        Assert.Empty(removed);
        Assert.True(File.Exists(file));
        Assert.Equal(["videoA"], cache.GetVideoIds("PL1"));
        Assert.Contains(
            OperationLog.RecentLines,
            line => line.Contains("[cache]") && line.Contains("no se borra nada: no hay ninguna playlist en uso"));
    }

    [Fact]
    public void RemovePlaylistsNotIn_never_deletes_a_file_being_written()
    {
        var cache = new YouTubeTrackCache(_dir);

        // La descarga en curso usa el nombre de una playlist abandonada, pero
        // todavía no está terminada: borrarla dejaría la descarga a medias.
        string inFlight = Path.Combine(_dir, YouTubeTrackCache.BuildFileName("PLgone", "videoB", "m4a"));
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(inFlight, PlayableContent());
        cache.BeginWrite(inFlight);

        IReadOnlyList<string> removed = cache.RemovePlaylistsNotIn(new HashSet<string> { "PLused" });

        // La descarga en curso se queda: la lista solo trae las rutas borradas.
        Assert.Empty(removed);
        Assert.True(File.Exists(inFlight));
    }

    [Fact]
    public void RemovePlaylistsNotIn_forgets_the_removed_playlist_in_the_persisted_manifest()
    {
        var cache = new YouTubeTrackCache(_dir);
        cache.Register("PLgone", "videoB", CreateFile("PLgone", "videoB"), DateTime.UtcNow);
        cache.Register("PLgone", "videoC", CreateFile("PLgone", "videoC"), DateTime.UtcNow.AddMinutes(1));

        cache.RemovePlaylistsNotIn(new HashSet<string> { "PLused" });

        // Al reiniciar, la carpeta ya no tiene nada que recuperar: el manifiesto
        // también olvidó la playlist.
        var restarted = new YouTubeTrackCache(_dir);
        restarted.LoadExisting();

        Assert.Empty(restarted.GetVideoIds("PLgone"));
        Assert.Equal(0, restarted.TotalDownloadedCount);
        Assert.DoesNotContain("PLgone", File.ReadAllText(ManifestPath));
    }
}
