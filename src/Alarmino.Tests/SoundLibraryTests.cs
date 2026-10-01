using Alarmino.Core.Models;
using Alarmino.Core.Services;

namespace Alarmino.Tests;

public class SoundLibraryTests
{
    private static string CreateTempDir() =>
        Path.Combine(Path.GetTempPath(), "alarmino-tests", Guid.NewGuid().ToString());

    private static string WriteTempWav(string dir, string name)
    {
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name);
        File.WriteAllBytes(path, [0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0]);
        return path;
    }

    [Fact]
    public void StoreUserSound_CopiesIntoLibrary_AndDetectsExisting()
    {
        string appData = CreateTempDir();
        string sourceDir = Path.Combine(Path.GetTempPath(), "alarmino-tests", "origen");
        string source = WriteTempWav(sourceDir, "campana.wav");

        try
        {
            var library = new SoundLibrary(appData);
            string stored = library.StoreUserSound(source);

            Assert.True(File.Exists(stored));
            Assert.True(library.IsInLibrary(stored));
            Assert.False(library.IsInLibrary(source));

            string again = library.StoreUserSound(stored);
            Assert.Equal(stored, again);
        }
        finally
        {
            if (Directory.Exists(appData))
            {
                Directory.Delete(appData, recursive: true);
            }

            if (Directory.Exists(sourceDir))
            {
                Directory.Delete(sourceDir, recursive: true);
            }
        }
    }

    [Fact]
    public void Normalize_MovesExternalSoundIntoLibrary()
    {
        string appData = CreateTempDir();
        string sourceDir = Path.Combine(Path.GetTempPath(), "alarmino-tests", "origen");
        string source = WriteTempWav(sourceDir, "timbre.wav");

        try
        {
            var library = new SoundLibrary(appData);
            var selection = new SoundSelection
            {
                Kind = SoundSourceKind.File,
                FilePath = source,
            };

            Assert.True(library.Normalize(selection));
            Assert.True(library.IsInLibrary(selection.FilePath));
            Assert.True(File.Exists(selection.FilePath));
        }
        finally
        {
            if (Directory.Exists(appData))
            {
                Directory.Delete(appData, recursive: true);
            }

            if (Directory.Exists(sourceDir))
            {
                Directory.Delete(sourceDir, recursive: true);
            }
        }
    }

    [Fact]
    public void EnsureDefaultPresets_InstallsTheThreeShippedAudios()
    {
        string appData = CreateTempDir();
        try
        {
            var library = new SoundLibrary(appData);
            Assert.Equal(3, library.EnsureDefaultPresets());

            foreach (string presetId in new[] { "airplaneBeep", "softChime", "fireAlarm" })
            {
                string? file = library.ResolvePresetFile(presetId);
                Assert.NotNull(file);
                Assert.True(File.Exists(file), $"falta el preset {presetId}");

                // Debe ser un MP3 de verdad (cabecera MPEG válida y audio audible).
                TimeSpan duration = ReadMpegDuration(file);
                Assert.True(duration > TimeSpan.FromSeconds(1), $"{presetId} no dura nada");
            }

            // Segunda llamada: no vuelve a copiar nada.
            Assert.Equal(0, library.EnsureDefaultPresets());
        }
        finally
        {
            if (Directory.Exists(appData))
            {
                Directory.Delete(appData, recursive: true);
            }
        }
    }

    /// <summary>
    /// Duración aproximada de un MP3: busca la primera cabecera de trama MPEG y
    /// calcula el total con el bitrate. No decodifica (NAudio necesita el códec
    /// del sistema, que en Linux no existe).
    /// </summary>
    private static TimeSpan ReadMpegDuration(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        int[] bitratesV1 = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0, 0];
        int[] bitratesV2 = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0, 0];

        for (int i = 0; i + 3 < data.Length; i++)
        {
            if (data[i] != 0xFF || (data[i + 1] & 0xE0) != 0xE0)
            {
                continue;
            }

            int version = (data[i + 1] >> 3) & 3;   // 3 = MPEG1, 2 = MPEG2
            int layer = (data[i + 1] >> 1) & 3;      // 1 = Layer III
            int bitrateIndex = (data[i + 2] >> 4) & 0xF;
            int rateIndex = (data[i + 2] >> 2) & 0x3;

            if (version is 0 or 1 || layer != 1 || bitrateIndex is 0 or 15 || rateIndex == 3)
            {
                continue;
            }

            int bitrate = (version == 3 ? bitratesV1 : bitratesV2)[bitrateIndex];
            return TimeSpan.FromSeconds(data.Length * 8d / (bitrate * 1000d));
        }

        return TimeSpan.Zero;
    }

    [Fact]
    public void ResolvePresetFile_ReturnsFile_WhenPresent()
    {
        string appData = CreateTempDir();
        try
        {
            var library = new SoundLibrary(appData);
            Assert.Null(library.ResolvePresetFile("airplaneBeep"));

            Directory.CreateDirectory(library.PresetsDirectory);
            File.WriteAllText(Path.Combine(library.PresetsDirectory, "airplaneBeep.wav"), "x");

            Assert.Equal(Path.Combine(library.PresetsDirectory, "airplaneBeep.wav"), library.ResolvePresetFile("airplaneBeep"));
        }
        finally
        {
            if (Directory.Exists(appData))
            {
                Directory.Delete(appData, recursive: true);
            }
        }
    }
}
