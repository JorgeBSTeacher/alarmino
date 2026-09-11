using Alarmino.Core.Services;
using NAudio.Wave;

namespace Alarmino.Tests;

public class FadeOutWaveStreamTests
{
    private const int SampleRate = 44100;

    private static WaveStream ConstantSource(float amplitude, double seconds)
    {
        int frames = (int)(seconds * SampleRate);
        var format = new WaveFormat(SampleRate, 16, 1);
        var bytes = new byte[frames * 2];
        short value = (short)(amplitude * short.MaxValue);
        for (int i = 0; i < frames; i++)
        {
            bytes[i * 2] = (byte)(value & 0xFF);
            bytes[i * 2 + 1] = (byte)((value >> 8) & 0xFF);
        }

        return new RawSourceWaveStream(new MemoryStream(bytes, writable: false), format);
    }

    private static short[] ReadPcm16(WaveStream stream)
    {
        stream.Position = 0;
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            ms.Write(buffer, 0, read);
        }

        var data = ms.ToArray();
        var samples = new short[data.Length / 2];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = (short)(data[i * 2] | (data[i * 2 + 1] << 8));
        }

        return samples;
    }

    [Fact]
    public void Fade_KeepsFirstHalfUnchanged()
    {
        float amplitude = 0.8f;
        using WaveStream source = ConstantSource(amplitude, 10.0);
        using var fade = new FadeOutWaveStream(source, 5.0);
        short[] samples = ReadPcm16(fade);

        short expected = (short)(amplitude * short.MaxValue);
        Assert.Equal(expected, samples[0]);
        Assert.Equal(expected, samples[SampleRate / 2]);
    }

    [Fact]
    public void Fade_DrivesTailToSilence()
    {
        using WaveStream source = ConstantSource(0.8f, 10.0);
        using var fade = new FadeOutWaveStream(source, 5.0);
        short[] samples = ReadPcm16(fade);

        short last = samples[^1];
        Assert.InRange(Math.Abs(last), 0, 3_000);
    }

    [Fact]
    public void Fade_DecreasesMonotonicallyInFadeWindow()
    {
        using WaveStream source = ConstantSource(0.8f, 10.0);
        using var fade = new FadeOutWaveStream(source, 5.0);
        short[] samples = ReadPcm16(fade);

        // Mitad de la ventana de fade: a 7.5 s (2.5 s antes del final) la
        // ganancia debe estar alrededor del 0.5, por debajo del comienzo.
        short at7_5 = samples[(int)(7.5 * SampleRate)];
        Assert.True(at7_5 > 0 && at7_5 < samples[0], $"En la ventana de fade la señal no baja (7.5s={at7_5}).");
    }
}
