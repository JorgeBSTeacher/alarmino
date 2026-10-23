using System.Text.RegularExpressions;
using YoutubeExplode;
using YoutubeExplode.Common;
using YoutubeExplode.Playlists;
using YoutubeExplode.Videos.Streams;

namespace Alarmino.Core.Services;

/// <summary>
/// Foto de una playlist remota en un momento dado: los ids de vídeo leídos y el
/// total de vídeos que YouTube declara para la lista. El total es lo que permite
/// distinguir una lista leída entera (los dos números coinciden) de una lectura
/// truncada (se leyeron menos de los declarados): solo la primera autoriza a
/// borrar pistas locales.
/// </summary>
/// <param name="VideoIds">Ids de los vídeos leídos, en el orden de YouTube.</param>
/// <param name="DeclaredCount">Total de vídeos declarado por YouTube; 0 si no lo declara.</param>
public readonly record struct PlaylistSnapshot(IReadOnlyList<string> VideoIds, int DeclaredCount);

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
/// mantiene descargada la playlist completa y elige una pista aleatoria de las
/// ya descargadas, evitando las reproducidas dentro de la ventana antirrepetición.
/// Al sonar no se espera ninguna descarga; solo si la playlist no tiene ninguna
/// pista descargada se descarga en el momento. Si no hay conexión o algo falla,
/// devuelve el archivo de respaldo configurado.
/// </summary>
public interface IYouTubeAudioResolver
{
    /// <summary>
    /// Devuelve la ruta local a reproducir (pista precargada/descargada o
    /// respaldo local). Null significa que no hay nada que sonar.
    /// </summary>
    Task<string?> ResolveAsync(string? playlistUrl, string? fallbackFilePath, CancellationToken cancellationToken);

    /// <summary>
    /// Descarga en segundo plano toda la playlist para que la próxima alarma con
    /// ese sonido empiece a su hora. Solo descarga las pistas que falten: una
    /// pista ya descargada no se vuelve a descargar. No hace nada si no hay
    /// conexión. Con <paramref name="force"/> se salta la ventana de 30 minutos
    /// entre intentos: lo usa el arranque, donde una caché vacía debe empezar a
    /// descargar ya en lugar de esperar al próximo barrido.
    /// </summary>
    Task PrefetchAsync(string? playlistUrl, CancellationToken cancellationToken = default, bool force = false);

    /// <summary>
    /// Sincroniza en segundo plano la caché de una playlist con la lista remota:
    /// descarga todas las pistas que falten y borra las que ya no están en la
    /// playlist. Si la consulta a la playlist falla, se agota el tiempo, se
    /// cancela o devuelve una lista vacía, la caché queda intacta. Nunca lanza.
    /// </summary>
    Task PrefetchAndSyncAsync(string? playlistUrl, CancellationToken cancellationToken = default);

    /// <summary>
    /// Barre de la caché las playlists que ya no usa ninguna alarma: el usuario
    /// cambió una alarma de la playlist A a la B y A seguía ocupando espacio.
    /// Es un barrido puramente local (solo compara ids de playlist, sin tocar la
    /// red), así que no se confunde con un fallo de la consulta de una playlist
    /// que sigue en uso: esa conserva sus pistas. Un conjunto vacío no borra
    /// nada. Nunca lanza.
    /// </summary>
    void RemovePlaylistsNotIn(IReadOnlySet<string> allowedPlaylistIds);

