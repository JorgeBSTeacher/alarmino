using Alarmino.Core.Models;

namespace Alarmino.Core.Services;

/// <summary>Reloj inyectable para poder testear el scheduler de forma determinista.</summary>
public interface ITimeProvider
{
    DateTimeOffset Now { get; }
}

/// <summary>Reloj real del sistema.</summary>
public sealed class SystemTimeProvider : ITimeProvider
{
    public DateTimeOffset Now => DateTimeOffset.Now;
}

/// <summary>Reloj falso usado en tests.</summary>
public sealed class FakeTimeProvider : ITimeProvider
{
    public DateTimeOffset Now { get; set; }

    public FakeTimeProvider(DateTimeOffset now) => Now = now;

    public void Advance(TimeSpan delta) => Now = Now.Add(delta);
}

/// <summary>Resultado de evaluar el tick actual del scheduler.</summary>
public sealed class TickResult
{
    public IReadOnlyList<Alarm> ToPlay { get; init; } = [];

    public IReadOnlyList<MissedAlarm> Missed { get; init; } = [];
}

public sealed record MissedAlarm(Alarm Alarm, DateTime ScheduledAt);

/// <summary>
/// Evalúa reloj vs programación de alarmas. Es lógica pura (sin dependencias de UI)
/// para poder testearla con un reloj falso. El UI la llama cada 500 ms.
/// Detecta transiciones de franja: solo dispara una alarma cuando se entra en su minuto.
/// </summary>
public sealed class SchedulerEvaluator
{
    private DateTime? _lastSlot;

    /// <summary>Paso de cronómetro: evalúa un instante.</summary>
    public TickResult Evaluate(DateTime now, IReadOnlyList<Alarm> alarms)
    {
        now = TruncateToMinute(now);

        if (_lastSlot is null)
        {
            // Recién arrancado: se establece como referencia el minuto anterior para que
            // una alarma cuyo minuto coincide con el arranque sí dispare una vez.
            _lastSlot = now.AddMinutes(-1);
        }

        DateTime last = _lastSlot.Value;
        _lastSlot = now;

        if (now <= last)
        {
            // Reloj atrasado o sin avance: no disparar nada.
            return new TickResult();
        }

        var toPlay = new List<Alarm>();
        var missed = new List<MissedAlarm>();

        // Franjas intermedias: si el avance es de más de un minuto, las franjas
        // anteriores a la actual se consideran pasadas (equipo dormido o atascado).
        for (var slot = last.AddMinutes(1); slot <= now; slot = slot.AddMinutes(1))
        {
            bool isCurrent = slot == now;
            var day = slot.DayOfWeek;
            var time = slot.TimeOfDay;

            var scheduled = alarms
                .Where(a => a.IsActive && a.Days.Matches(day) && SlotsMatch(a.Time, time))
                .OrderBy(a => a.Time)
                .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            foreach (var alarm in scheduled)
            {
                if (isCurrent)
                {
                    toPlay.Add(alarm);
                }
                else
                {
                    missed.Add(new MissedAlarm(alarm, slot));
                }
            }
        }

        return new TickResult { ToPlay = toPlay, Missed = missed };
    }

    /// <summary>Una alarma con hora HH:mm dispara en cualquier instante dentro de ese minuto.</summary>
    private static bool SlotsMatch(TimeSpan alarmTime, TimeSpan slotTime)
        => alarmTime.Hours == slotTime.Hours && alarmTime.Minutes == slotTime.Minutes;

    private static DateTime TruncateToMinute(DateTime value)
        => new(value.Year, value.Month, value.Day, value.Hour, value.Minute, 0, value.Kind);

    public void Reset() => _lastSlot = null;
}
