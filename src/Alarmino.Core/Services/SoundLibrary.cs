using Alarmino.Core.Models;

namespace Alarmino.Core.Services;

/// <summary>
/// Biblioteca de sonidos en %APPDATA%\Alarmino\sounds.
/// Los archivos que elige el usuario se copian aquí (nombre único) para que la
/// alarma no se rompa si el archivo original se mueve o se borra. Los sonidos
/// predeterminados en formato de archivo (cuando existan) viven en sounds\presets.
/// </summary>
public sealed class SoundLibrary
{
    private readonly string _baseDir;

    public SoundLibrary(string? baseDir = null)
    {
        _baseDir = baseDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Alarmino");
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(SoundsDirectory);
    }

    public string DataDirectory => _baseDir;

    /// <summary>Carpeta con las copias de los sonidos personalizados.</summary>
    public string SoundsDirectory => Path.Combine(_baseDir, "sounds");

    /// <summary>Carpeta con los presets en formato de archivo (si existen).</summary>
    public string PresetsDirectory => Path.Combine(SoundsDirectory, "presets");

    /// <summary>True si la ruta ya está dentro de la biblioteca de sonidos.</summary>
    public bool IsInLibrary(string? path)
        => !string.IsNullOrWhiteSpace(path) && IsUnder(SoundsDirectory, path);

    /// <summary>
    /// Copia un archivo de sonido a la biblioteca con nombre único y devuelve la ruta guardada.
    /// Si ya estaba en la biblioteca, devuelve la misma ruta sin duplicar.
    /// </summary>
    public string StoreUserSound(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            throw new ArgumentException("La ruta del sonido no puede estar vacía.", nameof(sourcePath));
        }

        if (IsInLibrary(sourcePath))
        {
            return Path.GetFullPath(sourcePath);
        }

        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("El archivo de sonido ya no existe.", sourcePath);
        }

        Directory.CreateDirectory(SoundsDirectory);
        string name = SanitizeFileName(Path.GetFileName(sourcePath));
        string dest = Path.Combine(SoundsDirectory, $"{Guid.NewGuid():N}_{name}");
        File.Copy(sourcePath, dest, overwrite: false);
        return dest;
    }

    /// <summary>
    /// Ruta de archivo de un preset, si se ha integrado uno en sounds\presets.
    /// Devuelve null si el preset se genera por síntesis.
    /// </summary>
    public string? ResolvePresetFile(string? presetId)
    {
        if (string.IsNullOrWhiteSpace(presetId))
        {
            return null;
        }

        if (!Directory.Exists(PresetsDirectory))
        {
            return null;
        }

        foreach (string pattern in new[] { presetId + ".wav", presetId + ".mp3" })
        {
            string file = Path.Combine(PresetsDirectory, pattern);
            if (File.Exists(file))
            {
                return file;
            }
        }

        return null;
    }

    /// <summary>Normaliza un <see cref="SoundSelection"/> de archivo: copia el sonido a la biblioteca.</summary>
    public bool Normalize(SoundSelection selection)
    {
        if (selection is null || selection.Kind != SoundSourceKind.File)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(selection.FilePath))
        {
            return false;
        }

        if (IsInLibrary(selection.FilePath))
        {
            return false;
        }

        if (!File.Exists(selection.FilePath))
        {
            return false;
        }

        selection.FilePath = StoreUserSound(selection.FilePath);
        return true;
    }

    private static bool IsUnder(string directory, string path)
    {
        string dir = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string target = Path.GetFullPath(path);
        return target.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string SanitizeFileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }

        return string.IsNullOrWhiteSpace(name) ? "sonido" : name;
    }
}
