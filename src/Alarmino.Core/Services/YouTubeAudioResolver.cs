using System.Text.RegularExpressions;
using YoutubeExplode;
using YoutubeExplode.Common;
using YoutubeExplode.Videos.Streams;

namespace Alarmino.Core.Services;

/// <summary>Comprueba si hay conectividad con Internet (y con YouTube en particular).</summary>
public interface IConnectivityProbe
{
    Task<bool> IsOnlineAsync();
}

/// <summary>
/// Comprueba la conectividad consultando un endpoint ligero de YouTube con un
/// timeout corto. Cachea el resultado unos segundos para no penalizar las
/// repeticiones de una misma reproducción.
/// </summary>
public sealed class ConnectivityProbe : IConnectivityProbe
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(4) };
    private const string ProbeUrl = "https://www.youtube.com/generate_204";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(5);
    private readonly object _gate = new();
    private (bool Online, DateTime Utc) _cached;

    public async Task<bool> IsOnlineAsync()
    {
        lock (_gate)
        {
            if (DateTime.UtcNow - _cached.Utc < CacheDuration)
            {
                return _cached.Online;
            }
        }

        bool online = false;
        try
        {
            using var response = await Http.GetAsync(ProbeUrl, HttpCompletionOption.ResponseHeadersRead);
            online = response.IsSuccessStatusCode;
        }
        catch
        {
            online = false;
        }

        lock (_gate)
        {
            _cached = (online, DateTime.UtcNow);
        }

        return online;
    }
}

/// <summary>
/// Resuelve un sonido de tipo YouTube a un archivo local reproducible:
/// elige una pista aleatoria de la playlist, se la precarga en caché y devuelve
/// la ruta. Al sonar no se espera ninguna descarga (usa la pista precargada);
/// solo si no hay ninguna se descarga en el momento. Si no hay conexión o algo
/// falla, devuelve el archivo de respaldo configurado.
/// </summary>
public interface IYouTubeAudioResolver
{
    /// <summary>
    /// Devuelve la ruta local a reproducir (pista precargada/descargada o
    /// respaldo local). Null significa que no hay nada que sonar.
    /// </summary>
    Task<string?> ResolveAsync(string? playlistUrl, string? fallbackFilePath, CancellationToken cancellationToken);

    /// <summary>
    /// Descarga en segundo plano la pista de una playlist para que la próxima
    /// alarma con ese sonido empiece a su hora. No hace nada si ya hay una pista
    /// reciente o si no hay conexión.
    /// </summary>
    Task PrefetchAsync(string? playlistUrl, CancellationToken cancellationToken = default);
}

/// <summary>Implementación de <see cref="IYouTubeAudioResolver"/> con YoutubeExplode.</summary>
public sealed class YouTubeAudioResolver : IYouTubeAudioResolver, IDisposable
{
    private static readonly string[] PlayableContainers = ["mp4", "m4a", "mp3", "aac", "wav"];
    private const int MaxPlaylistItems = 300;

    /// <summary>Tiempo mínimo entre dos descargas de precarga de la misma playlist.</summary>
    private static readonly TimeSpan MinPrefetchInterval = TimeSpan.FromMinutes(30);

    /// <summary>Una pista más antigua que esto se vuelve a precargar.</summary>
    private static readonly TimeSpan MaxTrackAge = TimeSpan.FromHours(6);

    private readonly YouTubeTrackCache _cache;
    private readonly IConnectivityProbe _connectivity;
    private readonly YoutubeClient _youtube = new();
    private readonly SemaphoreSlim _prefetchGate = new(1, 1);
    private readonly object _attemptsGate = new();
    private readonly Dictionary<string, DateTime> _lastPrefetchUtc = new(StringComparer.Ordinal);

    /// <summary>Id del último vídeo reproducido, para no repetirlo consecutivamente.</summary>
    public string? LastVideoId { get; private set; }

    public YouTubeAudioResolver(string? cacheDir = null, IConnectivityProbe? connectivity = null, YouTubeTrackCache? cache = null)
    {
        _cache = cache ?? new YouTubeTrackCache(cacheDir ?? DefaultCacheDir());
        _connectivity = connectivity ?? new ConnectivityProbe();

        // Las pistas ya descargadas en sesiones anteriores sirven tal cual.
        _cache.LoadExisting();
    }

    public async Task<string?> ResolveAsync(string? playlistUrl, string? fallbackFilePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(playlistUrl))
        {
            return fallbackFilePath;
        }

        string? playlistId = ExtractPlaylistId(playlistUrl);
        if (playlistId is null)
        {
            AppendLog($"YouTube: URL de playlist no válida ({playlistUrl}).");
            return fallbackFilePath;
        }

        // Camino rápido: la pista ya estaba descargada, suena sin esperar nada.
        if (_cache.TryTake(playlistId, out string? preloaded, out string? preloadedId))
        {
            LastVideoId = preloadedId;
            QueueRefill(playlistUrl);
            return preloaded;
        }

        if (!await _connectivity.IsOnlineAsync())
        {
            return fallbackFilePath;
        }

