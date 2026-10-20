using System.Text.Json;

namespace Alarmino.Core.Services;

/// <summary>
/// Estado del búfer de descargas: cuántas pistas sin consumir hay listas para el
/// próximo arranque frente al objetivo configurado.
/// </summary>
public readonly record struct BufferState(int Ready, int Target)
{
    /// <summary>True si ya hay tantas pistas listas como pide el objetivo.</summary>
    public bool IsFull => Target <= 0 || Ready >= Target;
}

/// <summary>
/// Caché de pistas de YouTube descargadas por adelantado (una por playlist).
/// Permite que la alarma empiece a sonar a su hora sin esperar una descarga.
/// Solo guarda las últimas <see cref="MaxTracks"/> pistas y borra el resto.
/// </summary>
public sealed class YouTubeTrackCache
{
    /// <summary>Número máximo de pistas guardadas a la vez (una por playlist).</summary>
    public const int MaxTracks = 3;

    private readonly string _dir;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new();
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
    /// Registra una pista recién descargada. Sustituye la anterior de esa
    /// playlist, descarta la más antigua si se supera <see cref="MaxTracks"/> y
    /// borra los archivos huérfanos de la carpeta.
    /// </summary>
    public void Register(string playlistId, string videoId, string filePath, DateTime downloadedAtUtc, bool consumed = false)
    {
        var toDelete = new List<string>();

        lock (_gate)
        {
            if (_entries.TryGetValue(playlistId, out var previous))
            {
                toDelete.Add(previous.FilePath);
            }

            _pendingWrites.Remove(filePath);
            _entries[playlistId] = new Entry(videoId, filePath, downloadedAtUtc)
            {
                Consumed = consumed,
                ExpectedBytes = TryGetFileSize(filePath),
            };

            while (_entries.Count > MaxTracks)
            {
                var oldest = _entries.OrderBy(e => e.Value.DownloadedAtUtc).First();
                toDelete.Add(oldest.Value.FilePath);
                _entries.Remove(oldest.Key);
            }

            var referenced = new HashSet<string>(_entries.Values.Select(e => e.FilePath), StringComparer.OrdinalIgnoreCase);
            toDelete.AddRange(ListStaleFiles(referenced));
        }

        DeleteFiles(toDelete);
        SaveManifest();
    }

