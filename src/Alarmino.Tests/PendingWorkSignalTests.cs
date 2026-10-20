using Alarmino.Core.Services;

namespace Alarmino.Tests;

public sealed class PendingWorkSignalTests
{
    [Fact]
    public void TryTake_is_false_until_something_is_requested()
    {
        var signal = new PendingWorkSignal();

        Assert.False(signal.TryTake());
        Assert.False(signal.IsPending);
    }

    [Fact]
    public void Request_then_TryTake_reports_exactly_one_unit_of_work()
    {
        var signal = new PendingWorkSignal();

        Assert.True(signal.Request());
        Assert.True(signal.TryTake());

        // El trabajo ya se recogió: no queda nada pendiente.
        Assert.False(signal.TryTake());
    }

    [Fact]
    public void Repeated_requests_coalesce_into_a_single_unit_of_work()
    {
        var signal = new PendingWorkSignal();

        // Muchos cambios de ajustes seguidos (como los 30+ llamadores de
        // PersistSettings) no deben programar un trabajo cada uno.
        for (int i = 0; i < 50; i++)
        {
            signal.Request();
        }

        Assert.True(signal.TryTake());
        Assert.False(signal.TryTake());
    }

    [Fact]
    public void Request_while_pending_reports_it_did_not_raise_a_new_signal()
    {
        var signal = new PendingWorkSignal();

        Assert.True(signal.Request());
        // La segunda petición no levanta nada nuevo: ya había trabajo pendiente.
        Assert.False(signal.Request());
    }

    [Fact]
    public void A_request_arriving_while_working_is_taken_by_the_next_pickup()
    {
        var signal = new PendingWorkSignal();

        signal.Request();
        Assert.True(signal.TryTake());

        // Un cambio de ajustes llega mientras el barrido está en marcha.
        Assert.True(signal.Request());
        Assert.True(signal.IsPending);

        // El siguiente recojo sí lo ve: no se pierde.
        Assert.True(signal.TryTake());
        Assert.False(signal.TryTake());
    }

    [Fact]
    public void Concurrent_requests_produce_a_single_pending_unit()
    {
        var signal = new PendingWorkSignal();

        Parallel.For(0, 1000, _ => signal.Request());

        Assert.True(signal.TryTake());
        Assert.False(signal.TryTake());
    }
}
