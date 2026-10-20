using System.IO;
using Alarmino.Core.Models;
using Alarmino.Core.Services;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Alarmino.Services;

/// <summary>
/// Reproduce sonidos (.mp3/.wav y presets) con NAudio.
/// Gestiona el modo completo/duración personalizada, repetición con pausa y parada inmediata.
/// </summary>
public sealed class AudioService : IDisposable
{
    private readonly object _gate = new();
    private readonly SoundLibrary _library;
    private readonly Func<string, string?> _resolvePresetFile;
    private readonly IYouTubeAudioResolver? _youtube;
    private readonly Func<string?>? _resolveFallbackFile;
    private readonly Func<WaveFormat?> _resolveDeviceFormat;
    private WaveOutEvent? _current;
    private CancellationTokenSource? _cts;
    private string? _playingPlaylistUrl;
    private bool _playRecorded;

    public AudioService(
        SoundLibrary? library = null,
        Func<string, string?>? resolvePresetFile = null,
        IYouTubeAudioResolver? youtube = null,
        Func<string?>? resolveFallbackFile = null,
        Func<WaveFormat?>? resolveDeviceFormat = null)
    {
        _library = library ?? new SoundLibrary();
        _resolvePresetFile = resolvePresetFile ?? (_ => null);
        _youtube = youtube;
        _resolveFallbackFile = resolveFallbackFile;
        _resolveDeviceFormat = resolveDeviceFormat ?? DefaultDeviceFormat;

        // Los tres sonidos predeterminados vienen con la app y se copian a la
        // biblioteca la primera vez, para queResolvePresetFile los encuentre.
        _library.EnsureDefaultPresets();
    }

    public bool IsPlaying { get; private set; }

    public event Action? PlaybackFinished;

    public void Play(PlaybackRequest request)
    {
        _ = PlayAsync(request);
    }

    public async Task PlayAsync(PlaybackRequest request)
    {
        CancellationTokenSource cts;
        lock (_gate)
        {
            StopLocked();
            _cts = new CancellationTokenSource();
            cts = _cts;
            IsPlaying = true;
        }

        _playingPlaylistUrl = request.Sound.Kind == SoundSourceKind.YouTube
            ? request.Sound.YouTubePlaylistUrl
            : null;
        _playRecorded = false;

        try
        {
            // Los sonidos de YouTube se resuelven una sola vez: se elige y descarga
            // una pista aleatoria que reutilizan todas las repeticiones.
            string? youtubeFile = null;
            if (request.Sound.Kind == SoundSourceKind.YouTube)
            {
                youtubeFile = await ResolveYouTubeAsync(request.Sound.YouTubePlaylistUrl, cts.Token);
                if (youtubeFile is null)
                {
                    return; // Sin internet ni respaldo: nada que sonar.
                }
            }

            int repeats = Math.Clamp(request.RepeatCount, 1, 20);
            for (int i = 0; i < repeats; i++)
            {
                cts.Token.ThrowIfCancellationRequested();
                await PlayOnceAsync(request, cts, youtubeFile);

                lock (_gate)
                {
                    if (_cts != cts)
                    {
                        break; // parado a mitad de la reproducción
                    }
                }

                if (i < repeats - 1)
                {
                    int pauseMs = Math.Max(0, request.PauseBetweenRepeatSeconds * 1000);
                    await Task.Delay(pauseMs, cts.Token);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Parada intencional.
        }
        catch (Exception ex)
        {
            OperationLog.Write("audio", $"error de reproducción: {ex.Message}");
        }
        finally
        {
            lock (_gate)
            {
                IsPlaying = false;
                if (_cts == cts)
                {
                    _cts = null;
                    PlaybackFinished?.Invoke();
                }
            }
        }
    }

    /// <summary>
    /// Resuelve la pista de YouTube a un archivo local. Si no hay conexión o la
    /// descarga falla, reproduce en su lugar el MP3 de respaldo configurado.
    /// </summary>
    private async Task<string?> ResolveYouTubeAsync(string? playlistUrl, CancellationToken cancellationToken)
    {
        if (_youtube is null)
        {
            OperationLog.Write("audio", "servicio de YouTube no disponible; se usa el respaldo.");
            return _resolveFallbackFile?.Invoke();
        }

        return await _youtube.ResolveAsync(
            playlistUrl,
            _resolveFallbackFile?.Invoke(),
            cancellationToken);
    }

    /// <summary>
    /// Reproduce una repetición y espera a que termine (evento PlaybackStopped)
    /// antes de devolver el control, de modo que las N repeticiones quedan concatenadas
    /// (cada audio completo, sin solaparse).
    /// </summary>
    private async Task PlayOnceAsync(PlaybackRequest request, CancellationTokenSource cts, string? youtubeFile = null)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            WaveStream source = request.Sound.Kind switch
            {
                SoundSourceKind.Preset => _resolvePresetFile(request.Sound.PresetId ?? "airplaneBeep") is { } overriddenFile
                    ? OpenFile(overriddenFile)
                    : _library.ResolvePresetFile(request.Sound.PresetId) is { } file
                        ? OpenFile(file)
                        : PresetSynthesizer.Create(request.Sound.PresetId ?? "airplaneBeep"),
                SoundSourceKind.YouTube => OpenFile(youtubeFile),
                _ => OpenFile(request.Sound.FilePath),
            };

            OperationLog.Write("audio", $"se intenta reproducir ({request.Sound.Kind}).");

            if (request.Mode == PlaybackMode.CustomDuration && request.CustomDurationSeconds > 0)
            {
                source = new TruncatedWaveStream(source, TimeSpan.FromSeconds(request.CustomDurationSeconds));
                source = new FadeOutWaveStream(source, 5);
            }

            // La fuente ya está completa (recorte y desvanecimiento incluidos).
            // El remuestreo va ENVOLVIENDO ese resultado final: la fuente y sus
            // envoltorios siguen en el formato original (PCM de 16 bits), de modo
            // que el recorte y el fade calculan bien los segundos, y lo que recibe
            // el dispositivo ya está en su frecuencia, garantizando 1.0x.
            IDisposable? resampler = null;
            IWaveProvider provider = source;
            WaveFormat? deviceFormat = _resolveDeviceFormat();
            if (deviceFormat is not null
                && PlaybackFormatSelector.NeedsResampling(source.WaveFormat, deviceFormat.SampleRate, deviceFormat.Channels))
            {
                WaveFormat target = PlaybackFormatSelector.ComputeTargetFormat(
                    source.WaveFormat, deviceFormat.SampleRate, deviceFormat.Channels);
                var resampled = new MediaFoundationResampler(source, target);
                resampler = resampled;
                provider = resampled;
                OperationLog.Write("audio",
                    $"se convierte el audio de {source.WaveFormat.SampleRate} Hz/{source.WaveFormat.Channels} canales " +
                    $"a {target.SampleRate} Hz/{target.Channels} canales para sonar a velocidad normal.");
            }

            var output = new WaveOutEvent();
            output.Init(provider);
            output.Volume = Math.Clamp(request.VolumePercent / 100f, 0f, 1f);
            output.PlaybackStopped += (_, args) =>
            {
                lock (_gate)
                {
                    if (_current == output)
                    {
                        _current = null;
                    }
                }

                if (args.Exception is not null)
                {
                    OperationLog.Write("audio", $"error durante la reproducción: {args.Exception.Message}");
                }

                output.Dispose();
                // El remuestreador envuelve a la fuente, pero NAudio no lo encadena
                // a su Dispose: hay que liberar ambos (y sin doble liberación).
                resampler?.Dispose();
                source.Dispose();
                completion.TrySetResult(true);
            };

            lock (_gate)
            {
                _current?.Stop();
                _current = output;
            }

            output.Play();

            // La pista suena de verdad: es el único punto exacto para anotarla en
            // el historial (una descarga cancelada antes de sonar no cuenta).
            RecordYouTubePlayOnce();
        }
        catch (Exception ex)
        {
            OperationLog.Write("audio", $"fallo al abrir el audio ({request.Sound.Kind}): {ex.Message}");
            completion.TrySetResult(true);
        }

        await completion.Task;
    }

