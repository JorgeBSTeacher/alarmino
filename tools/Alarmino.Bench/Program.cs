using System.Diagnostics;
using Alarmino.Core.Models;
using Alarmino.Core.Services;

var samples = BuildAlarms(50, 48); // 50 alarmas entre 00:00 y 23:59

Console.WriteLine("Alarmino.Bench — SchedulerEvaluator");
Console.WriteLine($"  Alarmas: {samples.Count}");

// 1) Rendimiento en cruce de franja (50 evaluaciones · 1000 ticks)
var ticks = 50_000;
var sw = Stopwatch.StartNew();
var evaluator = new SchedulerEvaluator();
DateTime cursor = new(2026, 9, 14, 8, 0, 0);
for (int i = 0; i < ticks; i++)
{
    cursor = cursor.AddMilliseconds(500);
    _ = evaluator.Evaluate(cursor, samples.Where(a => a.Days.Matches(cursor.DayOfWeek)).ToList());
}
sw.Stop();
Console.WriteLine($"  Rendimiento: {ticks} ticks en {sw.ElapsedMilliseconds} ms "
    + $"({(double)ticks / sw.Elapsed.TotalSeconds:N0} ticks/s)");

// 2) Latencia de disparo real (cruce de minuto → detección)
var latency = MeasureFireLatency();
Console.WriteLine($"  Latencia de disparo (cruce de minuto → detección): {latency.TotalMilliseconds:F1} ms");

static List<Alarm> BuildAlarms(int count, int intervalsInDay)
{
    var list = new List<Alarm>(count);
    var step = TimeSpan.FromMinutes(24 * 60 / intervalsInDay);
    var time = TimeSpan.Zero;
    for (int i = 0; i < count; i++)
    {
        list.Add(new Alarm
        {
            Name = $"Alarma {i + 1}",
            Time = time,
            Days = DayFlags.EveryDay,
            IsActive = true,
        });
        time = time + step;
    }

    return list;
}

static TimeSpan MeasureFireLatency()
{
    var evaluator = new SchedulerEvaluator();

    var nextMinute = DateTime.Now.AddMinutes(1);
    var target = new DateTime(nextMinute.Year, nextMinute.Month, nextMinute.Day,
        nextMinute.Hour, nextMinute.Minute, 0);

    var alarm = new Alarm
    {
        Name = "Latency",
        Time = target.TimeOfDay,
        Days = DayFlagsExtensions.ToFlag(target.DayOfWeek),
        IsActive = true,
    };

    // Base: se marca el minuto anterior para que la franja objetivo sea la siguiente.
    evaluator.Evaluate(target.AddMinutes(-1), [alarm]);

    // Espera a justo después del cruce de minuto.
    while (DateTime.Now < target.AddMilliseconds(50))
    {
        Thread.Sleep(1);
    }

    var crossing = DateTime.Now;
    while (DateTime.Now - target < TimeSpan.FromSeconds(70))
    {
        var result = evaluator.Evaluate(DateTime.Now, [alarm]);
        if (result.ToPlay.Count > 0)
        {
            return DateTime.Now - crossing;
        }

        Thread.Sleep(1);
    }

    return TimeSpan.FromMilliseconds(double.NaN);
}
