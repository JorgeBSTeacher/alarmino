namespace Alarmino.Core.Models;

/// <summary>Días de la semana en los que se repite una alarma (modelo alarma de móvil).</summary>
[Flags]
public enum DayFlags
{
    None = 0,
    Monday = 1,
    Tuesday = 2,
    Wednesday = 4,
    Thursday = 8,
    Friday = 16,
    Saturday = 32,
    Sunday = 64,
    Weekdays = Monday | Tuesday | Wednesday | Thursday | Friday,
    Weekend = Saturday | Sunday,
    EveryDay = Weekdays | Weekend,
}

public static class DayFlagsExtensions
{
    private static readonly DayOfWeek[] Order = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
        DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];

    /// <summary>Devuelve las etiquetas (L M X J V S D) con su estado activado.</summary>
    public static char[] ToLetters(this DayFlags days)
    {
        char[] letters = new char[7];
        for (int i = 0; i < 7; i++)
        {
            letters[i] = days.HasFlag(ToFlag(Order[i])) ? Order[i] switch
            {
                DayOfWeek.Monday => 'L',
                DayOfWeek.Tuesday => 'M',
                DayOfWeek.Wednesday => 'X',
                DayOfWeek.Thursday => 'J',
                DayOfWeek.Friday => 'V',
                DayOfWeek.Saturday => 'S',
                _ => 'D',
            } : '·';
        }

        return letters;
    }

    public static bool Matches(this DayFlags days, DayOfWeek dayOfWeek)
        => days.HasFlag(ToFlag(dayOfWeek));

    public static DayFlags ToFlag(DayOfWeek dayOfWeek) => dayOfWeek switch
    {
        DayOfWeek.Monday => DayFlags.Monday,
        DayOfWeek.Tuesday => DayFlags.Tuesday,
        DayOfWeek.Wednesday => DayFlags.Wednesday,
        DayOfWeek.Thursday => DayFlags.Thursday,
        DayOfWeek.Friday => DayFlags.Friday,
        DayOfWeek.Saturday => DayFlags.Saturday,
        _ => DayFlags.Sunday,
    };

    /// <summary>Texto legible para la UI, p. ej. "L M X" o "Cada día".</summary>
    public static string ToDisplayText(this DayFlags days)
    {
        if (days == DayFlags.EveryDay)
        {
            return "Cada día";
        }

        if (days == DayFlags.Weekdays)
        {
            return "De lunes a viernes";
        }

        string[] names = ["L", "M", "X", "J", "V", "S", "D"];
        var marks = days.ToLetters();
        if (marks.Any(c => c != '·'))
        {
            return string.Join(" ", marks.Where(c => c != '·'));
        }

        _ = names;
        return "—";
    }
}
