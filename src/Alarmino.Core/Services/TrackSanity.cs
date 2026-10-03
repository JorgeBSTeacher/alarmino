namespace Alarmino.Core.Services;

/// <summary>
/// Comprobaciones baratas para descartar una descarga de audio incompleta o
/// corrupta (típico cuando la red se corta a media descarga) sin depender del
/// códec del sistema, que en Linux no existe.
/// </summary>
public static class TrackSanity
{
    /// <summary>Peso mínimo razonable para un fragmento de audio (10 KB).</summary>
    public const int MinimumBytes = 10 * 1024;

    private static readonly TimeSpan MinimumDuration = TimeSpan.FromSeconds(1);

    private static readonly int[] BitratesMpeg1Layer3 =
        [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0, 0];

    private static readonly int[] BitratesMpeg2Layer3 =
        [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0, 0];

    /// <summary>
    /// True si el archivo parece un fragmento de audio utilizable. Solo es una
    /// heurística: pesa lo suficiente y, si es MP3, dura lo suficiente.
    /// </summary>
    public static bool LooksPlayable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            if (new FileInfo(path).Length < MinimumBytes)
            {
                return false;
            }

            TimeSpan? duration = EstimateMpegDuration(path);

            // Si no es MP3 (m4a/aac dentro de MP4) no se puede medir sin decodificar:
            // nos quedamos con la comprobación de peso.
            return duration is null || duration >= MinimumDuration;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Duración aproximada de un MP3 (CBR) a partir de su primera trama MPEG, o
    /// null si el archivo no parece MP3.
    /// </summary>
    public static TimeSpan? EstimateMpegDuration(string path)
    {
        byte[] head = ReadHead(path, 4096);
        if (head.Length < 4)
        {
            return null;
        }

        for (int i = 0; i + 3 < head.Length; i++)
        {
            if (head[i] != 0xFF || (head[i + 1] & 0xE0) != 0xE0)
            {
                continue;
            }

            int version = (head[i + 1] >> 3) & 3;  // 3 = MPEG1, 2 = MPEG2
            int layer = (head[i + 1] >> 1) & 3;     // 1 = Layer III
            int bitrateIndex = (head[i + 2] >> 4) & 0xF;
            int rateIndex = (head[i + 2] >> 2) & 0x3;

            if (version is 0 or 1 || layer != 1 || bitrateIndex is 0 or 15 || rateIndex == 3)
            {
                continue;
            }

            int bitrate = (version == 3 ? BitratesMpeg1Layer3 : BitratesMpeg2Layer3)[bitrateIndex];
            if (bitrate <= 0)
            {
                return null;
            }

            long length = new FileInfo(path).Length;
            return TimeSpan.FromSeconds(length * 8d / (bitrate * 1000d));
        }

        return null;
    }

    private static byte[] ReadHead(string path, int maxBytes)
    {
        using var stream = File.OpenRead(path);
        byte[] buffer = new byte[Math.Min(maxBytes, (int)Math.Min(int.MaxValue, stream.Length))];
        int read = 0;
        while (read < buffer.Length)
        {
            int n = stream.Read(buffer, read, buffer.Length - read);
            if (n <= 0)
            {
                break;
            }

            read += n;
        }

        return read == buffer.Length ? buffer : buffer[..read];
    }
}
