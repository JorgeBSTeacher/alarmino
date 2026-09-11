using System.IO;
using System.IO.Compression;

namespace Alarmino.Installer;

/// <summary>
/// Extrae el paquete .zip adjunto al final de este mismo ejecutable
/// (formato: [exe][zip][marca de 16 bytes][longitud de 8 bytes]).
/// </summary>
public static class SelfExtractor
{
    private const string Marker = "ALARMINO_PAYLOAD";
    private const int MarkerLength = 16;

    public static long FindPayloadOffset(string exePath, out long payloadLength)
    {
        payloadLength = 0;
        using var fs = File.OpenRead(exePath);
        long fileLength = fs.Length;
        int trailer = MarkerLength + sizeof(long);

        if (fileLength < trailer)
        {
            return -1;
        }

        fs.Position = fileLength - sizeof(long);
        var lenBytes = new byte[sizeof(long)];
        fs.ReadExactly(lenBytes, 0, sizeof(long));
        payloadLength = BitConverter.ToInt64(lenBytes, 0);

        fs.Position = fileLength - trailer;
        var markerBytes = new byte[MarkerLength];
        fs.ReadExactly(markerBytes, 0, MarkerLength);

        long offset = fileLength - trailer - payloadLength;
        if (offset < 0)
        {
            return -1;
        }

        return System.Text.Encoding.ASCII.GetString(markerBytes) == Marker ? offset : -1;
    }

    public static bool HasPayload(string exePath)
        => FindPayloadOffset(exePath, out _) >= 0;

    /// <summary>Extrae todas las entradas del paquete adjunto a <paramref name="targetDir"/>.</summary>
    public static void ExtractTo(string targetDir, string? exePath = null)
    {
        string exe = exePath ?? Environment.ProcessPath
            ?? throw new InvalidOperationException("No se pudo obtener la ruta del ejecutable.");

        long offset = FindPayloadOffset(exe, out long length);
        if (offset < 0)
        {
            throw new InvalidOperationException("Este ejecutable no contiene el paquete de la aplicación.");
        }

        Directory.CreateDirectory(targetDir);

        using var fs = File.OpenRead(exe);
        using var sub = new BoundedStream(fs, offset, length, leaveOpen: true);
        using var archive = new ZipArchive(sub, ZipArchiveMode.Read, leaveOpen: true);

        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/'))
            {
                continue;
            }

            string destination = Path.Combine(targetDir, entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            using var input = entry.Open();
            using var output = File.Create(destination);
            input.CopyTo(output);
        }
    }
}

/// <summary>Stream de solo lectura limitado a un tramo del stream base.</summary>
public sealed class BoundedStream : Stream
{
    private readonly Stream _base;
    private readonly long _offset;
    private readonly long _length;
    private readonly bool _leaveOpen;
    private long _position;

    public BoundedStream(Stream baseStream, long offset, long length, bool leaveOpen)
    {
        _base = baseStream;
        _leaveOpen = leaveOpen;
        _offset = offset;
        _length = length;
        _position = 0;
        _base.Position = offset;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;
    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_position >= _length)
        {
            return 0;
        }

        int toRead = (int)Math.Min(count, _length - _position);
        if (_base.Position != _offset + _position)
        {
            _base.Position = _offset + _position;
        }

        int read = _base.Read(buffer, offset, toRead);
        _position += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        _position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => _position,
        };
        _base.Position = _offset + _position;
        return _position;
    }

    public override void Flush() { }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_leaveOpen)
        {
            _base.Dispose();
        }

        base.Dispose(disposing);
    }
}
