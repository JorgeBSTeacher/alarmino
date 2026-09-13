using System.Text.Json;
using Alarmino.Core.Models;

namespace Alarmino.Core.Services;

/// <summary>
/// Persistencia JSON en %APPDATA%\Alarmino (alarms.json, config.json, events.json).
/// La escritura es atómica (archivo temporal + renombrado).
/// </summary>
public sealed class JsonDataStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _baseDir;

    public JsonDataStore(string? baseDir = null)
    {
        _baseDir = baseDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Alarmino");
        Directory.CreateDirectory(_baseDir);
    }

    public string DataDirectory => _baseDir;

    public SoundLibrary SoundLibrary => new(_baseDir);

    private string PathOf(string name) => Path.Combine(_baseDir, name);

    public List<Alarm> LoadAlarms()
    {
        var alarms = Load<List<Alarm>>("alarms.json") ?? [];

        // Migración: los sonidos de archivo que apuntan fuera de la biblioteca
        // se copian a sounds\ y se actualiza la referencia.
        var library = SoundLibrary;
        bool changed = false;
        foreach (var alarm in alarms)
        {
            changed |= library.Normalize(alarm.Sound);
        }

        if (changed)
        {
            SaveAlarms(alarms);
        }

        return alarms;
    }

    public void SaveAlarms(IEnumerable<Alarm> alarms) => Save("alarms.json", alarms);

    public AppSettings LoadSettings()
    {
        var settings = Load<AppSettings>("config.json") ?? new AppSettings();
        bool changed = SoundLibrary.Normalize(settings.AlarmSound);
        if (SoundLibrary.Normalize(settings.RainSound))
        {
            changed = true;
        }

        if (changed)
        {
            SaveSettings(settings);
        }

        return settings;
    }

    public void SaveSettings(AppSettings settings) => Save("config.json", settings);

    /// <summary>Copia un archivo de sonido a la biblioteca y devuelve la ruta guardada.</summary>
    public string StoreSoundInLibrary(string sourcePath) => SoundLibrary.StoreUserSound(sourcePath);

    public List<EventLogEntry> LoadEvents() => Load<List<EventLogEntry>>("events.json") ?? [];

    public void SaveEvents(IEnumerable<EventLogEntry> events) => Save("events.json", events);

    public static void AppendLogLine(string? baseDir, string line)
    {
        try
        {
            baseDir ??= Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Alarmino");
            Directory.CreateDirectory(Path.Combine(baseDir, "logs"));
            var logsDir = Path.Combine(baseDir, "logs");
            var file = Path.Combine(logsDir, $"alarmino-{DateTime.Now:yyyyMMdd}.log");
            File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
        }
        catch
        {
            // El log nunca debe impedir el funcionamiento de la app.
        }
    }

    private T? Load<T>(string name)
    {
        var path = PathOf(name);
        if (!File.Exists(path))
        {
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception ex)
        {
            AppendLogLine(_baseDir, $"Error leyendo {name}: {ex.Message}");
            return default;
        }
    }

    private void Save<T>(string name, T value)
    {
        var path = PathOf(name);
        var temp = path + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(value, JsonOptions));
            if (File.Exists(path))
            {
                File.Replace(temp, path, null);
            }
            else
            {
                File.Move(temp, path);
            }
        }
        catch (Exception ex)
        {
            AppendLogLine(_baseDir, $"Error escribiendo {name}: {ex.Message}");
        }
    }
}
