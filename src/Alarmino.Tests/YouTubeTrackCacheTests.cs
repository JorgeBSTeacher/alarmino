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
    public void LoadExisting_discards_a_recovered_file_below_the_minimum_size()
    {
        // Una descarga interrumpida a media transmisión deja un archivo casi vacío.
        Directory.CreateDirectory(_dir);
        string junk = Path.Combine(_dir, YouTubeTrackCache.BuildFileName("PL1", "videoA", "m4a"));
        File.WriteAllText(junk, "cortada");

        var cache = new YouTubeTrackCache(_dir);
        cache.LoadExisting();

        Assert.False(cache.TryTake("PL1", out _, out _));
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

        Assert.False(restarted.TryTake("PL1", out _, out _));
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void LoadExisting_restores_the_consumed_state()
    {
        string file = CreateFile("PL1", "videoA");
        var cache = new YouTubeTrackCache(_dir);
        cache.Register("PL1", "videoA", file, DateTime.UtcNow);
        Assert.True(cache.TryTake("PL1", out _, out _));  // la pista suena y se consume

        var restarted = new YouTubeTrackCache(_dir);
        restarted.LoadExisting();

        // Tras el reinicio la pista sigue consumida: no se vuelve a servir.
        Assert.False(restarted.TryTake("PL1", out _, out _));
        Assert.False(restarted.IsFresh("PL1", TimeSpan.FromHours(6), DateTime.UtcNow));
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
        Assert.True(cache.IsFresh("PL1", TimeSpan.FromHours(6), DateTime.UtcNow));
    }

    [Fact]
    public void RemoveTracksNotIn_removes_a_track_missing_from_the_remote_list()
    {
        var cache = new YouTubeTrackCache(_dir);
        string file = CreateFile("PL1", "videoA");
        cache.Register("PL1", "videoA", file, DateTime.UtcNow);

        IReadOnlyList<string> removed = cache.RemoveTracksNotIn("PL1", new HashSet<string> { "videoB" });

        Assert.Equal([file], removed);
        Assert.False(File.Exists(file));
        Assert.False(cache.IsFresh("PL1", TimeSpan.FromHours(6), DateTime.UtcNow));
        Assert.Null(cache.PeekVideoId("PL1"));
    }

    [Fact]
    public void RemoveTracksNotIn_keeps_a_track_still_present_remotely()
    {
        var cache = new YouTubeTrackCache(_dir);
        string file = CreateFile("PL1", "videoA");
        cache.Register("PL1", "videoA", file, DateTime.UtcNow);

        IReadOnlyList<string> removed = cache.RemoveTracksNotIn("PL1", new HashSet<string> { "videoA", "videoB" });

        Assert.Empty(removed);
        Assert.True(File.Exists(file));
        Assert.Equal("videoA", cache.PeekVideoId("PL1"));
    }

    [Fact]
    public void RemoveTracksNotIn_forgets_the_track_in_the_persisted_manifest()
    {
        var cache = new YouTubeTrackCache(_dir);
        string file = CreateFile("PL1", "videoA");
        cache.Register("PL1", "videoA", file, DateTime.UtcNow);

        cache.RemoveTracksNotIn("PL1", new HashSet<string> { "videoB" });

        var restarted = new YouTubeTrackCache(_dir);
        restarted.LoadExisting();

        Assert.False(restarted.IsFresh("PL1", TimeSpan.FromHours(6), DateTime.UtcNow));
    }

    [Fact]
    public void RemoveTracksNotIn_touches_only_the_given_playlist()
    {
        var cache = new YouTubeTrackCache(_dir);
        string first = CreateFile("PL1", "videoA");
        string second = CreateFile("PL2", "videoB");
        cache.Register("PL1", "videoA", first, DateTime.UtcNow);
        cache.Register("PL2", "videoB", second, DateTime.UtcNow);

        IReadOnlyList<string> removed = cache.RemoveTracksNotIn("PL1", new HashSet<string> { "videoC" });

        Assert.Equal([first], removed);
        Assert.False(File.Exists(first));
        Assert.Equal("videoB", cache.PeekVideoId("PL2"));
        // La otra playlist conserva su pista en el manifiesto (el archivo lo borra
        // la purga de huérfanos preexistente de Register, ajena a este borrado).
        Assert.Contains("PL2", File.ReadAllText(Path.Combine(_dir, "manifest.json")));
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
    public void RemovePlaylistsNotIn_removes_an_unreferenced_playlist_and_keeps_the_referenced_one()
    {
        var cache = new YouTubeTrackCache(_dir);
        string used = CreateFile("PLused", "videoA");
        string abandoned = CreateFile("PLgone", "videoB");
        cache.Register("PLused", "videoA", used, DateTime.UtcNow);
        cache.Register("PLgone", "videoB", abandoned, DateTime.UtcNow.AddMinutes(1));

        IReadOnlyList<string> removed = cache.RemovePlaylistsNotIn(new HashSet<string> { "PLused" });

        Assert.Equal([abandoned], removed);
        Assert.False(File.Exists(abandoned));
        Assert.Null(cache.PeekVideoId("PLgone"));
        Assert.True(File.Exists(used));
        Assert.Equal("videoA", cache.PeekVideoId("PLused"));
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
        Assert.Equal("videoA", cache.PeekVideoId("PL1"));
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
        string file = CreateFile("PLgone", "videoB");
        cache.Register("PLgone", "videoB", file, DateTime.UtcNow);

        cache.RemovePlaylistsNotIn(new HashSet<string> { "PLused" });

        // Al reiniciar, la carpeta ya no tiene nada que recuperar: el manifiesto
        // también olvidó la playlist.
        var restarted = new YouTubeTrackCache(_dir);
        restarted.LoadExisting();

        Assert.False(restarted.IsFresh("PLgone", TimeSpan.FromHours(6), DateTime.UtcNow));
        Assert.Null(restarted.PeekVideoId("PLgone"));
        Assert.DoesNotContain("PLgone", File.ReadAllText(Path.Combine(_dir, "manifest.json")));
    }

    [Fact]
    public void RemovePlaylistsNotIn_frees_the_budget_so_an_orphan_stops_evicting_a_needed_track()
    {
        var cache = new YouTubeTrackCache(_dir);
        var neededFiles = new List<string>();

        // La caché arranca con una playlist que ya no usa ninguna alarma.
        string abandoned = CreateFile("PLgone", "videoB");
        cache.Register("PLgone", "videoB", abandoned, DateTime.UtcNow);

        // Al barrerla, su plaza deja de estar ocupada: el índice en memoria baja.
        cache.RemovePlaylistsNotIn(new HashSet<string> { "PLused", "PLused2", "PLused3" });
        Assert.Equal(0, cache.GetBufferState(YouTubeTrackCache.MaxTracks).Ready);

        // Con el hueco libre, las playlists en uso caben enteras sin que la
        // abandonada desplace a la más antigua de las necesarias.
        DateTime when = DateTime.UtcNow.AddMinutes(1);
        foreach (string id in new[] { "PLused", "PLused2", "PLused3" })
        {
            string file = CreateFile(id, "video" + id);
            neededFiles.Add(file);
            cache.Register(id, "video" + id, file, when);
            when = when.AddMinutes(1);
        }

        foreach (string file in neededFiles)
        {
            Assert.True(File.Exists(file));
        }

        Assert.Equal(YouTubeTrackCache.MaxTracks, cache.GetBufferState(YouTubeTrackCache.MaxTracks).Ready);
        Assert.False(File.Exists(abandoned));
    }
}
