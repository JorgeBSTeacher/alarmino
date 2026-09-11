using System.IO;
using NAudio.Wave;

namespace Alarmino.Services;

/// <summary>
/// Genera sonidos sintetizados (CAMBELA, TIMBRE, TONO ESCOLAR) en un WAV en memoria.
/// Sin dependencias externas y sin problemas de copyright.
/// </summary>
public static class PresetSynthesizer
{
    private const int SampleRate = 44100;

    public static WaveStream Create(string presetId)
    {
        double duration = presetId switch
        {
            "ring" => 3.4,
            "schoolTone" => 4.2,
            _ => 3.6,
        };

        var ms = new MemoryStream();
        using (var writer = new WaveFileWriter(ms, new WaveFormat(SampleRate, 16, 1)))
        {
            int samples = (int)(duration * SampleRate);
            for (int i = 0; i < samples; i++)
            {
                double t = (double)i / SampleRate;
                float s = presetId switch
                {
                    "ring" => Ring(t),
                    "schoolTone" => School(t),
                    _ => Bell(t),
                };
                writer.WriteSample(s);
            }
        }

        // El MemoryStream pasa a ser propiedad del reader (NO usar `using` aquí).
        ms.Position = 0;
        return new WaveFileReader(ms);
    }

    // ---------- Presets ----------

    private static float Bell(double t)
    {
        float s = 0f;
        // Golpe 1 @ 0.0s
        s += DecayingTone(t, 880, 2.6);
        s += 0.45f * DecayingTone(t, 1760, 1.8);
        // Golpe 2 @ 1.3s
        s += 0.85f * DecayingTone(t - 1.3, 660, 2.4);
        s += 0.30f * DecayingTone(t - 1.3, 1320, 1.5);
        return Clamp(s);
    }

    private static float Ring(double t)
    {
        double cycle = t % 0.6;
        double freq = cycle < 0.3 ? 820 : 600;
        float s = 0.45f * MathF.Sin((float)(2 * Math.PI * freq * t));
        s += 0.20f * MathF.Sin((float)(2 * Math.PI * freq * 2 * t));

        float env;
        if (cycle < 0.3)
        {
            env = MathF.Min(1f, (float)(cycle / 0.015))
                * MathF.Min(1f, (float)((0.3 - cycle) / 0.05));
        }
        else
        {
            env = MathF.Min(1f, (float)((cycle - 0.3) / 0.015))
                * MathF.Min(1f, (float)((0.6 - cycle) / 0.05));
        }

        return Clamp(s * env * 0.70f);
    }

    private static float School(double t)
    {
        double vibrato = 440 + 5 * Math.Sin(2 * Math.PI * 5.5 * t);
        float s = 0.50f * MathF.Sin((float)(2 * Math.PI * vibrato * t));
        s += 0.15f * MathF.Sin((float)(2 * Math.PI * 880 * t));

        double total = 4.2;
        double fadeIn = Math.Min(1.0, t / 0.08);
        double fadeOut = Math.Min(1.0, Math.Max(0.0, (total - t)) / 0.15);
        return Clamp(s * (float)(fadeIn * fadeOut));
    }

    // ---------- Helpers ----------

    private static float DecayingTone(double t, double freq, double decayRate)
    {
        if (t <= 0) return 0f;
        double env = Math.Exp(-decayRate * t) * Math.Min(1.0, t / 0.005);
        return 0.50f * MathF.Sin((float)(2 * Math.PI * freq * t)) * (float)env;
    }

    private static float Clamp(float v) => Math.Clamp(v, -1f, 1f);
}
