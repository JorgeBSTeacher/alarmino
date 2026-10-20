using Alarmino.Core.Services;
using Xunit;

namespace Alarmino.Tests;

/// <summary>
/// El log del arranque debe confirmar el respaldo sin conexión en los DOS casos,
/// no solo cuando falta: el camino normal (el archivo configurado sigue siendo
/// válido) antes no registraba nada.
/// </summary>
public sealed class OfflineFallbackReadinessTests
{
    [Fact]
    public void A_validated_configured_fallback_produces_a_log_line()
    {
        var outcome = new OfflineFallbackOutcome(@"C:\datos\respaldo.mp3", WasConfiguredPathKept: true, [@"C:\datos\respaldo.mp3"]);

        string line = OfflineFallbackReadiness.Describe(outcome);

        Assert.Contains("validado", line);
        Assert.Contains(@"C:\datos\respaldo.mp3", line);
    }

    [Fact]
    public void A_newly_chosen_fallback_produces_a_log_line()
    {
        var outcome = new OfflineFallbackOutcome(@"C:\presets\tono.mp3", WasConfiguredPathKept: false, [@"C:\presets"]);

        string line = OfflineFallbackReadiness.Describe(outcome);

        Assert.Contains("establecido", line);
        Assert.Contains(@"C:\presets\tono.mp3", line);
    }

    [Fact]
    public void A_missing_fallback_is_reported_with_what_was_examined()
    {
        var outcome = new OfflineFallbackOutcome(null, WasConfiguredPathKept: false, [@"C:\a", @"C:\b"]);

        string line = OfflineFallbackReadiness.Describe(outcome);

        Assert.Contains("sin respaldo", line);
        Assert.Contains(@"C:\a", line);
        Assert.Contains(@"C:\b", line);
    }
}