    /// <summary>Registra una sola vez en el historial que la pista de YouTube ha sonado.</summary>
    private void RecordYouTubePlayOnce()
    {
        if (_playRecorded || _youtube is null || _playingPlaylistUrl is null)
        {
            return;
        }

        _playRecorded = true;
        try
        {
            _youtube.RecordPlay(_playingPlaylistUrl);
        }
        catch (Exception ex)
        {
            OperationLog.Write("audio", $"no se pudo registrar la reproducción ({ex.Message}).");
        }
    }

    /// <summary>
    /// Formato del dispositivo de salida predeterminado (frecuencia y canales).
    /// Se consulta a WASAPI, que es la frecuencia a la que corre el motor de audio
    /// compartido de Windows. Si algo falla, devuelve <c>null</c> y la reproducción
    /// continúa sin conversión (se comporta como antes, nunca peor).
    /// </summary>
    private static WaveFormat? DefaultDeviceFormat()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using MMDevice device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            WaveFormat mix = device.AudioClient.MixFormat;
            return new WaveFormat(mix.SampleRate, mix.BitsPerSample, mix.Channels);
        }
        catch (Exception ex)
        {
            OperationLog.Write("audio", $"no se pudo consultar el formato del dispositivo ({ex.Message}); se reproduce sin conversión.");
            return null;
        }
    }

    private static WaveStream OpenFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new FileNotFoundException("No se encontró el archivo de sonido.", path);
        }

        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".wave" or ".wav" => new WaveFileReader(path),
            ".mp3" => new Mp3FileReader(path),
            // Pistas de YouTube (AAC) y otros formatos con códec del sistema.
            ".m4a" or ".mp4" or ".aac" => new MediaFoundationReader(path),
            _ => new WaveFileReader(path),
        };
    }

    public void Stop()
    {
        lock (_gate)
        {
            StopLocked();
        }
    }

    private void StopLocked()
    {
        _cts?.Cancel();
        _cts = null;
        IsPlaying = false;
        _current?.Stop();
        _current = null;
    }

    public void Dispose()
    {
        Stop();
        _current?.Dispose();
    }
}
