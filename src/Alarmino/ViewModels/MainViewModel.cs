using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using Alarmino.Core.Models;
using Alarmino.Core.Services;
using Alarmino.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Alarmino.ViewModels;

/// <summary>ViewModel principal: lista de alarmas, scheduler, bandeja, configuración e historial.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly JsonDataStore _store;
    private readonly AudioService _audio;
    private readonly SchedulerEvaluator _evaluator;
    private readonly ThemeService _themes;
    private readonly AppSettings _settings;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private TrayIconService? _tray;

    private bool _masterEnabled = true;
    private int _volumePercent;
    private ThemeMode _theme;
    private bool _autoStart;
    private AlarmSortMode _sortMode;

    public MainViewModel()
    {
        _store = new JsonDataStore();
        _audio = new AudioService();
        _evaluator = new SchedulerEvaluator();
        _themes = new ThemeService();
        _dispatcher = Dispatcher.CurrentDispatcher;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += OnTick;

        _settings = _store.LoadSettings();
        _volumePercent = _settings.GlobalVolumePercent;
        _theme = _settings.Theme;
        _autoStart = _settings.AutoStartEnabled;
        _sortMode = _settings.SortMode;

        Alarms = new ObservableCollection<AlarmViewModel>(
            _store.LoadAlarms().Select(a => new AlarmViewModel(a)));
        AlarmsView = new ListCollectionView(Alarms);
        Events = new ObservableCollection<EventLogEntry>(_store.LoadEvents());

        foreach (var vm in Alarms)
        {
            vm.PropertyChanged += OnAlarmPropertyChanged;
        }
    }

    // ---------- Colecciones ----------

    public ObservableCollection<AlarmViewModel> Alarms { get; }

    public ListCollectionView AlarmsView { get; }

    public ObservableCollection<EventLogEntry> Events { get; }

    public bool HasAlarms => Alarms.Count > 0;

    public bool HasEvents => Events.Count > 0;

    // ---------- Eventos hacia la ventana ----------

    /// <summary>Solicita abrir el editor. <paramref name="existing"/> nulo ⇒ nueva alarma.</summary>
    public event Action<AlarmViewModel?>? EditRequested;

    // ---------- Propiedades de estado ----------

    public string AppVersion => "Alarmino v0.1.0";

    public bool MasterEnabled
    {
        get => _masterEnabled;
        set => SetProperty(ref _masterEnabled, value);
    }

    public string MasterStatusText => _masterEnabled ? "Todo activado" : "Todo apagado";

    public int VolumePercent
    {
        get => _volumePercent;
        set
        {
            int clamped = Math.Clamp(value, 0, 100);
            if (SetProperty(ref _volumePercent, clamped))
            {
                _settings.GlobalVolumePercent = clamped;
                PersistSettings();
            }
        }
    }

    public ThemeMode Theme
    {
        get => _theme;
        set
        {
            if (SetProperty(ref _theme, value))
            {
                OnPropertyChanged(nameof(IsDarkMode));
                OnPropertyChanged(nameof(IsLightMode));
                _themes.Apply(value);
                _settings.Theme = value;
                PersistSettings();
            }
        }
    }

    public bool IsDarkMode
    {
        get => _theme == ThemeMode.Dark;
        set { if (value) Theme = ThemeMode.Dark; }
    }

    public bool IsLightMode
    {
        get => _theme == ThemeMode.Light;
        set { if (value) Theme = ThemeMode.Light; }
    }

    public bool AutoStartEnabled
    {
        get => _autoStart;
        set
        {
            if (SetProperty(ref _autoStart, value))
            {
                AutostartService.SetEnabled(value);
                _settings.AutoStartEnabled = value;
                PersistSettings();
            }
        }
    }

    public AlarmSortMode SortMode
    {
        get => _sortMode;
        set
        {
            if (SetProperty(ref _sortMode, value))
            {
                OnPropertyChanged(nameof(SortByTime));
                OnPropertyChanged(nameof(SortByDay));
                _settings.SortMode = value;
                ApplySort();
                PersistSettings();
            }
        }
    }

    public bool SortByTime
    {
        get => _sortMode == AlarmSortMode.ByTime;
        set { if (value) SortMode = AlarmSortMode.ByTime; }
    }

    public bool SortByDay
    {
        get => _sortMode == AlarmSortMode.ByDay;
        set { if (value) SortMode = AlarmSortMode.ByDay; }
    }

    // ---------- Comandos ----------

    [RelayCommand]
    private void AddAlarm() => EditRequested?.Invoke(null);

    [RelayCommand]
    private void EditAlarm(AlarmViewModel? vm) => EditRequested?.Invoke(vm);

    [RelayCommand]
    private void DeleteAlarm(AlarmViewModel? vm)
    {
        if (vm is null)
        {
            return;
        }

        if (MessageBox.Show($"¿Eliminar la alarma «{vm.Name}»?", "Alarmino",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        vm.PropertyChanged -= OnAlarmPropertyChanged;
        Alarms.Remove(vm);
        OnPropertyChanged(nameof(HasAlarms));
        PersistAlarms();
    }

    [RelayCommand]
    private void ClearEvents()
    {
        Events.Clear();
        OnPropertyChanged(nameof(HasEvents));
        PersistEvents();
    }

    // ---------- Arranque / parada ----------

    public void Initialize()
    {
        _themes.Apply(_theme);
        ApplySort();
        if (_autoStart)
        {
            // Garantiza la entrada de autostart si es la primera ejecución.
            AutostartService.SetEnabled(true);
        }

        _timer.Start();
    }

    public void AttachTrayIcon(TrayIconService tray) => _tray = tray;

    // ---------- Métodos usados por la ventana / bandeja ----------

    public void AddAlarm(AlarmViewModel vm)
    {
        vm.PropertyChanged += OnAlarmPropertyChanged;
        Alarms.Add(vm);
        OnPropertyChanged(nameof(HasAlarms));
        PersistAlarms();
    }

    public void PersistAlarms() => _store.SaveAlarms(Alarms.Select(v => v.Model));

    public void Preview(PlaybackRequest request) => _audio.Play(request);

    public void Silence() => _audio.Stop();

    public void ShowMainWindow()
    {
        var window = Application.Current.MainWindow;
        if (window is null)
        {
            return;
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Show();
        window.Activate();
        window.WindowState = WindowState.Normal;
    }

    public void ShutdownApp()
    {
        App.IsExiting = true;
        Application.Current.Shutdown();
    }

    // ---------- Scheduler ----------

    private void OnTick(object? sender, EventArgs e)
    {
        var alarms = Alarms
            .Where(v => v.IsActive && MasterEnabled)
            .Select(v => v.Model)
            .ToList();

        var result = _evaluator.Evaluate(DateTime.Now, alarms);

        foreach (var missed in result.Missed)
        {
            AddEvent(new EventLogEntry
            {
                UtcTime = DateTime.UtcNow,
                AlarmId = missed.Alarm.Id,
                AlarmName = missed.Alarm.Name,
                State = EventState.Missed,
            });
            _tray?.ShowNotification("Alarmino",
                $"Se omitió la alarma «{missed.Alarm.Name}» de las {missed.Alarm.TimeText}: el equipo estaba apagado.");
        }

        foreach (var alarm in result.ToPlay)
        {
            PlayAlarm(alarm);
        }
    }

    private void PlayAlarm(Alarm alarm)
    {
        AddEvent(new EventLogEntry
        {
            UtcTime = DateTime.UtcNow,
            AlarmId = alarm.Id,
            AlarmName = alarm.Name,
            State = EventState.Played,
        });

        _tray?.ShowNotification("Alarmino", $"«{alarm.Name}» · {alarm.TimeText}");

        var request = new PlaybackRequest(
            alarm.Sound,
            alarm.Mode,
            alarm.CustomDurationSeconds,
            alarm.RepeatCount,
            alarm.PauseBetweenRepeatSeconds,
            _volumePercent);

        _audio.Play(request);
    }

    // ---------- Persistencia auxiliar ----------

    private void AddEvent(EventLogEntry entry)
    {
        Events.Add(entry);
        OnPropertyChanged(nameof(HasEvents));
        PersistEvents();
    }

    private void PersistEvents() => _store.SaveEvents(Events);

    private void PersistSettings() => _store.SaveSettings(_settings);

    private void OnAlarmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AlarmViewModel.IsActive))
        {
            PersistAlarms();
        }
    }

    private void ApplySort()
    {
        AlarmsView.SortDescriptions.Clear();
        if (_sortMode == AlarmSortMode.ByDay)
        {
            AlarmsView.SortDescriptions.Add(new SortDescription(
                nameof(AlarmViewModel.DayOfWeekOrder), ListSortDirection.Ascending));
            AlarmsView.SortDescriptions.Add(new SortDescription(
                nameof(AlarmViewModel.TimeOfDayTicks), ListSortDirection.Ascending));
        }
        else
        {
            AlarmsView.SortDescriptions.Add(new SortDescription(
                nameof(AlarmViewModel.TimeOfDayTicks), ListSortDirection.Ascending));
        }

        AlarmsView.Refresh();
    }
}
