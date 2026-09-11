using NAudio.Wave;

namespace Alarmino.Core.Services;

/// <summary>
/// Envuelve un <see cref="WaveStream"/> PCM de 16 bits y aplica un desvanecimiento
/// (fade out) lineal en los últimos <paramref name="fadeSeconds"/> segundos.
/// Usado en reproducción con duración personalizada para un cierre suave.
/// </summary>
public sealed class FadeOutWaveStream : WaveStream
{
    private readonly WaveStream _source;
    private readonly double _fadeSeconds;

    public FadeOutWaveStream(WaveStream source, double fadeSeconds)
    {
        _source = source;
        _fadeSeconds = Math.Max(0, fadeSeconds);
    }

    public override WaveFormat WaveFormat => _source.WaveFormat;

    public override long Length => _source.Length;

    public override long Position
    {
        get => _source.Position;
        set => _source.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        if (read <= 0 || _fadeSeconds <= 0)
        {
            return read;
        }

        WaveFormat fmt = _source.WaveFormat;
        if (fmt.BitsPerSample != 16)
        {
            return read; // Solo aplicamos fade a los formatos PCM de 16 bits (nuestras fuentes).
        }

        double bytesPerSecond = fmt.AverageBytesPerSecond;
        // Segundos de sonido que faltan justo antes y justo después de este fragmento.
        double startSeconds = (Length - (_source.Position - read)) / bytesPerSecond;
        double endSeconds = (Length - _source.Position) / bytesPerSecond;

        // Factor de ganancia: 1 mientras quede más de un fade, 0 al llegar al final.
        double gainStart = Gain(startSeconds);
        double gainEnd = Gain(endSeconds);

        ApplyFade16(buffer, offset, read, fmt, gainStart, gainEnd);
        return read;
    }

    /// <summary>Ganancia (0..1) según los segundos de audio que queden.</summary>
    private double Gain(double secondsRemaining)
        => Math.Clamp(secondsRemaining / _fadeSeconds, 0, 1);

    private static void ApplyFade16(byte[] buffer, int offset, int byteCount, WaveFormat fmt, double gainStart, double gainEnd)
    {
        int bytesPerSample = fmt.BitsPerSample / 8;
        int frames = byteCount / (bytesPerSample * fmt.Channels);
        for (int frame = 0; frame < frames; frame++)
        {
            double t = frames == 1 ? 0 : (double)frame / (frames - 1);
            double gain = gainStart + (gainEnd - gainStart) * t;

            for (int ch = 0; ch < fmt.Channels; ch++)
            {
                int idx = offset + (frame * fmt.Channels + ch) * bytesPerSample;
                short sample = (short)(buffer[idx] | (buffer[idx + 1] << 8));
                sample = (short)(sample * gain);
                buffer[idx] = (byte)(sample & 0xFF);
                buffer[idx + 1] = (byte)((sample >> 8) & 0xFF);
            }
        }
    }
}
