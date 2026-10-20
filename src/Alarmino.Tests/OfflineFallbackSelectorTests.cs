using Alarmino.Core.Services;

namespace Alarmino.Tests;

public sealed class OfflineFallbackSelectorTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "alarmino-fallback-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    /// <summary>Contenido que supera <see cref="TrackSanity.LooksPlayable"/> (40 KB con cabecera MPEG válida).</summary>
    private static byte[] PlayableContent()
    {
        byte[] data = new byte[40 * 1024];
        data[0] = 0xFF;
        data[1] = 0xFB;
        data[2] = 0xA0;
        return data;
    }

    private string CreateSound(string name, byte[]? content = null)
    {
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, content ?? PlayableContent());
        return path;
    }

    [Fact]
    public void Select_prefers_the_configured_file_when_it_is_valid()
    {
        string configured = CreateSound("elegido.mp3");

        string? result = OfflineFallbackSelector.Select(configured, Array.Empty<string>());

        Assert.Equal(configured, result);
    }

    [Fact]
    public void Select_ignores_a_configured_file_that_no_longer_exists()
    {
        string candidato = CreateSound("candidato.mp3");
        var candidates = new string[] { _dir };

        string? result = OfflineFallbackSelector.Select(Path.Combine(_dir, "ya-no-esta.mp3"), candidates);

        Assert.Equal(candidato, result);
    }

    [Fact]
    public void Select_ignores_a_configured_file_that_is_not_playable()
    {
        CreateSound("roto.mp3", "cortada"u8.ToArray());
        string valid = CreateSound("bueno.mp3");

        string? result = OfflineFallbackSelector.Select(Path.Combine(_dir, "roto.mp3"), [_dir]);

        Assert.Equal(valid, result);
    }

    [Fact]
    public void Select_finds_a_default_sound_in_a_candidate_directory()
    {
        string beep = CreateSound("avion.mp3");

        string? result = OfflineFallbackSelector.Select(null, [_dir]);

        Assert.Equal(beep, result);
    }

    [Fact]
    public void Select_returns_null_when_there_is_no_valid_candidate()
    {
        CreateSound("roto.mp3", "cortada"u8.ToArray());

        string? result = OfflineFallbackSelector.Select(null, [_dir]);

        Assert.Null(result);
    }

    [Fact]
    public void Select_reports_the_directories_and_files_it_examined()
    {
        CreateSound("avion.mp3");

        string? result = OfflineFallbackSelector.Select(null, [_dir], out IReadOnlyList<string> examined);

        Assert.NotNull(result);
        Assert.Contains(_dir, examined);
        Assert.Contains(Path.Combine(_dir, "avion.mp3"), examined);
    }
}
