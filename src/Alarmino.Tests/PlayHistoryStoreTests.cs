using System.Text.Json;
using Alarmino.Core.Services;

namespace Alarmino.Tests;

public sealed class PlayHistoryStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "alarmino-history-" + Guid.NewGuid().ToString("N"));

    private string HistoryPath => Path.Combine(_dir, "play-history.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Entries_within_the_window_are_included()
    {
        var store = new PlayHistoryStore(_dir);
        store.Record("PL1", "videoA", DateTime.UtcNow);

        var window = store.InWindow("PL1", TimeSpan.FromDays(7), DateTime.UtcNow);

        Assert.Contains("videoA", window);
    }

    [Fact]
    public void Entries_older_than_the_window_are_left_out()
    {
        var store = new PlayHistoryStore(_dir);
        DateTime now = DateTime.UtcNow;
        store.Record("PL1", "antigua", now.AddDays(-8));
        store.Record("PL1", "reciente", now.AddDays(-1));

        var window = store.InWindow("PL1", TimeSpan.FromDays(7), now);

        Assert.Equal(["reciente"], window);
    }

    [Fact]
    public void History_is_kept_separate_per_playlist()
    {
        var store = new PlayHistoryStore(_dir);
        store.Record("PL1", "videoA", DateTime.UtcNow);
        store.Record("PL2", "videoB", DateTime.UtcNow);

        Assert.Equal(["videoA"], store.InWindow("PL1", TimeSpan.FromDays(7), DateTime.UtcNow));
        Assert.Equal(["videoB"], store.InWindow("PL2", TimeSpan.FromDays(7), DateTime.UtcNow));
    }

    [Fact]
    public void History_survives_a_restart()
    {
        DateTime now = DateTime.UtcNow;
        new PlayHistoryStore(_dir).Record("PL1", "videoA", now.AddDays(-1));

        var restarted = new PlayHistoryStore(_dir);

        Assert.Equal(["videoA"], restarted.InWindow("PL1", TimeSpan.FromDays(7), now));
    }

    [Fact]
    public void Recording_the_same_video_twice_keeps_the_latest_play()
    {
        DateTime now = DateTime.UtcNow;
        var store = new PlayHistoryStore(_dir);
        store.Record("PL1", "videoA", now.AddDays(-9));
        store.Record("PL1", "videoA", now.AddDays(-1));

        var window = store.InWindow("PL1", TimeSpan.FromDays(7), now);

        Assert.Equal(["videoA"], window);
    }

    [Fact]
    public void Missing_file_is_an_empty_history()
    {
        var store = new PlayHistoryStore(_dir);

        Assert.Empty(store.InWindow("PL1", TimeSpan.FromDays(7), DateTime.UtcNow));
    }

    [Fact]
    public void Corrupt_file_is_an_empty_history_and_does_not_throw()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(HistoryPath, "{esto no es json");

        var store = new PlayHistoryStore(_dir);
        var exception = Record.Exception(() =>
        {
            IReadOnlyList<string> window = store.InWindow("PL1", TimeSpan.FromDays(7), DateTime.UtcNow);
            Assert.Empty(window);
        });

        Assert.Null(exception);
    }

    [Fact]
    public void Record_never_throws_when_the_file_cannot_be_written()
    {
        // Un archivo ocupa el lugar de la carpeta: la escritura es imposible.
        File.WriteAllText(_dir, "bloqueado");

        var store = new PlayHistoryStore(_dir);
        var exception = Record.Exception(() =>
        {
            store.Record("PL1", "videoA", DateTime.UtcNow);
        });

        Assert.Null(exception);
    }

    [Fact]
    public void Stored_file_is_a_playlist_to_video_map()
    {
        DateTime played = new(2026, 10, 1, 8, 30, 0, DateTimeKind.Utc);
        new PlayHistoryStore(_dir).Record("PL1", "videoA", played);

        using var document = JsonDocument.Parse(File.ReadAllText(HistoryPath));

        Assert.Equal(
            played,
            document.RootElement.GetProperty("PL1").GetProperty("videoA").GetDateTime());
    }
}
