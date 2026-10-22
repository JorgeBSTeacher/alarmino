using Alarmino.Core.Services;
using Xunit;

namespace Alarmino.Tests;

public sealed class LiveDiagnosticsTests
{
    private static DiagnosticsSnapshot Snapshot(
        NetworkState network = NetworkState.Online,
        int ready = 2,
        int target = 3,
        IReadOnlyList<CachedTrackInfo>? tracks = null,
        bool downloadActive = false,
        string playerState = "En espera") =>
        new(
            network,
            DateTime.UtcNow,
            ready,
            target,
            tracks ?? [],
            downloadActive,
            playerState);

    [Fact]
    public void Describe_shows_network_track_availability_and_player_state()
    {
        IReadOnlyList<string> lines = DiagnosticsFormatter.Describe(Snapshot());

        Assert.Contains(lines, l => l.StartsWith("Red: con conexión", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("Pista descargada: disponible (2 de 3 listas)", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("Reproductor: En espera", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("Descarga en curso: no", StringComparison.Ordinal));
    }

    [Fact]
    public void Describe_marks_no_track_available_when_the_buffer_is_empty()
    {
        IReadOnlyList<string> lines = DiagnosticsFormatter.Describe(Snapshot(ready: 0, target: 3));

        Assert.Contains(lines, l => l.StartsWith("Pista descargada: NINGUNA disponible (0 de 3)", StringComparison.Ordinal));
    }

    [Fact]
    public void Describe_shows_unknown_network_before_the_first_check()
    {
        var snapshot = new DiagnosticsSnapshot(
            NetworkState.Unknown, null, 0, 3, [], false, "En espera");

        IReadOnlyList<string> lines = DiagnosticsFormatter.Describe(snapshot);

        Assert.Contains(lines, l => l.StartsWith("Red: sin comprobar", StringComparison.Ordinal));
    }

    [Fact]
    public void Describe_lists_every_cached_track_with_its_state()
    {
        var tracks = new[]
        {
            new CachedTrackInfo("PL1", "vid1", Consumed: false, FileExists: true, Bytes: 1_200_000, DateTime.UtcNow),
            new CachedTrackInfo("PL2", "vid2", Consumed: true, FileExists: true, Bytes: 500, DateTime.UtcNow),
            new CachedTrackInfo("PL3", "vid3", Consumed: false, FileExists: false, Bytes: 0, DateTime.UtcNow),
        };

        IReadOnlyList<string> lines = DiagnosticsFormatter.Describe(Snapshot(tracks: tracks));

        Assert.Contains(lines, l => l.Contains("PL1 · vid1 · 1.1 MB · sin usar", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("PL2 · vid2 · 500 B · ya reproducida", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("PL3 · vid3 · — · archivo ausente", StringComparison.Ordinal));
    }

    [Fact]
    public void Describe_says_when_the_cache_is_empty()
    {
        IReadOnlyList<string> lines = DiagnosticsFormatter.Describe(Snapshot());

        Assert.Contains(lines, l => l.Contains("no hay ninguna pista en caché", StringComparison.Ordinal));
    }

    [Fact]
    public void FormatBytes_uses_readable_units()
    {
        Assert.Equal("512 B", DiagnosticsFormatter.FormatBytes(512));
        Assert.Equal("1 KB", DiagnosticsFormatter.FormatBytes(1024));
        Assert.Equal("1.5 KB", DiagnosticsFormatter.FormatBytes(1536));
        Assert.Equal("1 MB", DiagnosticsFormatter.FormatBytes(1024 * 1024));
    }

    [Fact]
    public async Task Monitor_probes_once_and_reuses_the_result_within_the_interval()
    {
        var probe = new FakeProbe(true);
        DateTime now = new(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var monitor = new NetworkStatusMonitor(probe, TimeSpan.FromSeconds(15), () => now);

        Assert.Equal(NetworkState.Unknown, monitor.State);

        Assert.Equal(NetworkState.Online, await monitor.RefreshAsync());
        Assert.Equal(1, probe.Calls);

        now = now.AddSeconds(10);
        Assert.Equal(NetworkState.Online, await monitor.RefreshAsync());
        Assert.Equal(1, probe.Calls);
    }

    [Fact]
    public async Task Monitor_rechecks_after_the_interval()
    {
        var probe = new FakeProbe(true);
        DateTime now = new(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var monitor = new NetworkStatusMonitor(probe, TimeSpan.FromSeconds(15), () => now);

        await monitor.RefreshAsync();
        now = now.AddSeconds(15);

        Assert.True(monitor.IsStale);
        await monitor.RefreshAsync();

        Assert.Equal(2, probe.Calls);
    }

    [Fact]
    public async Task Monitor_treats_a_failing_probe_as_offline()
    {
        var probe = new FakeProbe(false) { Throws = true };
        var monitor = new NetworkStatusMonitor(probe, TimeSpan.FromSeconds(15));

        Assert.Equal(NetworkState.Offline, await monitor.RefreshAsync());
        Assert.NotNull(monitor.CheckedUtc);
    }

    [Fact]
    public async Task Monitor_force_rechecks_even_within_the_interval()
    {
        var probe = new FakeProbe(true);
        var monitor = new NetworkStatusMonitor(probe, TimeSpan.FromMinutes(10));

        await monitor.RefreshAsync();
        await monitor.RefreshAsync(force: true);

        Assert.Equal(2, probe.Calls);
    }

    private sealed class FakeProbe(bool online) : IConnectivityProbe
    {
        public int Calls { get; private set; }

        public bool Throws { get; init; }

        public Task<bool> IsOnlineAsync()
        {
            Calls++;

            if (Throws)
            {
                throw new InvalidOperationException("sin red");
            }

            return Task.FromResult(online);
        }
    }
}
