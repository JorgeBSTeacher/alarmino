namespace Alarmino.Core.Services;

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
            _entries[playlistId] = new Entry(videoId, filePath, downloadedAtUtc) { Consumed = consumed };

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
    /// Recupera las pistas que ya estaban descargadas en la carpeta (sesiones
    /// anteriores) para poder sonar a la hora sin descargar nada.
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

        var obsolete = new List<string>();
        var parsed = new List<(string PlaylistId, string VideoId, FileInfo File)>();

        foreach (FileInfo file in files)
        {
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
            }
            else
            {
                _entries[playlistId] = new Entry(videoId, file.FullName, file.LastWriteTimeUtc);
            }
        }

        DeleteFiles(obsolete);
        Prune();
    }

    /// <summary>Vídeo almacenado para esa playlist, esté o no consumido.</summary>
    public string? PeekVideoId(string playlistId)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(playlistId, out var entry) ? entry.VideoId : null;
        }
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
                .Where(f => !referenced.Contains(f) && !IsPending(f))
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
    }
}
