using System.Text.Json;

namespace Alarmino.Core.Services;

/// <summary>
/// Caché de pistas de YouTube descargadas por adelantado. Guarda todas las pistas
/// de cada playlist y las conserva hasta que la sincronización con la lista
/// remota diga que ya no están. Permite que la alarma empiece a sonar a su hora
/// sin esperar una descarga.
/// </summary>
public sealed class YouTubeTrackCache
{
    private readonly string _dir;
    private readonly object _gate = new();
    private readonly Dictionary<(string PlaylistId, string VideoId), Entry> _entries = new();
    private readonly HashSet<string> _pendingWrites = new(StringComparer.OrdinalIgnoreCase);

    public YouTubeTrackCache(string dir) => _dir = dir;

    /// <summary>Carpeta donde se guardan las pistas.</summary>
    public string DirectoryPath => _dir;

    private const string Separator = "--";

    /// <summary>Archivo JSON junto a las pistas que conserva el estado entre sesiones.</summary>
    private const string ManifestFileName = "manifest.json";

    private string ManifestPath => Path.Combine(_dir, ManifestFileName);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Nombre de archivo de una pista dentro de la caché. El separador "--"
    /// (que no aparece en los ids de playlist) permite recuperar las pistas de
    /// sesiones anteriores al abrir la aplicación.
    /// </summary>
    public static string BuildFileName(string playlistId, string videoId, string extension) =>
        $"{playlistId}{Separator}{videoId}.{extension.TrimStart('.').ToLowerInvariant()}";

    /// <summary>Avisa de que se empieza a escribir un archivo (para no borrarlo por barrido).</summary>
    public void BeginWrite(string filePath)
    {
        lock (_gate)
        {
            _pendingWrites.Add(filePath);
        }
    }

    /// <summary>Descarta la marca de escritura (la descarga falló o se canceló).</summary>
    public void CancelWrite(string filePath)
    {
        lock (_gate)
        {
            _pendingWrites.Remove(filePath);
        }
    }

    /// <summary>
    /// Registra una pista recién descargada. Solo añade o actualiza esa pista
    /// concreta: las demás pistas de la misma playlist se conservan. Si la misma
    /// pista se registra con otra ruta, el archivo anterior se borra.
    /// </summary>
    public void Register(string playlistId, string videoId, string filePath, DateTime downloadedAtUtc, bool consumed = false)
    {
        var toDelete = new List<string>();
        var key = (playlistId, videoId);

        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var previous) &&
                !string.Equals(previous.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
            {
                toDelete.Add(previous.FilePath);
            }

            _pendingWrites.Remove(filePath);
            _entries[key] = new Entry(videoId, filePath, downloadedAtUtc)
            {
                Consumed = consumed,
                ExpectedBytes = TryGetFileSize(filePath),
            };
        }

