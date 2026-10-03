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

    /// <summary>MP3 sintético CBR para casos de duración exacta (bipes cortos).</summary>
    private static byte[] SyntheticMp3(double seconds, int bitrateKbps = 160)
    {
        // 0xFF 0xFB = MPEG1 Layer III sin CRC; 0xA0 = 160 kbps a 44,1 kHz.
        byte[] file = new byte[(int)(seconds * bitrateKbps * 1000 / 8)];
        file[0] = 0xFF;
        file[1] = 0xFB;
        file[2] = 0xA0;
        return file;
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
        byte[] mp3 = RealMp3();
        string path = Write(mp3);

        Assert.True(TrackSanity.LooksPlayable(path, mp3.Length));
        Assert.True(TrackSanity.EstimateMpegDuration(path) > TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void LooksPlayable_accepts_a_short_two_second_beep()
    {
        // Un bip de 2 s a 160 kbps pesa 40 KB: ni se parece a una canción, pero es
        // una pista legítima y debe sonar entera.
        byte[] bip = SyntheticMp3(2);
        string path = Write(bip);

        Assert.Equal(2, TrackSanity.EstimateMpegDuration(path)!.Value.TotalSeconds, 1);
        Assert.True(TrackSanity.LooksPlayable(path));
        Assert.True(TrackSanity.LooksPlayable(path, bip.Length));
    }

    [Fact]
    public void LooksPlayable_rejects_empty_tiny_and_missing_files()
    {
        Assert.False(TrackSanity.LooksPlayable(Write([])));
        Assert.False(TrackSanity.LooksPlayable(Write(new byte[TrackSanity.MinimumBytes - 1])));
        Assert.False(TrackSanity.LooksPlayable(null));
        Assert.False(TrackSanity.LooksPlayable(Path.Combine(_dir, "no-existe.mp3")));
    }

    [Fact]
    public void LooksPlayable_rejects_a_truncated_download_against_the_manifest_size()
    {
        byte[] full = RealMp3();

        // Cortada al 60 %: pesa de sobra, así que solo la compara con el manifiesto la detecta.
        string truncated = Write(full[..(int)(full.Length * 0.6)]);
        Assert.True(new FileInfo(truncated).Length > TrackSanity.MinimumBytes);
        Assert.False(TrackSanity.LooksPlayable(truncated, full.Length));

        // Completa: mismo tamaño que el manifiesto → válida.
        string complete = Write(full, "completa.mp3");
        Assert.True(TrackSanity.LooksPlayable(complete, full.Length));
    }

    [Fact]
    public void Falls_back_to_duration_when_the_manifest_reports_no_size()
    {
        // Sin tamaño esperado, un fragmento de 0,5 s se descarta por la cabecera.
        Assert.False(TrackSanity.LooksPlayable(Write(SyntheticMp3(0.5))));

        // Un m4a no se puede medir sin decodificar: solo se comprueba el peso.
        byte[] m4a = [0x00, 0x00, 0x00, 0x20, 0x66, 0x74, 0x79, 0x70, 0x69, 0x73, 0x6F, 0x6D];
        Assert.Null(TrackSanity.EstimateMpegDuration(Write(m4a, "audio.m4a")));

        // Pasa a 'ruido' si pesa lo suficiente.
        Assert.True(TrackSanity.LooksPlayable(Write(new byte[TrackSanity.MinimumBytes + 10], "ruido.bin")));
    }
}
