using Alarmino.Core.Services;

namespace Alarmino.Tests;

public class TrackSanityTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "alarmino-track-sanity-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    /// <summary>Un MP3 real de los que incluye la app (dura bastante más de 1 s).</summary>
    private static byte[] RealMp3()
    {
        var library = new SoundLibrary(Path.Combine(Path.GetTempPath(), "alarmino-presets-" + Guid.NewGuid().ToString("N")));
        library.EnsureDefaultPresets();
        return File.ReadAllBytes(library.ResolvePresetFile("fireAlarm")!);
    }

    private string Write(byte[] data, string name = "audio.mp3")
    {
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, data);
        return path;
    }

    [Fact]
    public void LooksPlayable_accepts_a_real_download()
    {
        string path = Write(RealMp3());
        Assert.True(TrackSanity.LooksPlayable(path));
        Assert.True(TrackSanity.EstimateMpegDuration(path) > TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void LooksPlayable_rejects_empty_and_tiny_files()
    {
        Assert.False(TrackSanity.LooksPlayable(Write([])));
        Assert.False(TrackSanity.LooksPlayable(Write(new byte[TrackSanity.MinimumBytes - 1])));
        Assert.False(TrackSanity.LooksPlayable(null));
        Assert.False(TrackSanity.LooksPlayable(Path.Combine(_dir, "no-existe.mp3")));
    }

    [Fact]
    public void LooksPlayable_rejects_a_truncated_download()
    {
        byte[] full = RealMp3();
        // Suficiente peso para pasar el mínimo, pero cortada a menos de 1 s de audio.
        string path = Write(full[..(full.Length / 100)]);

        Assert.True(new FileInfo(path).Length >= TrackSanity.MinimumBytes);
        Assert.True(TrackSanity.EstimateMpegDuration(path) < TimeSpan.FromSeconds(1));
        Assert.False(TrackSanity.LooksPlayable(path));
    }

    [Fact]
    public void EstimateMpegDuration_returns_null_when_it_is_not_an_mp3()
    {
        // Un m4a empieza por la caja 'ftyp'; no se puede medir sin decodificar.
        byte[] m4a = [0x00, 0x00, 0x00, 0x20, 0x66, 0x74, 0x79, 0x70, 0x69, 0x73, 0x6F, 0x6D];
        Assert.Null(TrackSanity.EstimateMpegDuration(Write(m4a, "audio.m4a")));

        // Con peso suficiente, un archivo que no es MP3 se acepta (solo hay peso).
        Assert.True(TrackSanity.LooksPlayable(Write(new byte[TrackSanity.MinimumBytes + 10], "ruido.bin")));
    }
}
