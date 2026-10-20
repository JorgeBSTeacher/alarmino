using Alarmino.Core.Services;
using NAudio.Wave;

namespace Alarmino.Tests;

public class PlaybackFormatSelectorTests
{
    [Fact]
    public void SourceAtDeviceRate_DoesNotNeedResampling()
    {
        var source = new WaveFormat(44100, 16, 2);

        Assert.False(PlaybackFormatSelector.NeedsResampling(source, 44100, 2));
    }

    [Fact]
    public void SourceAtDifferentRate_NeedsResampling()
    {
        var source = new WaveFormat(48000, 16, 2);

        Assert.True(PlaybackFormatSelector.NeedsResampling(source, 44100, 2));
    }

    [Fact]
    public void SourceWithDifferentChannelCount_NeedsResampling()
    {
        var source = new WaveFormat(44100, 16, 1);

        Assert.True(PlaybackFormatSelector.NeedsResampling(source, 44100, 2));
    }

    [Fact]
    public void TargetFormat_UsesDeviceSampleRateAndChannels()
    {
        var source = new WaveFormat(48000, 16, 2);

        WaveFormat target = PlaybackFormatSelector.ComputeTargetFormat(source, 44100, 1);

        Assert.Equal(44100, target.SampleRate);
        Assert.Equal(1, target.Channels);
        Assert.Equal(16, target.BitsPerSample);
    }

    [Fact]
    public void TargetFormat_PreservesSourceBitsPerSample()
    {
        var source = new WaveFormat(48000, 24, 2);

        WaveFormat target = PlaybackFormatSelector.ComputeTargetFormat(source, 44100, 2);

        Assert.Equal(24, target.BitsPerSample);
    }

    [Fact]
    public void TargetFormat_SourceAlreadyAtDeviceFormat_MirrorsSource()
    {
        var source = new WaveFormat(44100, 16, 2);

        WaveFormat target = PlaybackFormatSelector.ComputeTargetFormat(source, 44100, 2);

        Assert.True(target.Equals(source));
    }

    [Fact]
    public void TargetFormat_UnknownBitDepth_FallsBackToSixteen()
    {
        // Un decodificador podría no declarar bits por muestra; el remuestreador
        // debe recibir igualmente un PCM válido.
        var source = new WaveFormat(44100, 0, 2);

        WaveFormat target = PlaybackFormatSelector.ComputeTargetFormat(source, 48000, 2);

        Assert.Equal(16, target.BitsPerSample);
    }
}
