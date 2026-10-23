using Alarmino.Core.Services;
using Xunit;

namespace Alarmino.Tests;

/// <summary>
/// Pruebas del veredicto de arranque del búfer de descargas (T11): el estado
/// cacheado de cada playlist debe traducirse a una frase clara, y un arranque
/// con la caché vacía debe disparar la descarga sin que la ventana de 30 minutos
/// se la trague.
/// </summary>
public sealed class BootReadinessTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "alarmino-boot-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private string CreatePlayableFile(string playlistId, string videoId, string extension = "m4a")
    {
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, YouTubeTrackCache.BuildFileName(playlistId, videoId, extension));
        byte[] data = new byte[40 * 1024];
        data[0] = 0xFF;
        data[1] = 0xFB;
        data[2] = 0xA0;
        File.WriteAllBytes(path, data);
        return path;
    }

    private sealed class FakeProbe(bool online) : IConnectivityProbe
    {
        public int Calls { get; private set; }

        public Task<bool> IsOnlineAsync()
        {
            Calls++;
            return Task.FromResult(online);
        }
    }

    // ---------- Helper puro de veredicto ----------

    [Fact]
    public void A_ready_playlist_is_reported_as_ready_with_its_count()
    {
        string verdict = BootReadiness.Describe(new TrackReadiness("PL123abc", 1));

        Assert.Contains("PL123abc", verdict);
        Assert.Contains("1 pista(s) descargada(s).", verdict);
    }

    [Fact]
    public void An_empty_playlist_is_reported_as_not_ready()
    {
        string verdict = BootReadiness.Describe(new TrackReadiness("PLxyz", 0));

        Assert.Contains("PLxyz", verdict);
        Assert.Contains("nada descargado (0)", verdict);
        // El veredicto distingue el caso vacío del listo de forma explícita.
        Assert.NotEqual(verdict, BootReadiness.Describe(new TrackReadiness("PLxyz", 1)));
        Assert.Contains("descarga", verdict);
    }

    [Fact]
    public void A_ready_playlist_reports_a_track_was_available()
    {
        // Una playlist se considera preparada en cuanto tiene alguna pista
        // descargada: la alarma puede sonar sin descargar.
        Assert.True(new TrackReadiness("PL1", 1).IsReady);
        Assert.False(new TrackReadiness("PL1", 0).IsReady);
    }

    // ---------- API de comprobación en el resolutor ----------

    [Fact]
    public async Task Boot_check_reports_ready_for_a_fresh_cached_playlist()
    {
        string file = CreatePlayableFile("PL123abc", "videoA");
        var cache = new YouTubeTrackCache(_dir);
        cache.Register("PL123abc", "videoA", file, DateTime.UtcNow);

        var resolver = new YouTubeAudioResolver(cache: cache, connectivity: new FakeProbe(online: false));
        IReadOnlyList<TrackReadiness> verdicts = await resolver.CheckBootReadinessAsync(
            ["https://www.youtube.com/playlist?list=PL123abc"]);

        TrackReadiness verdict = Assert.Single(verdicts);
        Assert.Equal("PL123abc", verdict.PlaylistId);
        Assert.True(verdict.IsReady);
        Assert.Equal(1, verdict.Downloaded);
    }

    [Fact]
    public async Task Boot_check_reports_empty_for_an_uncached_playlist()
    {
        var resolver = new YouTubeAudioResolver(cacheDir: _dir, connectivity: new FakeProbe(online: false));
        IReadOnlyList<TrackReadiness> verdicts = await resolver.CheckBootReadinessAsync(
            ["https://www.youtube.com/playlist?list=PLxyz"]);

        TrackReadiness verdict = Assert.Single(verdicts);
        Assert.Equal("PLxyz", verdict.PlaylistId);
        Assert.False(verdict.IsReady);
        Assert.Equal(0, verdict.Downloaded);
    }

    [Fact]
    public async Task Boot_check_reports_one_verdict_per_distinct_playlist()
    {
        var resolver = new YouTubeAudioResolver(cacheDir: _dir, connectivity: new FakeProbe(online: true));
        IReadOnlyList<TrackReadiness> verdicts = await resolver.CheckBootReadinessAsync(
        [
            "https://www.youtube.com/playlist?list=PL1",
            "https://www.youtube.com/playlist?list=PL2",
            "https://www.youtube.com/playlist?list=PL1",
        ]);

        Assert.Equal(2, verdicts.Count);
        Assert.Contains(verdicts, v => v.PlaylistId == "PL1");
        Assert.Contains(verdicts, v => v.PlaylistId == "PL2");
    }

    [Fact]
    public async Task Boot_check_ignores_urls_that_are_not_a_playlist()
    {
        var resolver = new YouTubeAudioResolver(cacheDir: _dir, connectivity: new FakeProbe(online: true));
        IReadOnlyList<TrackReadiness> verdicts = await resolver.CheckBootReadinessAsync(
            ["https://www.youtube.com/watch?v=dQw4w9WgXcQ", null, "  "]);

        Assert.Empty(verdicts);
    }

    // ---------- Regla arranque vs ventana de 30 minutos ----------

    [Fact]
    public async Task Boot_with_an_empty_cache_starts_a_download_immediately()
    {
        // El veredicto de arranque es de disco y no espera a la red, pero deja dicho
        // que no hay nada listo; el arranque fuerza entonces la descarga (force: true),
        // saltándose la ventana de 30 min que sí respeta el barrido normal.
        var probe = new FakeProbe(online: true);
        var resolver = FailingFetchResolver(probe);

        // Un barrido previo ya marcó el intento: la ventana de 30 min está corriendo.
        await resolver.PrefetchAsync("https://www.youtube.com/playlist?list=PL123abc");
        Assert.Equal(1, probe.Calls);
        Assert.False(resolver.IsPrefetchDue("PL123abc"));

        TrackReadiness verdict = Assert.Single(await resolver.CheckBootReadinessAsync(
            ["https://www.youtube.com/playlist?list=PL123abc"]));
        Assert.False(verdict.IsReady);

        // El arranque no se traga la ventana: vuelve a intentar la descarga de inmediato.
        await resolver.PrefetchAsync("https://www.youtube.com/playlist?list=PL123abc", force: true);
        Assert.Equal(2, probe.Calls);
    }

    [Fact]
    public async Task Boot_with_a_fresh_track_does_not_start_a_download()
    {
        string file = CreatePlayableFile("PL123abc", "videoA");
        var cache = new YouTubeTrackCache(_dir);
        cache.Register("PL123abc", "videoA", file, DateTime.UtcNow);

        var probe = new FakeProbe(online: true);
        var resolver = new YouTubeAudioResolver(cache: cache, connectivity: probe);

        IReadOnlyList<TrackReadiness> verdicts = await resolver.CheckBootReadinessAsync(
            ["https://www.youtube.com/playlist?list=PL123abc"]);

        // Con una pista fresca no se descarga nada: la ventana de 30 minutos se respeta.
        Assert.Equal(0, probe.Calls);
        Assert.True(Assert.Single(verdicts).IsReady);
    }

    [Fact]
    public async Task The_thirty_minute_window_still_swallows_a_normal_sweep()
    {
        // La regla de arranque no puede relajar el barrido normal: sin force, el
        // segundo intento dentro de la ventana sigue sin consultar la red.
        var probe = new FakeProbe(online: true);
        var resolver = FailingFetchResolver(probe);

        await resolver.PrefetchAsync("https://www.youtube.com/playlist?list=PL123abc");
        await resolver.PrefetchAsync("https://www.youtube.com/playlist?list=PL123abc");

        Assert.Equal(1, probe.Calls);
    }

    /// <summary>
    /// Resolutor cuya lectura remota falla: el intento queda marcado (la ventana
    /// arranca) pero la descarga no llega a ejecutarse, así que la prueba es
    /// determinista y no toca la red.
    /// </summary>
    private YouTubeAudioResolver FailingFetchResolver(IConnectivityProbe probe) =>
        new(
            cacheDir: _dir,
            connectivity: probe,
            playlistVideoIds: (_, _) => Task.FromException<PlaylistSnapshot>(
                new InvalidOperationException("playlist inaccesible en la prueba")));

    [Fact]
    public async Task Boot_never_blocks_startup_on_the_network()
    {
        // Con una pista ya descargada, el veredicto es solo de disco: no espera a
        // la red aunque la sonda nunca responda. Si el arranque esperase a la red,
        // esta prueba se colgaría (la sonda no contesta nunca).
        string file = CreatePlayableFile("PL123abc", "videoA");
        var cache = new YouTubeTrackCache(_dir);
        cache.Register("PL123abc", "videoA", file, DateTime.UtcNow);

        var resolver = new YouTubeAudioResolver(cache: cache, connectivity: new NeverProbe());

        Task<IReadOnlyList<TrackReadiness>> check =
            resolver.CheckBootReadinessAsync(["https://www.youtube.com/playlist?list=PL123abc"]);

        Assert.True(await Task.WhenAny(check, Task.Delay(TimeSpan.FromSeconds(5))) == check);
        Assert.True(Assert.Single(await check).IsReady);
    }

    [Fact]
    public async Task Forcing_the_prefetch_ignores_the_thirty_minute_window()
    {
        // El arranque con la caché vacía usa el camino forzoso: aunque un barrido
        // anterior ya marcó el intento (la ventana de 30 min está corriendo), el
        // arranque fuerza el intento y la ventana no lo bloquea.
        var probe = new FakeProbe(online: true);
        var resolver = FailingFetchResolver(probe);

        await resolver.PrefetchAsync("https://www.youtube.com/playlist?list=PL123abc");
        Assert.Equal(1, probe.Calls);
        Assert.False(resolver.IsPrefetchDue("PL123abc"));

        // force ignora la ventana: la caché vacía al arrancar no debe esperar 30 min.
        await resolver.PrefetchAsync("https://www.youtube.com/playlist?list=PL123abc", force: true);

        Assert.Equal(2, probe.Calls);
    }

    private sealed class NeverProbe : IConnectivityProbe
    {
        public async Task<bool> IsOnlineAsync()
        {
            await Task.Delay(Timeout.InfiniteTimeSpan);
            return true;
        }
    }
}
