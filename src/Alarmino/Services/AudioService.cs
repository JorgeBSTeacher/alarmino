using System.IO;
using Alarmino.Core.Models;
using Alarmino.Core.Services;
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
    private WaveOutEvent? _current;
    private CancellationTokenSource? _cts;

    public AudioService(
        SoundLibrary? library = null,
        Func<string, string?>? resolvePresetFile = null,
        IYouTubeAudioResolver? youtube = null,
        Func<string?>? resolveFallbackFile = null)
    {
        _library = library ?? new SoundLibrary();
        _resolvePresetFile = resolvePresetFile ?? (_ => null);
        _youtube = youtube;
        _resolveFallbackFile = resolveFallbackFile;

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
            JsonDataStore.AppendLogLine(null, $"Error de reproducción: {ex.Message}");
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
            JsonDataStore.AppendLogLine(null, "YouTube: servicio no disponible.");
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

            if (request.Mode == PlaybackMode.CustomDuration && request.CustomDurationSeconds > 0)
            {
                source = new TruncatedWaveStream(source, TimeSpan.FromSeconds(request.CustomDurationSeconds));
                source = new FadeOutWaveStream(source, 5);
            }

            var output = new WaveOutEvent();
            output.Init(source);
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
                    JsonDataStore.AppendLogLine(null, $"Error de reproducción: {args.Exception.Message}");
                }

                output.Dispose();
                source.Dispose();
                completion.TrySetResult(true);
            };

            lock (_gate)
            {
                _current?.Stop();
                _current = output;
            }

            output.Play();
        }
        catch (Exception ex)
        {
            JsonDataStore.AppendLogLine(null, $"Error abriendo audio ({request.Sound.Kind}): {ex.Message}");
            completion.TrySetResult(true);
        }

        await completion.Task;
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
