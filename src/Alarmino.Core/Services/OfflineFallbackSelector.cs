namespace Alarmino.Core.Services;

/// <summary>
/// Elige el MP3 de respaldo sin conexión que debe usar la aplicación. Si el
/// archivo configurado ya no existe o no parece reproducible, busca el primer
/// sonido predeterminado válido entre los candidatos (carpetas de la biblioteca
/// o del instalador) y lo devuelve.
/// <para>
/// Es lógica pura sobre rutas, sin dependencias de WPF, para poder probarla.
/// </para>
/// </summary>
public static class OfflineFallbackSelector
{
    /// <summary>Extensiones de audio admitidas para el respaldo.</summary>
    private static readonly string[] AudioExtensions = [".mp3", ".wav"];

    /// <summary>
    /// Devuelve la ruta del respaldo a usar, o null si no hay ningún candidato
    /// válido. Si <paramref name="configuredPath"/> sigue siendo válido se respeta
    /// tal cual; si no, se busca un sonido predeterminado en las carpetas dadas.
    /// </summary>
    public static string? Select(string? configuredPath, IEnumerable<string> candidateDirectories)
        => Select(configuredPath, candidateDirectories, out _);

    /// <summary>
    /// Igual que <see cref="Select(string?, IEnumerable{string})"/>, informando
    /// además de las carpetas y archivos examinados (para el log de diagnóstico).
    /// </summary>
    public static string? Select(
        string? configuredPath,
        IEnumerable<string> candidateDirectories,
        out IReadOnlyList<string> examinedPaths)
    {
        var examined = new List<string>();

        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            examined.Add(configuredPath);
            if (File.Exists(configuredPath) && TrackSanity.LooksPlayable(configuredPath))
            {
                examinedPaths = examined;
                return configuredPath;
            }
        }

        foreach (string directory in candidateDirectories)
        {
            examined.Add(directory);
            string? candidate = FirstPlayableFile(directory, examined);
            if (candidate is not null)
            {
                examinedPaths = examined;
                return candidate;
            }
        }

        examinedPaths = examined;
        return null;
    }

    private static string? FirstPlayableFile(string directory, List<string> examined)
    {
        // Ojo: File.Exists devuelve false para un directorio; el directorio se
        // comprueba con Directory.Exists y cada archivo con TrackSanity.
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return null;
        }

        List<string> files;
        try
        {
            files = Directory.GetFiles(directory)
                .Where(f => AudioExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        examined.AddRange(files);
        return files.FirstOrDefault(f => TrackSanity.LooksPlayable(f));
    }
}