        try
        {
            string videoId = await PickRandomVideoIdAsync(playlistId, [LastVideoId], cancellationToken);
            string file = await DownloadAudioAsync(playlistId, videoId, cancellationToken, consumed: true);
            LastVideoId = videoId;
            MarkPrefetchAttempt(playlistId);
            return file;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppendLog($"YouTube: {ex.Message}");
            return fallbackFilePath;
        }
    }

    public async Task PrefetchAsync(string? playlistUrl, CancellationToken cancellationToken = default)
    {
        await PrefetchCoreAsync(playlistUrl, force: false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Precarga la siguiente pista de una playlist. Se ejecuta en segundo plano
    /// tras cada reproducción, sin bloquearla y sin redimensionar nada.
    /// </summary>
    private void QueueRefill(string playlistUrl)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await PrefetchCoreAsync(playlistUrl, force: true, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // La precarga nunca debe propagar errores.
            }
        });
    }

    private async Task PrefetchCoreAsync(string? playlistUrl, bool force, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(playlistUrl))
        {
            return;
        }

        string? playlistId = ExtractPlaylistId(playlistUrl);
        if (playlistId is null)
        {
            return;
        }

        if (_cache.IsFresh(playlistId, MaxTrackAge, DateTime.UtcNow))
        {
            return;
        }

        if (!force && !IsPrefetchDue(playlistId))
        {
            return;
        }

        // Como mucho una precarga a la vez (no solapar descargas).
        if (!await _prefetchGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            MarkPrefetchAttempt(playlistId);

            if (_cache.IsFresh(playlistId, MaxTrackAge, DateTime.UtcNow))
            {
                return;
            }

            if (!await _connectivity.IsOnlineAsync().ConfigureAwait(false))
            {
                return;
            }

            var avoid = new List<string?>();
            if (LastVideoId is not null)
            {
                avoid.Add(LastVideoId);
            }

            string? cached = _cache.PeekVideoId(playlistId);
            if (cached is not null)
            {
                avoid.Add(cached);
            }

            string videoId = await PickRandomVideoIdAsync(playlistId, avoid, cancellationToken).ConfigureAwait(false);
            await DownloadAudioAsync(playlistId, videoId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppendLog($"YouTube (precarga): {ex.Message}");
        }
        finally
        {
            _prefetchGate.Release();
        }
    }

    private bool IsPrefetchDue(string playlistId)
    {
        lock (_attemptsGate)
        {
            return !_lastPrefetchUtc.TryGetValue(playlistId, out DateTime last) ||
                   DateTime.UtcNow - last >= MinPrefetchInterval;
        }
    }

    private void MarkPrefetchAttempt(string playlistId)
    {
        lock (_attemptsGate)
        {
            _lastPrefetchUtc[playlistId] = DateTime.UtcNow;
        }
    }

    private async Task<string> PickRandomVideoIdAsync(string playlistId, IReadOnlyList<string?> avoid, CancellationToken cancellationToken)
    {
        var videos = await _youtube.Playlists
            .GetVideosAsync(playlistId, cancellationToken)
            .CollectAsync(MaxPlaylistItems)
            .ConfigureAwait(false);

        if (videos.Count == 0)
        {
            throw new InvalidOperationException("La playlist está vacía.");
        }

        var excluded = new HashSet<string>(avoid.Where(id => id is not null).Select(id => id!));
        var candidates = videos.Where(v => !excluded.Contains(v.Id.Value)).ToList();
        if (candidates.Count == 0)
        {
            candidates = videos.ToList();
        }

        return candidates[Random.Shared.Next(candidates.Count)].Id.Value;
    }

    private async Task<string> DownloadAudioAsync(string playlistId, string videoId, CancellationToken cancellationToken, bool consumed = false)
    {
        var manifest = await _youtube.Videos.Streams.GetManifestAsync(videoId, cancellationToken).ConfigureAwait(false);

        // Se prefiere AAC (mp4/m4a/mp3), que Windows Media Foundation decodifica siempre.
        var streamInfo = manifest.GetAudioOnlyStreams()
            .Where(s => PlayableContainers.Contains(s.Container.Name))
            .OrderByDescending(s => s.Bitrate.BitsPerSecond)
            .FirstOrDefault();

        if (streamInfo is null)
        {
            throw new InvalidOperationException("El vídeo no ofrece audio reproducible.");
        }

        Directory.CreateDirectory(_cache.DirectoryPath);
        string dest = Path.Combine(
            _cache.DirectoryPath,
            YouTubeTrackCache.BuildFileName(playlistId, videoId, streamInfo.Container.Name));

        _cache.BeginWrite(dest);
        try
        {
            await _youtube.Videos.Streams
                .DownloadAsync(streamInfo, dest, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            // Una descarga interrumpida dejaría un archivo corrupto en la caché.
            _cache.CancelWrite(dest);
            TryDeleteFile(dest);
            throw;
        }

        // Si el archivo llega vacío, truncado o corrupto, no se registra: se
        // descarta y se cae al MP3 de respaldo en lugar de reproducir un fragmento.
        // El tamaño que anuncia el manifiesto es la referencia de integridad.
        if (!TrackSanity.LooksPlayable(dest, streamInfo.Size.Bytes))
        {
            _cache.CancelWrite(dest);
            TryDeleteFile(dest);
            throw new InvalidDataException("La descarga de YouTube llegó incompleta o corrupta.");
        }

        _cache.Register(playlistId, videoId, dest, DateTime.UtcNow, consumed);
        return dest;
    }

    /// <summary>Id de playlist de una URL de YouTube, o null si no es una playlist.</summary>
    public static string? ExtractPlaylistId(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        if (Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) &&
            (uri.Host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase) ||
             uri.Host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase)))
        {
            var match = Regex.Match(uri.Query, "(?:^|[?&])list=([^&]+)");
            if (match.Success)
            {
                return match.Groups[1].Value;
            }
        }

        return null;
    }

    public void Dispose() => _youtube.Dispose();

    private static string DefaultCacheDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Alarmino", "cache", "youtube");

    private static void AppendLog(string message) => JsonDataStore.AppendLogLine(null, message);

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
