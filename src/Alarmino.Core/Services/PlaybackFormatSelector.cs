using NAudio.Wave;

namespace Alarmino.Core.Services;

/// <summary>
/// Decide si una fuente de audio necesita convertirse antes de entregarla al
/// dispositivo de salida.
/// <para>
/// Si la frecuencia de muestreo (o el número de canales) de la fuente no coincide
/// con la del dispositivo, NAudio entrega las muestras tal cual y el dispositivo las
/// reproduce a su propia frecuencia: el audio sale con el tono y la velocidad
/// cambiados. Por ejemplo, 48 kHz en un dispositivo de 44,1 kHz suena un ~9 % más
/// rápido. Esta lógica es pura (no toca ningún dispositivo) para poder probarse
/// fuera de Windows.
/// </para>
/// </summary>
public static class PlaybackFormatSelector
{
    /// <summary>
    /// Indica si hay que insertar una etapa de conversión para que la fuente suene
    /// a velocidad real (1.0x) en un dispositivo con la frecuencia y los canales
    /// indicados.
    /// </summary>
    /// <param name="source">Formato que declara la fuente.</param>
    /// <param name="deviceSampleRate">Frecuencia del dispositivo de salida, en Hz.</param>
    /// <param name="deviceChannels">Canales del dispositivo de salida.</param>
    public static bool NeedsResampling(WaveFormat source, int deviceSampleRate, int deviceChannels)
        => source.SampleRate != deviceSampleRate || source.Channels != deviceChannels;

    /// <summary>
    /// Calcula el formato de destino para la conversión: adopta la frecuencia y los
    /// canales del dispositivo y conserva la profundidad de bits de la fuente (el
    /// remuestreador no debe cambiar el formato de muestra).
    /// </summary>
    public static WaveFormat ComputeTargetFormat(WaveFormat source, int deviceSampleRate, int deviceChannels)
        => NeedsResampling(source, deviceSampleRate, deviceChannels)
            ? new WaveFormat(deviceSampleRate, source.BitsPerSample > 0 ? source.BitsPerSample : 16, deviceChannels)
            : source;
}
