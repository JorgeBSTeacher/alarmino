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

    /// <summary>
    /// Anota que una pista empezó a reproducirse de verdad (para la ventana
    /// antirrepetición). Es "best-effort": nunca lanza. Con <paramref name="videoId"/>
    /// nulo se usa el último id resuelto (<see cref="YouTubeAudioResolver.LastVideoId"/>).
    /// </summary>
    void RecordPlay(string? playlistUrl, string? videoId = null);
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

    /// <summary>Tiempo máximo para la descarga en vivo (cuando suena la alarma y no hay
    /// pista precargada). Sin él, una red colgada dejaría la alarma en silencio.</summary>
    private static readonly TimeSpan LiveDownloadTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Ventana por defecto sin repetir una pista (días) si no se configura otra.</summary>
    private const int DefaultNoRepeatDays = 7;

    private readonly YouTubeTrackCache _cache;
    private readonly IConnectivityProbe _connectivity;
    private readonly YoutubeClient _youtube = new();
    private readonly PlayHistoryStore _history;
    private readonly SemaphoreSlim _prefetchGate = new(1, 1);
    private readonly object _attemptsGate = new();
    private readonly Dictionary<string, DateTime> _lastPrefetchUtc = new(StringComparer.Ordinal);

    private int _noRepeatDays = DefaultNoRepeatDays;
    private int _targetBufferCount = YouTubeTrackCache.MaxTracks;

    /// <summary>Id del último vídeo reproducido, para no repetirlo consecutivamente.</summary>
    public string? LastVideoId { get; private set; }

    public YouTubeAudioResolver(
        string? cacheDir = null,
        IConnectivityProbe? connectivity = null,
        YouTubeTrackCache? cache = null,
        PlayHistoryStore? history = null)
    {
        _cache = cache ?? new YouTubeTrackCache(cacheDir ?? DefaultCacheDir());
        _connectivity = connectivity ?? new ConnectivityProbe();
        _history = history ?? new PlayHistoryStore(AppDataDir());

        // Las pistas ya descargadas en sesiones anteriores sirven tal cual.
        _cache.LoadExisting();
    }

    /// <summary>
    /// Días durante los que una pista ya reproducida no se vuelve a elegir. Se
    /// recorta a un mínimo de 0 (0 = sin ventana antirrepetición).
    /// </summary>
    public int NoRepeatDays
    {
        get
        {
            lock (_attemptsGate)
            {
                return _noRepeatDays;
            }
        }

        set
        {
            lock (_attemptsGate)
            {
                _noRepeatDays = Math.Max(0, value);
            }
        }
    }

    /// <summary>
    /// Pistas sin consumir que se intentan mantener listas para el próximo
    /// arranque. Se recorta al máximo que la caché puede guardar.
    /// </summary>
    public int TargetBufferCount
    {
        get
        {
            lock (_attemptsGate)
            {
                return _targetBufferCount;
            }
        }

        set
        {
            lock (_attemptsGate)
            {
                _targetBufferCount = Math.Clamp(value, 0, YouTubeTrackCache.MaxTracks);
            }
        }
    }

    /// <summary>Historial de reproducción usado para la ventana antirrepetición.</summary>
    public PlayHistoryStore History => _history;

    /// <summary>
    /// Registra que una pista ha sonado. Lo llama el servicio de audio cuando el
    /// archivo empieza a reproducirse de verdad: es el punto más exacto (una pista
    /// descargada pero cancelada antes de sonar no cuenta).
    /// </summary>
    public void RecordPlay(string? playlistUrl, string? videoId = null)
    {
        videoId ??= LastVideoId;
        if (string.IsNullOrWhiteSpace(videoId))
        {
            return;
        }

        string? playlistId = ExtractPlaylistId(playlistUrl ?? string.Empty);
        if (playlistId is null)
        {
            return;
        }

        _history.Record(playlistId, videoId, DateTime.UtcNow);
        AppendLog($"reproducción registrada ({playlistId}, {Describe(videoId)}).");
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
            AppendLog($"pista precargada elegida ({Describe(preloadedId)}): {preloaded}");
            QueueRefill(playlistUrl);
            return preloaded;
        }

        if (!await _connectivity.IsOnlineAsync())
        {
            AppendLog("sin conexión; se usa el MP3 de respaldo.");
            return fallbackFilePath;
        }

        try
        {
            // Descarga en vivo con tiempo máximo: una red colgada no debe dejar
            // la alarma en silencio; el tiempo agotado cae al respaldo.
            var (videoId, file) = await WithLiveTimeoutAsync(async ct =>
            {
                string id = await PickRandomVideoIdAsync(playlistId, [LastVideoId], ct);
                AppendLog($"pista elegida para descarga en vivo ({Describe(id)}).");
                string path = await DownloadAudioAsync(playlistId, id, ct, consumed: true);
                return (id, path);
            }, LiveDownloadTimeout, cancellationToken);

            LastVideoId = videoId;
            MarkPrefetchAttempt(playlistId);
            AppendLog($"descarga en vivo completada: {file}");
            return file;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TimeoutException ex)
        {
            AppendLog($"la descarga en vivo superó el tiempo máximo ({ex.Message}); se usa el respaldo.");
            return fallbackFilePath;
        }
        catch (Exception ex)
        {
            AppendLog($"la descarga en vivo falló ({ex.Message}); se usa el respaldo.");
            return fallbackFilePath;
        }
    }

    /// <summary>
    /// Ejecuta una acción de descarga ligada a un tiempo máximo. Si se agota,
    /// lanza <see cref="TimeoutException"/> (para que el llamador caiga al
    /// respaldo) sin tocar el token del usuario, que queda reservado al stop.
    /// </summary>
    public static async Task<T> WithLiveTimeoutAsync<T>(
        Func<CancellationToken, Task<T>> action, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            return await action(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeoutCts.IsCancellationRequested)
        {
            throw new TimeoutException($"La descarga superó el tiempo máximo de {timeout.TotalSeconds:0} s.");
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
            AppendLog("precarga omitida: la playlist está vacía.");
            return;
        }

        string? playlistId = ExtractPlaylistId(playlistUrl);
        if (playlistId is null)
        {
            AppendLog("precarga omitida: URL de playlist no válida.");
            return;
        }

        if (_cache.IsFresh(playlistId, MaxTrackAge, DateTime.UtcNow))
        {
            AppendLog($"precarga omitida ({playlistId}): ya hay una pista reciente.");
            AppendLogBufferState(playlistId);
            return;
        }

        if (!force && !IsPrefetchDue(playlistId))
        {
            AppendLog($"precarga omitida ({playlistId}): aún no toca (ventana de 30 min).");
            AppendLogBufferState(playlistId);
            return;
        }

        // Como mucho una precarga a la vez (no solapar descargas).
        if (!await _prefetchGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            AppendLog($"precarga omitida ({playlistId}): ya hay otra en curso.");
            return;
        }

        try
        {
            if (_cache.IsFresh(playlistId, MaxTrackAge, DateTime.UtcNow))
            {
                AppendLog($"precarga omitida ({playlistId}): apareció una pista fresca.");
                AppendLogBufferState(playlistId);
                return;
            }

            // Sin conexión no se consume el intento: se pospone y el próximo
            // barrido lo reintenta, en lugar de quemar la ventana de 30 minutos.
            if (!await _connectivity.IsOnlineAsync().ConfigureAwait(false))
            {
                AppendLog($"precarga pospuesta ({playlistId}): sin conexión; no se consume el intento.");
                return;
            }

            MarkPrefetchAttempt(playlistId);
            AppendLog($"precarga iniciada ({playlistId}).");
            AppendLogBufferState(playlistId);

            // Una pista por playlist: la caché guarda como mucho una por lista,
            // así que el búfer se llena sumando playlists, no repitiendo la misma.
            if (_cache.IsFresh(playlistId, MaxTrackAge, DateTime.UtcNow))
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
            AppendLog($"precarga completada ({playlistId}, {Describe(videoId)}).");
            AppendLogBufferState(playlistId);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppendLog($"precarga fallida ({playlistId}): {ex.Message}");
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

        var excluded = new HashSet<string>(avoid.Where(id => id is not null).Select(id => id!), StringComparer.Ordinal);

        // La ventana antirrepetición se salta solo si no queda ninguna otra pista:
        // entonces se repite a propósito y queda anotado en el log.
        var recent = _history.InWindow(playlistId, TimeSpan.FromDays(NoRepeatDays), DateTime.UtcNow);
        if (recent.Count > 0)
        {
            var window = videos.Where(v => !recent.Contains(v.Id.Value)).ToList();
            if (window.Count > 0)
            {
                excluded.UnionWith(recent);
            }
            else
            {
                AppendLog($"ventana antirrepetición agotada ({playlistId}): no quedan pistas fuera de los últimos {NoRepeatDays} días; se repite.");
            }
        }

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
            AppendLog($"descarga descartada ({Describe(videoId)}): llegó incompleta o corrupta.");
            throw new InvalidDataException("La descarga de YouTube llegó incompleta o corrupta.");
        }

        _cache.Register(playlistId, videoId, dest, DateTime.UtcNow, consumed);
        AppendLog($"descarga completada ({Describe(videoId)}): {dest}");
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

    private static string AppDataDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Alarmino");

    private static string DefaultCacheDir() => Path.Combine(AppDataDir(), "cache", "youtube");

    /// <summary>Anota el estado del búfer (listas frente al objetivo) en el log.</summary>
    private void AppendLogBufferState(string? playlistId)
    {
        BufferState state = _cache.GetBufferState(TargetBufferCount);
        AppendLog($"búfer ({playlistId}): {state.Ready}/{state.Target} pistas listas.");
    }

    /// <summary>Identificador legible de una pista para el log.</summary>
    private static string Describe(string? videoId) => string.IsNullOrWhiteSpace(videoId) ? "sin id" : videoId;

    private static void AppendLog(string message) => OperationLog.Write("youtube", message);

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