        DeleteFiles(toDelete);
        SaveManifest();
    }

    /// <summary>
    /// Elige al azar una pista descargada de la playlist cuyo vídeo no esté en
    /// <paramref name="excludedVideoIds"/> y cuyo archivo siga existiendo. La
    /// marca como consumida solo para el diagnóstico y persiste el manifiesto
    /// una vez. Devuelve false si no queda ninguna candidata.
    /// </summary>
    public bool TryTakeRandom(string playlistId, IReadOnlySet<string> excludedVideoIds, out string? filePath, out string? videoId)
    {
        lock (_gate)
        {
            var candidates = _entries
                .Where(kv => string.Equals(kv.Key.PlaylistId, playlistId, StringComparison.Ordinal) &&
                             File.Exists(kv.Value.FilePath) &&
                             !excludedVideoIds.Contains(kv.Key.VideoId))
                .Select(kv => kv.Value)
                .ToList();

            if (candidates.Count == 0)
            {
                filePath = null;
                videoId = null;
                return false;
            }

            Entry chosen = candidates[Random.Shared.Next(candidates.Count)];
            chosen.Consumed = true;
            filePath = chosen.FilePath;
            videoId = chosen.VideoId;
            SaveManifest();
            return true;
        }
    }

    /// <summary>Ids de vídeo de esa playlist que tienen archivo en disco.</summary>
    public IReadOnlyList<string> GetVideoIds(string playlistId)
    {
        lock (_gate)
        {
            return _entries
                .Where(kv => string.Equals(kv.Key.PlaylistId, playlistId, StringComparison.Ordinal) &&
                             File.Exists(kv.Value.FilePath))
                .Select(kv => kv.Key.VideoId)
                .ToList();
        }
    }

    /// <summary>Pistas descargadas (con archivo en disco) de esa playlist.</summary>
    public int GetDownloadedCount(string playlistId)
    {
        lock (_gate)
        {
            return _entries.Count(kv =>
                string.Equals(kv.Key.PlaylistId, playlistId, StringComparison.Ordinal) &&
                File.Exists(kv.Value.FilePath));
        }
    }

    /// <summary>Pistas descargadas (con archivo en disco) de todas las playlists.</summary>
    public int TotalDownloadedCount
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count(kv => File.Exists(kv.Value.FilePath));
            }
        }
    }

    /// <summary>
    /// Una foto de las pistas guardadas para el panel de diagnóstico en vivo:
    /// qué hay en caché, de qué playlist, si está consumida y cuánto ocupa. Es
    /// solo de disco: no toca la red y no puede fallar.
    /// </summary>
    public IReadOnlyList<CachedTrackInfo> DescribeEntries()
    {
        lock (_gate)
        {
            return _entries
                .OrderBy(kv => kv.Key.PlaylistId, StringComparer.Ordinal)
                .ThenBy(kv => kv.Key.VideoId, StringComparer.Ordinal)
                .Select(kv => new CachedTrackInfo(
                    kv.Key.PlaylistId,
                    kv.Key.VideoId,
                    kv.Value.Consumed,
                    File.Exists(kv.Value.FilePath),
                    TryGetFileSize(kv.Value.FilePath) ?? 0,
                    kv.Value.DownloadedAtUtc))
                .ToList();
        }
    }

    /// <summary>
    /// Recupera todas las pistas que ya estaban descargadas en la carpeta
    /// (sesiones anteriores) para poder sonar a la hora sin descargar nada. Una
    /// pista que no supera las comprobaciones de integridad (p. ej. truncada por
    /// un corte de luz) se descarta y se borra en lugar de registrarse.
    /// </summary>
    public void LoadExisting()
    {
        FileInfo[] files;
        try
        {
            files = Directory.Exists(_dir) ? new DirectoryInfo(_dir).GetFiles() : [];
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        ManifestLookup manifest = LoadManifest();

        var obsolete = new List<string>();
        var parsed = new List<(string PlaylistId, string VideoId, FileInfo File)>();

        foreach (FileInfo file in files)
        {
            // El manifiesto no es una pista: no se barre ni se borra.
            if (IsManifestFile(file.FullName))
            {
                continue;
            }

            string name = Path.GetFileNameWithoutExtension(file.Name);
            int index = name.IndexOf(Separator, StringComparison.Ordinal);
            if (index <= 0 || index + Separator.Length >= name.Length)
            {
                obsolete.Add(file.FullName);
                continue;
            }

            parsed.Add((name[..index], name[(index + Separator.Length)..], file));
        }

        parsed.Sort((a, b) => b.File.LastWriteTimeUtc.CompareTo(a.File.LastWriteTimeUtc));

        foreach ((string playlistId, string videoId, FileInfo file) in parsed)
        {
            var key = (playlistId, videoId);

            if (_entries.TryGetValue(key, out var existing))
            {
                // Ya estaba registrada (p. ej. la acaba de dejar este proceso).
                if (!string.Equals(existing.FilePath, file.FullName, StringComparison.OrdinalIgnoreCase))
                {
                    obsolete.Add(file.FullName);
                }

                continue;
            }

            // Un archivo recuperado del disco solo se registra si supera las
            // comprobaciones de integridad: un fragmento truncado por un corte
            // de luz se descarta aquí y no llega nunca a NAudio.
            ManifestEntry? known = manifest.ByVideo.GetValueOrDefault(key) ??
                                   manifest.ByFileName.GetValueOrDefault(file.Name);

            if (!TrackSanity.LooksPlayable(file.FullName, known?.SizeBytes))
            {
                obsolete.Add(file.FullName);
                AppendLog($"se descarta pista dañada o incompleta ({file.Name}).");
                continue;
            }

            _entries[key] = new Entry(videoId, file.FullName, file.LastWriteTimeUtc)
            {
                // El estado consumido debe sobrevivir al reinicio: una pista ya
                // sonada no se vuelve a servir en la próxima sesión.
                Consumed = known?.Consumed ?? false,
                ExpectedBytes = known?.SizeBytes ?? file.Length,
            };
            AppendLog($"se recupera pista validada de una sesión anterior ({file.Name}).");
        }

        DeleteFiles(obsolete);
        SaveManifest();
    }

    /// <summary>
    /// Borra todas las pistas de una playlist cuyo vídeo ya no está en la lista
    /// remota. Devuelve las rutas realmente retiradas de la caché (vacío si no
    /// había nada que borrar).
    /// <para>
    /// Un <paramref name="allowedVideoIds"/> vacío NO borra nada: un conjunto
    /// vacío es indistinguible de «la consulta a la playlist devolvió cero
    /// vídeos» (fallo de red, lista privada, respuesta truncada), y ese es
    /// justo el caso en el que propagar el borrado vaciaría la caché entera.
    /// La comprobación vive aquí, en la caché, y no en quien llama, para que
    /// ningún camino futuro pueda borrar por accidente con una lista vacía:
    /// el único punto que borra es este y se niega por sí mismo.
    /// </para>
    /// <para>
    /// Tampoco toca un archivo con escritura en curso (<c>BeginWrite</c>): la
    /// descarga que lo está llenando se registraría luego con un archivo que ya
    /// no existe. Un archivo en uso por la reproducción se intenta borrar igual:
    /// la eliminación traga el <see cref="IOException"/> y lo deja para el
    /// siguiente barrido.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> RemoveTracksNotIn(string playlistId, IReadOnlySet<string> allowedVideoIds)
    {
        if (string.IsNullOrWhiteSpace(playlistId))
        {
            return [];
        }

        if (allowedVideoIds.Count == 0)
        {
            AppendLog($"no se borra nada de {playlistId}: la lista remota llegó vacía (no se puede distinguir de un fallo).");
            return [];
        }

        var removed = new List<string>();
        var blocked = new List<string>();

        lock (_gate)
        {
            foreach (var key in _entries.Keys
                         .Where(k => string.Equals(k.PlaylistId, playlistId, StringComparison.Ordinal))
                         .ToList())
            {
                if (allowedVideoIds.Contains(key.VideoId))
                {
                    continue;
                }

                Entry entry = _entries[key];
                if (_pendingWrites.Contains(entry.FilePath))
                {
                    blocked.Add(key.VideoId);
                    continue;
                }

                _entries.Remove(key);
                removed.Add(entry.FilePath);
            }
        }

        foreach (string videoId in blocked)
        {
            AppendLog($"no se borra la pista en curso de {playlistId}: la descarga sigue escribiendo {Describe(videoId)}.");
        }

        if (removed.Count > 0)
        {
            DeleteFiles(removed);
            SaveManifest();
            foreach (string filePath in removed)
            {
                AppendLog($"se borra pista que ya no está en la playlist ({playlistId}): {Path.GetFileName(filePath)}.");
            }
        }

        return removed;
    }

    /// <summary>
    /// Borra todas las pistas de las playlists que ya no usa ninguna alarma y
    /// devuelve las rutas retiradas de la caché (vacío si no había nada que
    /// borrar). La baja del manifiesto es siempre efectiva, aunque el borrado del
    /// archivo se traguen (queda en uso por una reproducción): el siguiente
    /// barrido lo deja.
    /// <para>
    /// Solo mira la pertenencia del id de playlist al conjunto de playlists en
    /// uso (ajustes y alarmas): es un barrido puramente local. NO consulta la red
    /// ni depende de datos remotos, así que no puede confundirse con un fallo de
    /// la consulta de una playlist: una playlist que sigue en uso pero cuya
    /// descarga remota ha fallado se conserva, porque su id sigue en el conjunto.
    /// El borrado por «este vídeo ya no está en la lista remota» es cosa de
    /// <see cref="RemoveTracksNotIn"/>.
    /// </para>
    /// <para>
    /// Un <paramref name="allowedPlaylistIds"/> vacío NO borra nada: un conjunto
    /// vacío es indistinguible de «los ajustes no se pudieron cargar», y ese es
    /// justo el caso en el que propagar el borrado vaciaría la caché entera.
    /// La comprobación vive aquí, en la caché, y no en quien llama, para que
    /// ningún camino futuro pueda borrar por accidente con un conjunto vacío.
    /// </para>
    /// <para>
    /// Tampoco toca un archivo con escritura en curso (<c>BeginWrite</c>): la
    /// descarga que lo está llenando se registraría luego con un archivo que ya
    /// no existe. El resto de archivos se intenta borrar igual: la eliminación
    /// traga el <see cref="IOException"/> y lo deja para el siguiente barrido.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> RemovePlaylistsNotIn(IReadOnlySet<string> allowedPlaylistIds)
    {
        if (allowedPlaylistIds.Count == 0)
        {
            AppendLog("no se borra nada: no hay ninguna playlist en uso (no se puede distinguir de unos ajustes sin cargar).");
            return [];
        }

        var removed = new List<string>();
        var blocked = new List<string>();

        lock (_gate)
        {
            foreach (var key in _entries.Keys.ToList())
            {
                if (allowedPlaylistIds.Contains(key.PlaylistId))
                {
                    continue;
                }

                Entry entry = _entries[key];
                if (_pendingWrites.Contains(entry.FilePath))
                {
                    // Se deja fuera del barrido pero sigue en la caché: la descarga
                    // en curso lo registrará al terminar.
                    blocked.Add($"{key.PlaylistId}/{key.VideoId}");
                    continue;
                }

                _entries.Remove(key);
                removed.Add(entry.FilePath);
            }
        }

        foreach (string track in blocked)
        {
            AppendLog($"no se borra la pista en curso de la playlist abandonada {track}: la descarga sigue escribiendo.");
        }

        if (removed.Count > 0)
        {
            DeleteFiles(removed);
            SaveManifest();

            // El nombre del archivo empieza por el id de playlist ("PLx--video.m4a"):
            // es la traza útil en el log para saber qué se barrió.
            foreach (string filePath in removed)
            {
                AppendLog($"se borra pista de una playlist que ya no usa ninguna alarma: {Path.GetFileName(filePath)}.");
            }
        }

        return removed;
    }

    /// <summary>Estado persistente de una pista, para recuperarlo en la próxima sesión.</summary>
    private sealed class ManifestEntry
    {
        public string PlaylistId { get; set; } = "";

        public string VideoId { get; set; } = "";

        public string FileName { get; set; } = "";

        public DateTime DownloadedAtUtc { get; set; }

        public bool Consumed { get; set; }

        public long? SizeBytes { get; set; }
    }

    private sealed class ManifestData
    {
        public List<ManifestEntry> Tracks { get; set; } = [];
    }

    /// <summary>Índices del manifiesto: por (playlist, vídeo) y por nombre de archivo.</summary>
    private sealed class ManifestLookup
    {
        public Dictionary<(string PlaylistId, string VideoId), ManifestEntry> ByVideo { get; } = new();

        public Dictionary<string, ManifestEntry> ByFileName { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsManifestFile(string path) =>
        string.Equals(Path.GetFileName(path), ManifestFileName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Path.GetFileName(path), ManifestFileName + ".tmp", StringComparison.OrdinalIgnoreCase);

    private static long? TryGetFileSize(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Lee el manifiesto: (playlistId, videoId) → estado de su pista.</summary>
    private ManifestLookup LoadManifest()
    {
        var lookup = new ManifestLookup();
        try
        {
            if (!File.Exists(ManifestPath))
            {
                return lookup;
            }

            var data = JsonSerializer.Deserialize<ManifestData>(File.ReadAllText(ManifestPath), JsonOptions);
            foreach (ManifestEntry entry in data?.Tracks ?? [])
            {
                if (string.IsNullOrEmpty(entry.PlaylistId) || string.IsNullOrEmpty(entry.FileName))
                {
                    continue;
                }

                // El manifiesto de versiones anteriores guardaba una sola pista
                // por playlist, pero ya traía el id de vídeo: la clave compuesta
                // lo carga tal cual. Sin id de vídeo se cae al nombre de archivo.
                var key = (entry.PlaylistId, entry.VideoId ?? string.Empty);
                lookup.ByVideo.TryAdd(key, entry);
                lookup.ByFileName.TryAdd(entry.FileName, entry);
            }

            return lookup;
        }
        catch (Exception ex)
        {
            AppendLog($"manifiesto ilegible ({ex.Message}).");
            return new ManifestLookup();
        }
    }

    private void SaveManifest()
    {
        try
        {
            Directory.CreateDirectory(_dir);

            ManifestData data;
            lock (_gate)
            {
                data = new ManifestData
                {
                    Tracks = _entries.Select(e => new ManifestEntry
                    {
                        PlaylistId = e.Key.PlaylistId,
                        VideoId = e.Key.VideoId,
                        FileName = Path.GetFileName(e.Value.FilePath),
                        DownloadedAtUtc = e.Value.DownloadedAtUtc,
                        Consumed = e.Value.Consumed,
                        SizeBytes = e.Value.ExpectedBytes,
                    }).ToList(),
                };
            }

            string temp = ManifestPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(data, JsonOptions));
            if (File.Exists(ManifestPath))
            {
                File.Replace(temp, ManifestPath, null);
            }
            else
            {
                File.Move(temp, ManifestPath);
            }
        }
        catch (Exception ex)
        {
            AppendLog($"no se pudo guardar el manifiesto ({ex.Message}).");
        }
    }

    private static void AppendLog(string message) => OperationLog.Write("cache", message);

    private static string Describe(string? videoId) => string.IsNullOrWhiteSpace(videoId) ? "sin id" : videoId;

    private static void DeleteFiles(IEnumerable<string> paths)
    {
        foreach (string path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            TryDelete(path);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // El archivo puede estar en uso por una reproducción en curso.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class Entry(string videoId, string filePath, DateTime downloadedAtUtc)
    {
        public string VideoId { get; } = videoId;

        public string FilePath { get; } = filePath;

        public DateTime DownloadedAtUtc { get; } = downloadedAtUtc;

        public bool Consumed { get; set; }

        /// <summary>Tamaño real del archivo al registrarlo (referencia de integridad).</summary>
        public long? ExpectedBytes { get; set; }
    }
}