    /// <summary>
    /// Toma la pista precargada de una playlist (para reproducirla ya) y la
    /// marca como consumida, de modo que se vuelva a precargar otra.
    /// </summary>
    public bool TryTake(string playlistId, out string? filePath, out string? videoId)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(playlistId, out var entry) ||
                entry.Consumed ||
                !File.Exists(entry.FilePath))
            {
                _entries.Remove(playlistId);
                filePath = null;
                videoId = null;
                return false;
            }

            entry.Consumed = true;
            filePath = entry.FilePath;
            videoId = entry.VideoId;
            SaveManifest();
            return true;
        }
    }

    /// <summary>True si hay una pista sin usar y bastante reciente para no volver a descargar.</summary>
    public bool IsFresh(string playlistId, TimeSpan maxAge, DateTime nowUtc)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(playlistId, out var entry) &&
                   !entry.Consumed &&
                   File.Exists(entry.FilePath) &&
                   nowUtc - entry.DownloadedAtUtc < maxAge;
        }
    }

    /// <summary>
    /// Cuántas pistas sin consumir hay listas (y existen en disco) frente al
    /// objetivo del búfer. Las pistas ya sonadas no cuentan: no sirven para el
    /// próximo arranque en frío.
    /// </summary>
    public BufferState GetBufferState(int target)
    {
        lock (_gate)
        {
            int ready = _entries.Values.Count(e => !e.Consumed && File.Exists(e.FilePath));
            return new BufferState(ready, Math.Max(0, target));
        }
    }

    /// <summary>
    /// Recupera las pistas que ya estaban descargadas en la carpeta (sesiones
    /// anteriores) para poder sonar a la hora sin descargar nada. Una pista que
    /// no supera las comprobaciones de integridad (p. ej. truncada por un corte
    /// de luz) se descarta y se borra en lugar de registrarse.
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

        Dictionary<string, ManifestEntry> manifest = LoadManifest();

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
            if (_entries.TryGetValue(playlistId, out var existing))
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
            ManifestEntry? known = manifest.GetValueOrDefault(playlistId);
            if (known is not null && !string.Equals(known.VideoId, videoId, StringComparison.Ordinal))
            {
                known = null;
            }

            if (!TrackSanity.LooksPlayable(file.FullName, known?.SizeBytes))
            {
                obsolete.Add(file.FullName);
                AppendLog($"se descarta pista dañada o incompleta ({file.Name}).");
                continue;
            }

            _entries[playlistId] = new Entry(videoId, file.FullName, file.LastWriteTimeUtc)
            {
                // El estado consumido debe sobrevivir al reinicio: una pista ya
                // sonada no se vuelve a servir en la próxima sesión.
                Consumed = known?.Consumed ?? false,
                ExpectedBytes = known?.SizeBytes ?? file.Length,
            };
            AppendLog($"se recupera pista validada de una sesión anterior ({file.Name}).");
        }

        DeleteFiles(obsolete);
        Prune();
        SaveManifest();
    }

    /// <summary>Vídeo almacenado para esa playlist, esté o no consumido.</summary>
    public string? PeekVideoId(string playlistId)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(playlistId, out var entry) ? entry.VideoId : null;
        }
    }

    /// <summary>
    /// Borra la pista de una playlist si su vídeo ya no está en la lista remota.
    /// Devuelve las rutas realmente retiradas de la caché (vacío si no había
    /// nada que borrar).
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
        bool changed = false;

        lock (_gate)
        {
            if (!_entries.TryGetValue(playlistId, out var entry))
            {
                return [];
            }

            if (allowedVideoIds.Contains(entry.VideoId))
            {
                return [];
            }

            if (_pendingWrites.Contains(entry.FilePath))
            {
                AppendLog($"no se borra la pista en curso de {playlistId}: la descarga sigue escribiendo {Path.GetFileName(entry.FilePath)}.");
                return [];
            }

            _entries.Remove(playlistId);
            removed.Add(entry.FilePath);
            changed = true;
        }

        if (changed)
        {
            DeleteFiles(removed);
            SaveManifest();
            AppendLog($"se borra pista que ya no está en la playlist ({playlistId}): {Path.GetFileName(removed[0])}.");
        }

        return removed;
    }

    private bool IsPending(string path)
    {
        lock (_gate)
        {
            return _pendingWrites.Contains(path);
        }
    }

    private void Prune()
    {
        while (_entries.Count > MaxTracks)
        {
            var oldest = _entries.OrderBy(e => e.Value.DownloadedAtUtc).First();
            _entries.Remove(oldest.Key);
            TryDelete(oldest.Value.FilePath);
        }
    }

    private List<string> ListStaleFiles(HashSet<string> referenced)
    {
        if (!Directory.Exists(_dir))
        {
            return [];
        }

        try
        {
            return Directory.GetFiles(_dir)
                .Where(f => !referenced.Contains(f) && !IsPending(f) && !IsManifestFile(f))
                .ToList();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
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

    /// <summary>Lee el manifiesto: playlistId → estado de su pista.</summary>
    private Dictionary<string, ManifestEntry> LoadManifest()
    {
        try
        {
            if (!File.Exists(ManifestPath))
            {
                return new Dictionary<string, ManifestEntry>(StringComparer.Ordinal);
            }

            var data = JsonSerializer.Deserialize<ManifestData>(File.ReadAllText(ManifestPath), JsonOptions);
            return data?.Tracks?
                .Where(t => !string.IsNullOrEmpty(t.PlaylistId) && !string.IsNullOrEmpty(t.FileName))
                .GroupBy(t => t.PlaylistId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal)
                ?? new Dictionary<string, ManifestEntry>(StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            AppendLog($"manifiesto ilegible ({ex.Message}).");
            return new Dictionary<string, ManifestEntry>(StringComparer.Ordinal);
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
                        PlaylistId = e.Key,
                        VideoId = e.Value.VideoId,
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
