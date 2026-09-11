using Alarmino.Core.Services;
using NAudio.Wave;

namespace Alarmino.Tests;

public class PresetSynthesizerTests
{
    private static readonly byte[] Buffer = new byte[64 * 1024];

    private static WaveStream Create(string presetId) => PresetSynthesizer.Create(presetId);

    private static byte[] ReadAll(WaveStream stream)
    {
        stream.Position = 0;
        using var ms = new MemoryStream();
        int read;
        while ((read = stream.Read(Buffer, 0, Buffer.Length)) > 0)
        {
            ms.Write(Buffer, 0, read);
        }

        return ms.ToArray();
    }

    private static double ComputeRms(byte[] pcm16)
    {
        double sum = 0;
        int frames = pcm16.Length / 2;
        for (int i = 0; i < frames; i++)
        {
            short sample = (short)(pcm16[i * 2] | (pcm16[i * 2 + 1] << 8));
            double v = sample / (double)short.MaxValue;
            sum += v * v;
        }

        return frames == 0 ? 0 : Math.Sqrt(sum / frames);
    }

    public static TheoryData<string> PresetIds()
    {
        var data = new TheoryData<string> { "bell", "ring", "schoolTone" };
        return data;
    }

    [Theory]
    [MemberData(nameof(PresetIds))]
    public void Preset_ProducesNonNullSignal(string presetId)
    {
        using WaveStream stream = Create(presetId);
        byte[] data = ReadAll(stream);

        Assert.NotEmpty(data);
        Assert.True(data.Length >= 2);
    }

    [Theory]
    [MemberData(nameof(PresetIds))]
    public void Preset_HasExpectedDuration(string presetId)
    {
        using WaveStream stream = Create(presetId);
        double totalSeconds = (double)stream.Length / stream.WaveFormat.AverageBytesPerSecond;

        Assert.InRange(totalSeconds, 3.0, 5.0);
    }

    [Theory]
    [MemberData(nameof(PresetIds))]
    public void Preset_IsAudible(string presetId)
    {
        using WaveStream stream = Create(presetId);
        byte[] data = ReadAll(stream);
        double rms = ComputeRms(data);

        // Por debajo de ~ -26 dB sería inaudible o un simple clic; los presets
        // deben sonar con energía real.
        Assert.True(rms > 0.05, $"El preset «{presetId}» sale casi mudo (RMS={rms:F4}).");
    }
}
