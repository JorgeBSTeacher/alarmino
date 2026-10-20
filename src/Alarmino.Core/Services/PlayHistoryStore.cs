using System.Text.Json;

namespace Alarmino.Core.Services;

/// <summary>
/// Historial de reproducción en <c>%APPDATA%\Alarmino\play-history.json</c> con la
/// forma playlistId → videoId → última reproducción (UTC). Sirve para no repetir
/// una pista dentro de la ventana de días configurada.
/// <para>
/// Es "best-effort": si el archivo no existe, está corrupto o no se puede
/// escribir, el historial se trata como vacío y nunca lanza hacia el llamador;
/// un fallo al registrar no puede impedir que suene la alarma.
/// </para>
/// </summary>
public sealed class PlayHistoryStore
{
    private readonly string _filePath;
    private readonly object _gate = new();
    private readonly Dictionary<string, Dictionary<string, DateTime>> _plays = new(StringComparer.Ordinal);

    public PlayHistoryStore(string? dir = null)
    {
        _filePath = Path.Combine(
            dir ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Alarmino"),
            "play-history.json");

        _plays = Load();
    }

    /// <summary>Archivo JSON donde se conserva el historial.</summary>
    public string FilePath => _filePath;

    /// <summary>
    /// Registra que una pista ha sonado ahora mismo y lo persiste de inmediato.
    /// </summary>
    public void Record(string playlistId, string videoId, DateTime playedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(playlistId) || string.IsNullOrWhiteSpace(videoId))
        {
            return;
        }

        lock (_gate)
        {
            if (!_plays.TryGetValue(playlistId, out var playlist))
            {
                playlist = new Dictionary<string, DateTime>(StringComparer.Ordinal);
                _plays[playlistId] = playlist;
            }

            playlist[videoId] = playedAtUtc;
        }

        Save();
    }

    /// <summary>Pistas de una playlist reproducidas dentro de la ventana indicada.</summary>
    public IReadOnlyList<string> InWindow(string playlistId, TimeSpan window, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(playlistId))
        {
            return [];
        }

        DateTime threshold = nowUtc - window;
        lock (_gate)
        {
            if (!_plays.TryGetValue(playlistId, out var playlist))
            {
                return [];
            }

            return playlist
                .Where(p => p.Value >= threshold)
                .Select(p => p.Key)
                .ToList();
        }
    }

    private Dictionary<string, Dictionary<string, DateTime>> Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return new Dictionary<string, Dictionary<string, DateTime>>(StringComparer.Ordinal);
            }

            var data = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, DateTime>>>(
                File.ReadAllText(_filePath));

            return data is null
                ? new Dictionary<string, Dictionary<string, DateTime>>(StringComparer.Ordinal)
                : new Dictionary<string, Dictionary<string, DateTime>>(data, StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            OperationLog.Write("history", $"historial de reproducción ilegible ({ex.Message}); se empieza vacío.");
            return new Dictionary<string, Dictionary<string, DateTime>>(StringComparer.Ordinal);
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);

            Dictionary<string, Dictionary<string, DateTime>> snapshot;
            lock (_gate)
            {
                snapshot = _plays.ToDictionary(
                    p => p.Key,
                    p => new Dictionary<string, DateTime>(p.Value, StringComparer.Ordinal),
                    StringComparer.Ordinal);
            }

            string temp = _filePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
            if (File.Exists(_filePath))
            {
                File.Replace(temp, _filePath, null);
            }
            else
            {
                File.Move(temp, _filePath);
            }
        }
        catch (Exception ex)
        {
            OperationLog.Write("history", $"no se pudo guardar el historial de reproducción ({ex.Message}).");
        }
    }
}
