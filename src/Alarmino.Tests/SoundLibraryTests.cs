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
    public void ResolvePresetFile_ReturnsFile_WhenPresent()
    {
        string appData = CreateTempDir();
        try
        {
            var library = new SoundLibrary(appData);
            Assert.Null(library.ResolvePresetFile("bell"));

            Directory.CreateDirectory(library.PresetsDirectory);
            File.WriteAllText(Path.Combine(library.PresetsDirectory, "bell.wav"), "x");

            Assert.Equal(Path.Combine(library.PresetsDirectory, "bell.wav"), library.ResolvePresetFile("bell"));
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