    /// <summary>
    /// Comprueba al arrancar cuántas pistas descargadas tiene cada playlist
    /// configurada. Devuelve un veredicto por playlist y registra una línea por
    /// playlist. Es una comprobación solo de disco: nunca espera a la red y nunca
    /// lanza, así que no puede retrasar el arranque. La descarga forzosa de las
    /// playlists sin nada descargado la decide quien llama (con <c>force: true</c>).
    /// </summary>
    Task<IReadOnlyList<TrackReadiness>> CheckBootReadinessAsync(
        IEnumerable<string?> playlistUrls,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// True si ya toca volver a intentar la precarga de esa playlist (la ventana
    /// de 30 minutos ha pasado). Se expone para diagnóstico y pruebas.
    /// </summary>
    bool IsPrefetchDue(string playlistId);

    /// <summary>
    /// Anota que una pista empezó a reproducirse de verdad (para la ventana
    /// antirrepetición). Es "best-effort": nunca lanza. Con <paramref name="videoId"/>
    /// nulo se usa el último id resuelto (<see cref="YouTubeAudioResolver.LastVideoId"/>).
    /// </summary>
    void RecordPlay(string? playlistUrl, string? videoId = null);

    /// <summary>
    /// Cuántas pistas hay descargadas en total (de todas las playlists). Si es
    /// cero, la alarma tendrá que descargar en el momento (o usar el MP3 de
    /// respaldo).
    /// </summary>
    int DownloadedTrackCount { get; }

    /// <summary>
    /// Detalle de las pistas guardadas en caché, para el panel de diagnóstico.
    /// Es solo de disco: no toca la red.
    /// </summary>
    IReadOnlyList<CachedTrackInfo> DescribeCachedTracks();

    /// <summary>
    /// Comprueba si hay conexión con YouTube. El resultado se cachea unos
    /// segundos para no repetir la petición.
    /// </summary>
    Task<bool> IsOnlineAsync();
}

/// <summary>Implementación de <see cref="IYouTubeAudioResolver"/> con YoutubeExplode.</summary>
public sealed class YouTubeAudioResolver : IYouTubeAudioResolver, IDisposable
{
    private static readonly string[] PlayableContainers = ["mp4", "m4a", "mp3", "aac", "wav"];
    /// <summary>
    /// Tope de vídeos que se enumeran de una playlist (tanto al elegir una pista
    /// como al leer la lista para sincronizar). No es una señal de completitud: para
    /// saber si la lectura quedó entera se compara lo leído con el total que
    /// declara YouTube (<see cref="PlaylistSnapshot.DeclaredCount"/>).
    /// </summary>
    public const int MaxPlaylistItems = 25;
    /// <summary>Tiempo mínimo entre dos descargas de precarga de la misma playlist.</summary>
    private static readonly TimeSpan MinPrefetchInterval = TimeSpan.FromMinutes(30);

    /// <summary>Tiempo máximo para la descarga en vivo (cuando suena la alarma y no hay
    /// pista descargada). Sin él, una red colgada dejaría la alarma en silencio.</summary>
    private static readonly TimeSpan LiveDownloadTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Ventana por defecto sin repetir una pista (días) si no se configura otra.</summary>
    private const int DefaultNoRepeatDays = 10;

    private readonly YouTubeTrackCache _cache;
    private readonly IConnectivityProbe _connectivity;
    private readonly YoutubeClient _youtube = new();
    private readonly PlayHistoryStore _history;
    private readonly Func<string, CancellationToken, Task<PlaylistSnapshot>> _playlistVideoIds;
    private readonly Func<string, string, CancellationToken, Task<string>> _downloadTrack;
    private readonly SemaphoreSlim _prefetchGate = new(1, 1);
    private readonly object _attemptsGate = new();
    private readonly Dictionary<string, DateTime> _lastPrefetchUtc = new(StringComparer.Ordinal);

    private int _noRepeatDays = DefaultNoRepeatDays;

    /// <summary>Id del último vídeo reproducido, para no repetirlo consecutivamente.</summary>
    public string? LastVideoId { get; private set; }

    /// <summary>
    /// Se dispara al empezar (<c>true</c>) y al terminar (<c>false</c>) cada descarga
    /// de audio, tanto la precarga en segundo plano como la descarga en vivo. Es la
    /// señal que usa la ventana principal para mostrar «Descargando…». Se dispara
    /// desde el hilo que descarga, así que quien escuche debe saltar a su propio
    /// hilo de interfaz.
    /// </summary>
    public event Action<bool>? DownloadActivityChanged;

    /// <summary>
    /// Hay al menos una descarga de audio en curso. Es el respaldo del evento para
    /// quien prefiera consultar el estado en cada refresco en lugar de suscribirse.
    /// </summary>
    public bool IsDownloading => Volatile.Read(ref _activeDownloads) > 0;

