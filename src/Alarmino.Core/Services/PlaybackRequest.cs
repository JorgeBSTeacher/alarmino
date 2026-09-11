using Alarmino.Core.Models;

namespace Alarmino.Core.Services;

/// <summary>Plan de reproducción construido por la UI a partir de una alarma y el volumen global.</summary>
public sealed class PlaybackRequest
{
    public SoundSelection Sound { get; }

    public PlaybackMode Mode { get; }

    public int CustomDurationSeconds { get; }

    public int RepeatCount { get; }

    public int PauseBetweenRepeatSeconds { get; }

    public int VolumePercent { get; }

    public PlaybackRequest(SoundSelection sound, PlaybackMode mode, int customDurationSeconds,
        int repeatCount, int pauseBetweenRepeatSeconds, int volumePercent)
    {
        Sound = sound;
        Mode = mode;
        CustomDurationSeconds = customDurationSeconds;
        RepeatCount = repeatCount;
        PauseBetweenRepeatSeconds = pauseBetweenRepeatSeconds;
        VolumePercent = volumePercent;
    }

    public bool UsesFile => Sound.Kind == SoundSourceKind.File;
}
