using Alarmino.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Alarmino.ViewModels;

/// <summary>Representa un chip de día (L M X J V S D) en la tarjeta de alarma.</summary>
public sealed record DayChip(char Letter, bool Active);

/// <summary>ViewModel de una alarma para la lista de la ventana principal.</summary>
public sealed class AlarmViewModel : ObservableObject
{
    private readonly Alarm _model;

    public AlarmViewModel(Alarm model)
    {
        _model = model;
        DayChips = BuildChips(model.Days);
    }

    public Alarm Model => _model;

    public Guid Id => _model.Id;

    public string Name
    {
        get => _model.Name;
        set => SetProperty<Alarm, string>(_model.Name, value, _model, (m, v) => m.Name = v, nameof(Name));
    }

    public string TimeText => _model.Time.ToString(@"hh\:mm");

    public long TimeOfDayTicks => _model.Time.Ticks;

    /// <summary>Día más temprano programado (Lun=0 … Dom=6), para ordenar «por día».</summary>
    public int DayOfWeekOrder
    {
        get
        {
            for (int i = 0; i < 7; i++)
            {
                var flag = DayFlagsExtensions.ToFlag(Order[i]);
                if (_model.Days.HasFlag(flag))
                {
                    return i;
                }
            }

            return 7;
        }
    }

    public IReadOnlyList<DayChip> DayChips { get; }

    public string DaysText => _model.Days.ToDisplayText();

    public string SoundText => _model.Sound.DisplayText;

    public string RepeatText => _model.RepeatCount > 1
        ? $"{_model.RepeatCount} toques"
        : "1 toque";

    public string ModeText => _model.Mode == PlaybackMode.CustomDuration
        ? $"Duración {_model.CustomDurationSeconds} s"
        : "Completo";

    public bool IsActive
    {
        get => _model.IsActive;
        set => SetProperty<Alarm, bool>(_model.IsActive, value, _model, (m, v) => m.IsActive = v, nameof(IsActive));
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(TimeText));
        OnPropertyChanged(nameof(DayOfWeekOrder));
        OnPropertyChanged(nameof(DaysText));
        OnPropertyChanged(nameof(SoundText));
        OnPropertyChanged(nameof(RepeatText));
        OnPropertyChanged(nameof(ModeText));
    }

    private static readonly DayOfWeek[] Order = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
        DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];

    private static IReadOnlyList<DayChip> BuildChips(DayFlags days)
    {
        char[] letters = ['L', 'M', 'X', 'J', 'V', 'S', 'D'];
        var chips = new DayChip[7];
        for (int i = 0; i < 7; i++)
        {
            chips[i] = new DayChip(letters[i], days.HasFlag(DayFlagsExtensions.ToFlag(Order[i])));
        }

        return chips;
    }
}