    private int _activeDownloads;

    public YouTubeAudioResolver(
        string? cacheDir = null,
        IConnectivityProbe? connectivity = null,
        YouTubeTrackCache? cache = null,
        PlayHistoryStore? history = null,
        Func<string, CancellationToken, Task<PlaylistSnapshot>>? playlistVideoIds = null,
        Func<string, string, CancellationToken, Task<string>>? downloadTrack = null)
    {
        _cache = cache ?? new YouTubeTrackCache(cacheDir ?? DefaultCacheDir());
        _connectivity = connectivity ?? new ConnectivityProbe();
        _history = history ?? new PlayHistoryStore(AppDataDir());
        _playlistVideoIds = playlistVideoIds ?? FetchPlaylistVideoIdsAsync;
        _downloadTrack = downloadTrack ?? DownloadTrackFileAsync;

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

    /// <summary>Historial de reproducción usado para la ventana antirrepetición.</summary>
    public PlayHistoryStore History => _history;

    /// <summary>Pistas descargadas y presentes en disco, de todas las playlists.</summary>
    public int DownloadedTrackCount => _cache.TotalDownloadedCount;

    /// <summary>Detalle de las pistas guardadas en caché (panel de diagnóstico).</summary>
    public IReadOnlyList<CachedTrackInfo> DescribeCachedTracks() => _cache.DescribeEntries();

    /// <summary>Comprueba si hay conexión con YouTube.</summary>
    public Task<bool> IsOnlineAsync() => _connectivity.IsOnlineAsync();

    /// <summary>
    /// Sonda de conectividad usada por el panel de diagnóstico. Se expone para
    /// que la vista pueda vigilar la red sin tener su propia comprobación.
    /// </summary>
    public IConnectivityProbe Connectivity => _connectivity;

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

        // Camino rápido: se elige al azar una pista ya descargada, sin tocar la
        // red. Se excluyen las reproducidas dentro de la ventana antirrepetición
        // y la última pista, para no repetir mientras haya otra disponible.
        DateTime now = DateTime.UtcNow;
        var excluded = new HashSet<string>(
            _history.InWindow(playlistId, TimeSpan.FromDays(NoRepeatDays), now),
            StringComparer.Ordinal);
        if (LastVideoId is not null)
        {
            excluded.Add(LastVideoId);
        }

        if (_cache.TryTakeRandom(playlistId, excluded, out string? downloaded, out string? downloadedId))
        {
            LastVideoId = downloadedId;
            AppendLog($"pista descargada elegida ({Describe(downloadedId)}): {downloaded}");
            return downloaded;
        }

        // Todas las descargadas están dentro de la ventana: se reintenta sin
        // exclusiones y se repite a propósito. Si no hay ninguna descargada no
        // se habla de ventana agotada: se pasa directo a la descarga en vivo.
        if (_cache.GetDownloadedCount(playlistId) > 0 &&
            _cache.TryTakeRandom(playlistId, new HashSet<string>(), out downloaded, out downloadedId))
        {
            LastVideoId = downloadedId;
            AppendLog(
                $"ventana antirrepetición agotada ({playlistId}): no quedan pistas fuera de los últimos " +
                $"{NoRepeatDays} días; se repite {Describe(downloadedId)}.");
            return downloaded;
        }

        AppendLog($"no hay pistas descargadas ({playlistId}); se intenta descargar en vivo.");

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

    public async Task PrefetchAndSyncAsync(string? playlistUrl, CancellationToken cancellationToken = default)
    {
        await PrefetchAndSyncCoreAsync(playlistUrl, cancellationToken).ConfigureAwait(false);
    }

    public async Task PrefetchAsync(string? playlistUrl, CancellationToken cancellationToken = default, bool force = false)
    {
        await PrefetchCoreAsync(playlistUrl, force, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Comprueba el estado de descarga de cada playlist al arrancar. La
    /// comprobación es solo de disco (no toca la red, no bloquea el arranque):
    /// lee el búfer de cada playlist y registra el veredicto. No lanza la descarga
    /// por sí misma; quien llama decide forzarla con <c>force: true</c> para las
    /// playlists sin nada listo, de modo que la ventana de 30 minutos no deje el
    /// arranque con la caché vacía esperando al siguiente barrido.
    /// </summary>
    public Task<IReadOnlyList<TrackReadiness>> CheckBootReadinessAsync(
        IEnumerable<string?> playlistUrls,
        CancellationToken cancellationToken = default)
    {
        var verdicts = new List<TrackReadiness>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (string? url in playlistUrls)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string? playlistId = ExtractPlaylistId(url ?? string.Empty);
            if (playlistId is null || !seen.Add(playlistId))
            {
                continue;
            }

            // Solo se mira el disco: cuántas pistas descargadas tiene esta playlist.
            int downloaded = _cache.GetDownloadedCount(playlistId);
            var readiness = new TrackReadiness(playlistId, downloaded);
            verdicts.Add(readiness);
            AppendLog(BootReadiness.Describe(readiness));
        }

        return Task.FromResult<IReadOnlyList<TrackReadiness>>(verdicts);
    }

    /// <summary>True si ya toca volver a intentar la precarga de esa playlist.</summary>
    public bool IsPrefetchDue(string playlistId) => IsPrefetchDueCore(playlistId);

    /// <summary>
    /// Barre de la caché las playlists que no están en <paramref name="allowedPlaylistIds"/>.
    /// No consulta la red: es una poda local de lo que ya hay en disco, ajena a los
    /// datos remotos (y por tanto incapaz de borrar por un fallo de consulta). No
    /// lanza: un fallo del barrido deja la caché intacta.
    /// </summary>
    public void RemovePlaylistsNotIn(IReadOnlySet<string> allowedPlaylistIds)
    {
        try
        {
            _cache.RemovePlaylistsNotIn(allowedPlaylistIds);
        }
        catch (Exception ex)
        {
            AppendLog($"barrido de playlists abandonadas fallido: {ex.Message}");
        }
    }

    /// <summary>
    /// Sincroniza la caché de una playlist con la lista remota, en segundo plano:
    /// primero descarga todas las pistas que falten y después borra las que ya no
    /// están en la playlist. Nunca lanza: los fallos de la descarga remota dejan
    /// la caché intacta.
    /// </summary>
    private async Task PrefetchAndSyncCoreAsync(string? playlistUrl, CancellationToken cancellationToken)
    {
        // La caché y el historial se indexan por id de playlist, no por URL: hay
        // que extraerlo aquí o el barrido buscaría una playlist con la URL como
        // nombre y no encontraría nada que borrar.
        string? playlistId = ExtractPlaylistId(playlistUrl ?? string.Empty);
        if (playlistId is null)
        {
            return;
        }

        // Sin conexión no hay lista remota fiable: no se descarga ni se borra nada.
        if (!await _connectivity.IsOnlineAsync().ConfigureAwait(false))
        {
            AppendLog($"sincronización pospuesta ({playlistId}): sin conexión; no se toca nada.");
            return;
        }

        // Bajo el mismo pestillo que la precarga: este barrido incluye una
        // descarga y no debe solaparse con otra (ni con una precarga en curso).
        if (!await _prefetchGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            AppendLog($"sincronización omitida ({playlistId}): ya hay otra descarga o barrido en curso.");
            return;
        }

        try
        {
            // El relleno ya está bajo el pestillo: se llama a la variante sin
            // pestillo, porque volver a pedirlo aquí se bloquearía a sí mismo.
            await PrefetchCoreLockedAsync(playlistId, CancellationToken.None).ConfigureAwait(false);
            await PropagateDeletionsAsync(playlistId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppendLog($"sincronización fallida ({playlistUrl}): {ex.Message}");
        }
        finally
        {
            _prefetchGate.Release();
        }
    }

    /// <summary>
    /// Borra de la caché las pistas que ya no están en la lista remota. Solo borra
    /// cuando la lectura es completa de verdad: se leyeron al menos tantos vídeos
    /// como declara YouTube y ese total es mayor que cero. Si la consulta falla, se
    /// agota el tiempo, se cancela, llega vacía o llega truncada, no se toca nada:
    /// un fallo de red no puede vaciar la caché del usuario.
    /// </summary>
    private async Task PropagateDeletionsAsync(string playlistId, CancellationToken cancellationToken)
    {
        PlaylistSnapshot remote;
        try
        {
            remote = await _playlistVideoIds(playlistId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            AppendLog($"sincronización pospuesta ({playlistId}): la consulta se canceló; no se borra nada.");
            return;
        }
        catch (Exception ex)
        {
            AppendLog($"sincronización pospuesta ({playlistId}): la consulta falló ({ex.Message}); no se borra nada.");
            return;
        }

        // Una lista completa nunca llega vacía; si llega, la consulta se quedó a
        // medias. Tratarla como «la playlist no tiene nada» borraría la caché:
        // se corta antes de tocar nada (la caché también se negaría por su cuenta).
        if (remote.VideoIds.Count == 0)
        {
            AppendLog($"sincronización pospuesta ({playlistId}): la lista remota llegó vacía; no se borra nada.");
            return;
        }

        // Sin total declarado no se sabe si falta algo por leer: sin esa certeza no
        // se borra. Los vídeos mezclados («mixes») no declaran total.
        if (remote.DeclaredCount <= 0)
        {
            AppendLog(
                $"sincronización pospuesta ({playlistId}): YouTube no declara el total de la lista; " +
                "no se puede comprobar si está completa; no se borra nada.");
            return;
        }

        // El total declarado supera el tope de lectura: la lista es más grande de lo
        // que se puede enumerar entera, así que siempre faltarían vídeos por leer.
        if (remote.DeclaredCount > MaxPlaylistItems)
        {
            AppendLog(
                $"sincronización pospuesta ({playlistId}): la lista declara {remote.DeclaredCount} vídeo(s), " +
                $"por encima del límite de {MaxPlaylistItems} que se puede leer; no se borra nada.");
            return;
        }

        // Se leyó menos de lo que YouTube declara: respuesta incompleta, no un borrado.
        if (remote.VideoIds.Count < remote.DeclaredCount)
        {
            AppendLog(
                $"sincronización pospuesta ({playlistId}): se leyeron {remote.VideoIds.Count} de " +
                $"{remote.DeclaredCount} vídeo(s) declarados; lectura incompleta; no se borra nada.");
            return;
        }

        IReadOnlyList<string> removed = _cache.RemoveTracksNotIn(
            playlistId,
            new HashSet<string>(remote.VideoIds, StringComparer.Ordinal));
        if (removed.Count > 0)
        {
            AppendLog($"sincronización completada ({playlistId}): {removed.Count} pista(s) borrada(s) por ya no estar en la playlist.");
        }
        else
        {
            AppendLog($"sincronización completada ({playlistId}): nada que borrar.");
        }
    }

    /// <summary>
    /// Foto de la playlist remota: los ids de los vídeos leídos (hasta
    /// <see cref="MaxPlaylistItems"/>) y el total que declara YouTube, que es lo que
    /// decide si la lectura quedó completa.
    /// </summary>
    private async Task<PlaylistSnapshot> FetchPlaylistVideoIdsAsync(string playlistId, CancellationToken cancellationToken)
    {
        Playlist playlist = await _youtube.Playlists
            .GetAsync(playlistId, cancellationToken)
            .ConfigureAwait(false);

        var videos = await _youtube.Playlists
            .GetVideosAsync(playlistId, cancellationToken)
            .CollectAsync(MaxPlaylistItems)
            .ConfigureAwait(false);

        return new PlaylistSnapshot(videos.Select(v => v.Id.Value).ToList(), playlist.Count ?? 0);
    }


    /// <summary>
    /// Lista completa de ids de la playlist remota, o <c>null</c> si la consulta
    /// falla, se agota el tiempo, se cancela o devuelve una lista vacía. Es el
    /// dato con el que un llamador decide qué borrar: <c>null</c> significa
    /// «no se sabe», nunca «no hay nada». Solo para diagnóstico (vista de logs).
    /// </summary>
    public async Task<IReadOnlyList<string>?> TryFetchPlaylistVideoIdsAsync(string? playlistUrl, CancellationToken cancellationToken = default)
    {
        string? playlistId = ExtractPlaylistId(playlistUrl ?? string.Empty);
        if (playlistId is null)
        {
            AppendLog($"consulta de playlist omitida: URL no válida ({playlistUrl}).");
            return null;
        }

        try
        {
            PlaylistSnapshot snapshot = await _playlistVideoIds(playlistId, cancellationToken).ConfigureAwait(false);
            if (snapshot.VideoIds.Count == 0)
            {
                AppendLog($"consulta de playlist ({playlistId}): llegó vacía.");
                return null;
            }

            if (snapshot.DeclaredCount > 0 && snapshot.VideoIds.Count < snapshot.DeclaredCount)
            {
                AppendLog(
                    $"consulta de playlist ({playlistId}): {snapshot.VideoIds.Count} de " +
                    $"{snapshot.DeclaredCount} vídeo(s) declarados; lectura incompleta.");
            }
            else
            {
                AppendLog($"consulta de playlist ({playlistId}): {snapshot.VideoIds.Count} vídeo(s).");
            }

            return snapshot.VideoIds;
        }
        catch (OperationCanceledException)
        {
            AppendLog($"consulta de playlist ({playlistId}): cancelada.");
            return null;
        }
        catch (Exception ex)
        {
            AppendLog($"consulta de playlist ({playlistId}): falló ({ex.Message}).");
            return null;
        }
    }

    /// <summary>
    /// Precarga la playlist completa. Toma el pestillo de descarga y respeta la
    /// ventana de 30 minutos (salvo <paramref name="force"/>). El trabajo real de
    /// descarga vive en <see cref="PrefetchCoreLockedAsync"/>, que también usa la
    /// sincronización de arranque sin volver a pedir el pestillo.
    /// </summary>
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

        if (!force && !IsPrefetchDueCore(playlistId))
        {
            AppendLog($"precarga omitida ({playlistId}): aún no toca (ventana de 30 min).");
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
            await PrefetchCoreLockedAsync(playlistId, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Rellena la caché con todas las pistas de la playlist que aún no estén
    /// descargadas. Debe llamarse con el pestillo de descarga ya tomado. Descarga
    /// en secuencia, no vuelve a bajar una pista existente y sigue con las demás
    /// si una falla. No borra nada: eso es cosa de la sincronización remota.
    /// </summary>
    private async Task PrefetchCoreLockedAsync(string playlistId, CancellationToken cancellationToken)
    {
        // Sin conexión no se consume el intento: se pospone y el próximo
        // barrido lo reintenta, en lugar de quemar la ventana de 30 minutos.
        if (!await _connectivity.IsOnlineAsync().ConfigureAwait(false))
        {
            AppendLog($"precarga pospuesta ({playlistId}): sin conexión; no se consume el intento.");
            return;
        }

        // El intento se anota antes de consultar la lista: un fallo de la
        // consulta también respeta la ventana y no martillea la red.
        MarkPrefetchAttempt(playlistId);

        PlaylistSnapshot remote;
        try
        {
            remote = await _playlistVideoIds(playlistId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppendLog($"precarga pospuesta ({playlistId}): la consulta falló ({ex.Message}).");
            return;
        }

        int total = remote.VideoIds.Count;
        if (total == 0)
        {
            AppendLog($"precarga omitida ({playlistId}): la lista remota llegó vacía.");
            return;
        }

        var downloaded = new HashSet<string>(_cache.GetVideoIds(playlistId), StringComparer.Ordinal);
        var missing = remote.VideoIds
            .Where(id => !downloaded.Contains(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        int alreadyDownloaded = total - missing.Count;
        if (missing.Count == 0)
        {
            AppendLog($"precarga omitida ({playlistId}): ya están descargadas las {total} pista(s).");
            return;
        }

        AppendLog($"precarga iniciada ({playlistId}): {total} pista(s), faltan {missing.Count} por descargar.");

        int downloadedNow = 0;
        int failed = 0;

        foreach (string videoId in missing)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await DownloadAudioAsync(playlistId, videoId, cancellationToken).ConfigureAwait(false);
                downloadedNow++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Una pista rota no puede abortar el resto de la playlist.
                failed++;
                AppendLog($"fallo al descargar ({playlistId}, {Describe(videoId)}): {ex.Message}");
            }

            int done = alreadyDownloaded + downloadedNow;
            int remaining = Math.Max(0, total - done);
            AppendLog($"descarga de playlist {playlistId}: {done}/{total}, faltan {remaining}");
        }

        AppendLog(
            $"precarga completada ({playlistId}): {downloadedNow} descargada(s), {failed} fallida(s) de {total}.");
    }

    private bool IsPrefetchDueCore(string playlistId)
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
        // La misma lectura remota que usa la sincronización: así hay un único
        // punto (inyectable) que consulta la lista de vídeos de una playlist.
        PlaylistSnapshot snapshot = await _playlistVideoIds(playlistId, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<string> videos = snapshot.VideoIds;

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
            var window = videos.Where(id => !recent.Contains(id)).ToList();
            if (window.Count > 0)
            {
                excluded.UnionWith(recent);
            }
            else
            {
                AppendLog($"ventana antirrepetición agotada ({playlistId}): no quedan pistas fuera de los últimos {NoRepeatDays} días; se repite.");
            }
        }

        var candidates = videos.Where(id => !excluded.Contains(id)).ToList();
        if (candidates.Count == 0)
        {
            candidates = videos.ToList();
        }

        return candidates[Random.Shared.Next(candidates.Count)];
    }

    /// <summary>
    /// Descarga una pista (a través de la costura inyectable) y la registra en la
    /// caché. Es el envoltorio que avisa de la actividad de descarga.
    /// </summary>
    private async Task<string> DownloadAudioAsync(string playlistId, string videoId, CancellationToken cancellationToken, bool consumed = false)
    {
        BeginDownload();
        try
        {
            string path = await _downloadTrack(playlistId, videoId, cancellationToken).ConfigureAwait(false);
            _cache.Register(playlistId, videoId, path, DateTime.UtcNow, consumed);
            AppendLog($"descarga completada ({Describe(videoId)}): {path}");
            return path;
        }
        finally
        {
            EndDownload();
        }
    }

    /// <summary>
    /// Descarga real con YoutubeExplode y valida el archivo con
    /// <see cref="TrackSanity"/>. Devuelve la ruta del archivo descargado, sin
    /// registrarlo (de eso se encarga <see cref="DownloadAudioAsync"/>). Es la
    /// implementación por defecto de la costura de descarga, sustituible en las
    /// pruebas para no tocar la red.
    /// </summary>
    private async Task<string> DownloadTrackFileAsync(string playlistId, string videoId, CancellationToken cancellationToken)
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

    /// <summary>Identificador legible de una pista para el log.</summary>
    private static string Describe(string? videoId) => string.IsNullOrWhiteSpace(videoId) ? "sin id" : videoId;

    /// <summary>Cuenta una descarga en curso y avisa de que ha empezado.</summary>
    private void BeginDownload()
    {
        Interlocked.Increment(ref _activeDownloads);
        EmitDownloadActivity(true);
    }

    /// <summary>Descarta una descarga en curso y avisa de que ha terminado.</summary>
    private void EndDownload()
    {
        if (Interlocked.Decrement(ref _activeDownloads) <= 0)
        {
            Interlocked.Exchange(ref _activeDownloads, 0);
            EmitDownloadActivity(false);
        }
    }

    /// <summary>
    /// Avisa de la actividad de descarga sin que un fallo de quien escucha pueda
    /// romper la descarga (la alarma debe sonar por encima de la interfaz).
    /// </summary>
    private void EmitDownloadActivity(bool downloading)
    {
        try
        {
            DownloadActivityChanged?.Invoke(downloading);
        }
        catch
        {
        }
    }

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
