namespace Alarmino.Core.Models;

public enum EventState
{
    /// <summary>La alarma sonó correctamente.</summary>
    Played,

    /// <summary>La alarma debería haber sonado pero el equipo estaba dormido o apagado.</summary>
    Missed,
}

public class EventLogEntry
{
    public DateTime UtcTime { get; set; }

    public Guid AlarmId { get; set; }

    public string AlarmName { get; set; } = string.Empty;

    public EventState State { get; set; }

    public string TimeText => UtcTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
}
