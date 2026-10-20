using Alarmino.Core.Services;

namespace Alarmino.Tests;

public sealed class PlayerStatusFormatterTests
{
    [Fact]
    public void Everything_idle_reports_the_waiting_state()
    {
        Assert.Equal("En espera", PlayerStatusFormatter.Describe(false, false, null, TimeSpan.Zero));
    }

    [Fact]
    public void A_download_in_flight_is_reported_as_downloading()
    {
        Assert.Equal("Descargando…", PlayerStatusFormatter.Describe(false, true, null, TimeSpan.Zero));
    }

    [Fact]
    public void A_running_download_outranks_a_previous_label()
    {
        Assert.Equal("Descargando…", PlayerStatusFormatter.Describe(false, true, "la alarma «Recreo»", TimeSpan.Zero));
    }

    [Fact]
    public void Playing_reports_the_elapsed_seconds()
    {
        Assert.Equal("Reproduciendo — 00:12", PlayerStatusFormatter.Describe(true, false, null, TimeSpan.FromSeconds(12)));
    }

    [Fact]
    public void Playing_with_an_hour_or_more_keeps_the_hour_part()
    {
        Assert.Equal(
            "Reproduciendo — 01:05:07",
            PlayerStatusFormatter.Describe(true, false, null, new TimeSpan(1, 5, 7)));
    }

    [Fact]
    public void A_download_does_not_hide_active_playback()
    {
        Assert.Equal(
            "Reproduciendo — 00:03",
            PlayerStatusFormatter.Describe(true, true, null, TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void A_negative_elapsed_is_clamped_instead_of_showing_a_broken_counter()
    {
        Assert.Equal("Reproduciendo — 00:00", PlayerStatusFormatter.Describe(true, false, null, TimeSpan.FromSeconds(-5)));
    }
}
