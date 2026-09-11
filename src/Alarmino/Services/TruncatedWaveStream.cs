using NAudio.Wave;

namespace Alarmino.Services;

/// <summary>Envuelve un WaveStream y limita su longitud a una duración máxima.</summary>
public sealed class TruncatedWaveStream : WaveStream
{
    private readonly WaveStream _source;
    private readonly long _maxBytes;

    public TruncatedWaveStream(WaveStream source, TimeSpan duration)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _maxBytes = Math.Max(0, Math.Min(source.Length,
            (long)(duration.TotalSeconds * source.WaveFormat.AverageBytesPerSecond)));
    }

    public override WaveFormat WaveFormat => _source.WaveFormat;
    public override long Length => _maxBytes;

    public override long Position
    {
        get => Math.Min(_source.Position, _maxBytes);
        set => _source.Position = Math.Clamp(value, 0, _maxBytes);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_source.Position >= _maxBytes)
            return 0;

        int toRead = (int)Math.Min(count, _maxBytes - _source.Position);
        int read = _source.Read(buffer, offset, toRead);
        return read;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _source.Dispose();
        }

        base.Dispose(disposing);
    }
}
