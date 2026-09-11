using Alarmino.Core.Models;
using Alarmino.Core.Services;

namespace Alarmino.Tests;

public class SchedulerEvaluatorTests
{
    private static Alarm Alarm(string name = "Test", TimeSpan? time = null, DayFlags days = DayFlags.Weekdays)
        => new()
        {
            Name = name,
            Time = time ?? new TimeSpan(9, 0, 0),
            Days = days,
            IsActive = true,
        };

    [Fact]
    public void No_fires_before_its_minute()
    {
        var sut = new SchedulerEvaluator();
        var alarm = Alarm("Entrada", new TimeSpan(9, 0, 0));

        var withinMinute = new DateTime(2026, 9, 14, 8, 59, 30); // lunes 08:59:30
        var result = sut.Evaluate(withinMinute, [alarm]);

        Assert.Empty(result.ToPlay);
    }

    [Fact]
    public void Fires_once_when_entering_matching_minute()
    {
        var sut = new SchedulerEvaluator();
        var alarm = Alarm("Entrada", new TimeSpan(9, 0, 0));

        var before = new DateTime(2026, 9, 14, 8, 59, 40); // lunes 08:59:40
        sut.Evaluate(before, [alarm]);

        var at = new DateTime(2026, 9, 14, 9, 0, 5);
        var result = sut.Evaluate(at, [alarm]);

        var fired = Assert.Single(result.ToPlay);
        Assert.Equal(alarm.Id, fired.Id);
    }

    [Fact]
    public void Does_not_fire_again_within_same_minute()
    {
        var sut = new SchedulerEvaluator();
        var alarm = Alarm("Entrada", new TimeSpan(9, 0, 0));

        sut.Evaluate(new DateTime(2026, 9, 14, 8, 59, 40), [alarm]);
        sut.Evaluate(new DateTime(2026, 9, 14, 9, 0, 5), [alarm]);

        var result = sut.Evaluate(new DateTime(2026, 9, 14, 9, 0, 45), [alarm]);
        Assert.Empty(result.ToPlay);
    }

    [Theory]
    [InlineData(DayOfWeek.Monday, true)]
    [InlineData(DayOfWeek.Tuesday, true)]
    [InlineData(DayOfWeek.Saturday, false)]
    [InlineData(DayOfWeek.Sunday, false)]
    public void Only_fires_on_matching_days(DayOfWeek day, bool expected)
    {
        var sut = new SchedulerEvaluator();
        var alarm = Alarm("Clase", new TimeSpan(10, 0, 0), DayFlags.Weekdays);

        var dayStart = new DateTime(2026, 9, 14, 9, 59, 30).AddDays(DayOffset(day));
        sut.Evaluate(dayStart, [alarm]);
        var result = sut.Evaluate(dayStart.AddMinutes(1), [alarm]);

        Assert.Equal(expected, result.ToPlay.Count == 1);
    }

    [Fact]
    public void Inactive_alarm_does_not_fire()
    {
        var sut = new SchedulerEvaluator();
        var alarm = Alarm("Desactivada", new TimeSpan(9, 0, 0));
        alarm.IsActive = false;

        sut.Evaluate(new DateTime(2026, 9, 14, 8, 59, 30), [alarm]);
        var result = sut.Evaluate(new DateTime(2026, 9, 14, 9, 0, 5), [alarm]);

        Assert.Empty(result.ToPlay);
    }

    [Fact]
    public void Gap_of_several_minutes_marks_previous_as_missed()
    {
        var sut = new SchedulerEvaluator();
        var alarm = Alarm("Perdida", new TimeSpan(9, 0, 0));

        sut.Evaluate(new DateTime(2026, 9, 14, 8, 59, 30), [alarm]);

        // El equipo "se despierta" a las 09:03 → la franja de las 09:00 quedó atrás.
        var result = sut.Evaluate(new DateTime(2026, 9, 14, 9, 3, 0), [alarm]);

        var missed = Assert.Single(result.Missed);
        Assert.Equal(alarm.Id, missed.Alarm.Id);
        Assert.Equal(new DateTime(2026, 9, 14, 9, 0, 0), missed.ScheduledAt);
        Assert.Empty(result.ToPlay);
    }

    [Fact]
    public void Two_alarms_same_minute_fire_in_order()
    {
        var sut = new SchedulerEvaluator();
        var first = Alarm("Zeta", new TimeSpan(9, 0, 0));
        var second = Alarm("Alfa", new TimeSpan(9, 0, 0));

        sut.Evaluate(new DateTime(2026, 9, 14, 8, 59, 30), [second, first]);

        var result = sut.Evaluate(new DateTime(2026, 9, 14, 9, 0, 5), [second, first]);

        Assert.Equal(2, result.ToPlay.Count);
        Assert.Equal(second.Id, result.ToPlay[0].Id); // orden por hora, luego nombre
        Assert.Equal(first.Id, result.ToPlay[1].Id);
    }

    private static int DayOffset(DayOfWeek day)
        => day == DayOfWeek.Sunday ? 6 : (int)day - 1;
}
