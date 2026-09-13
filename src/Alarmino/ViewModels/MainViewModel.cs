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
    private bool _startVisible;
    private bool _notificationSound;
    private AlarmSortMode _sortMode;
    private int _alarmRepeatCount;
    private int _alarmPauseSeconds;
    private int _rainRepeatCount;
    private bool _isAlarmBlinkOn;
    private bool _isRainBlinkOn;
    private bool _blinkIsAlarm;
    private DispatcherTimer? _blinkTimer;
    private string? _activeSoundLabel;

    public MainViewModel()
    {
        _store = new JsonDataStore();
        _settings = _store.LoadSettings();
        _audio = new AudioService(resolvePresetFile: id => SoundPresets.ResolveFilePath(id, _settings.PresetOverrides));
        _evaluator = new SchedulerEvaluator();
        _themes = new ThemeService();
        _dispatcher = Dispatcher.CurrentDispatcher;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += OnTick;
        _volumePercent = _settings.GlobalVolumePercent;
        _theme = _settings.Theme;
        _autoStart = _settings.AutoStartEnabled;
        _startVisible = _settings.StartVisibleOnLogin;
        _notificationSound = _settings.NotificationSoundEnabled;
        _sortMode = _settings.SortMode;
        _alarmRepeatCount = _settings.AlarmRepeatCount;
        _alarmPauseSeconds = _settings.AlarmPauseSeconds;
        _rainRepeatCount = _settings.RainRepeatCount;

        _audio.PlaybackFinished += () =>
        {
            _dispatcher.InvokeAsync(() =>
            {
                _activeSoundLabel = null;
                StopBlink();
                PlaybackFinished?.Invoke();
                NotifyPlaybackState();
            });
        };

        Alarms = new ObservableCollection<AlarmViewModel>(
            _store.LoadAlarms().Select(a => new AlarmViewModel(a)));
        AlarmsView = new ListCollectionView(Alarms);
        Events = new ObservableCollection<EventLogEntry>(_store.LoadEvents());

        foreach (var vm in Alarms)
        {
            vm.PropertyChanged += OnAlarmPropertyChanged;
        }

        PresetEdits = new ObservableCollection<PresetEditViewModel>(
            SoundPresets.All.Select(builtin =>
            {
                var existing = _settings.PresetOverrides.FirstOrDefault(o => o.PresetId == builtin.Id);
                return new PresetEditViewModel(builtin, existing, SavePresetEdits, StoreSoundInLibrary);
            }));
    }

    // ---------- Colecciones ----------

    public ObservableCollection<AlarmViewModel> Alarms { get; }

    public ListCollectionView AlarmsView { get; }

    public ObservableCollection<EventLogEntry> Events { get; }

    public ObservableCollection<PresetEditViewModel> PresetEdits { get; }

    public bool HasAlarms => Alarms.Count > 0;

    public bool HasEvents => Events.Count > 0;

    // ---------- Eventos hacia la ventana ----------

    /// <summary>Solicita abrir el editor. <paramref name="existing"/> nulo ⇒ nueva alarma.</summary>
    public event Action<AlarmViewModel?>? EditRequested;

    // ---------- Propiedades de estado ----------

    public string AppVersion => "Alarmino v0.1.2";

    public bool MasterEnabled
    {
        get => _masterEnabled;
        set
        {
            if (SetProperty(ref _masterEnabled, value))
            {
                OnPropertyChanged(nameof(MasterStatusText));
            }
        }
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

    public bool NotificationSoundEnabled
    {
        get => _notificationSound;
        set
        {
            if (SetProperty(ref _notificationSound, value))
            {
                _settings.NotificationSoundEnabled = value;
                PersistSettings();
            }
        }
    }

    public bool StartVisibleOnLogin
    {
        get => _startVisible;
        set
        {
            if (SetProperty(ref _startVisible, value))
            {
                _settings.StartVisibleOnLogin = value;
                if (_autoStart)
                {
                    AutostartService.SetEnabled(true, value);
                }

                PersistSettings();
            }
        }
    }

    // ---------- Sonido del botón ALARMA ----------

    public IReadOnlyList<SoundPreset> AlarmButtonPresets => SoundPresets.GetAll(_settings.PresetOverrides);

    public int AlarmRepeatCount
    {
        get => _alarmRepeatCount;
        set
        {
            value = Math.Clamp(value, 1, 20);
            if (SetProperty(ref _alarmRepeatCount, value))
            {
                _settings.AlarmRepeatCount = value;
                PersistSettings();
            }
        }
    }

    public int AlarmPauseSeconds
    {
        get => _alarmPauseSeconds;
        set
        {
            value = Math.Clamp(value, 0, 300);
            if (SetProperty(ref _alarmPauseSeconds, value))
            {
                _settings.AlarmPauseSeconds = value;
                PersistSettings();
            }
        }
    }

    public bool AlarmButtonUsePreset
    {
        get => _settings.AlarmSound.Kind == SoundSourceKind.Preset;
        set
        {
            bool current = _settings.AlarmSound.Kind == SoundSourceKind.Preset;
            if (current != value)
            {
                _settings.AlarmSound.Kind = value ? SoundSourceKind.Preset : SoundSourceKind.File;
                OnPropertyChanged();
                OnPropertyChanged(nameof(AlarmButtonUseFile));
                PersistSettings();
            }
        }
    }

    public bool AlarmButtonUseFile
    {
        get => !AlarmButtonUsePreset;
        set => AlarmButtonUsePreset = !value;
    }

    public string AlarmButtonPresetId
    {
        get => _settings.AlarmSound.PresetId ?? SoundPresets.All[0].Id;
        set
        {
            string id = value ?? SoundPresets.All[0].Id;
            if (!string.Equals(_settings.AlarmSound.PresetId, id, StringComparison.Ordinal))
            {
                _settings.AlarmSound.PresetId = id;
                OnPropertyChanged();
                PersistSettings();
            }
        }
    }

    public string AlarmButtonFilePath
    {
        get => _settings.AlarmSound.FilePath ?? string.Empty;
        set
        {
            if (!string.Equals(_settings.AlarmSound.FilePath, value, StringComparison.Ordinal))
            {
                _settings.AlarmSound.FilePath = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(AlarmButtonFilePathDisplay));
                PersistSettings();
            }
        }
    }

    public string AlarmButtonFilePathDisplay
        => string.IsNullOrWhiteSpace(_settings.AlarmSound.FilePath)
            ? "Ningún archivo seleccionado"
            : System.IO.Path.GetFileName(_settings.AlarmSound.FilePath);

    /// <summary>Reproduce el sonido configurado para ALARMA con el número de repeticiones elegido.</summary>
    [RelayCommand]
    private void TriggerAlarm()
    {
        _activeSoundLabel = "el botón ALARMA";
        StartBlink(alarm: true);
        var request = new PlaybackRequest(
            _settings.AlarmSound,
            PlaybackMode.Full,
            0,
            _alarmRepeatCount,
            _alarmPauseSeconds,
            _volumePercent);
        _audio.Play(request);
        NotifyPlaybackState();
    }

    [RelayCommand]
    private void BrowseAlarmSound()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Selecciona el sonido de ALARMA",
            Filter = "Archivos de sonido (*.mp3;*.wav)|*.mp3;*.wav|MP3 (*.mp3)|*.mp3|WAV (*.wav)|*.wav",
            CheckFileExists = true,
            Multiselect = false,
        };

        if (dialog.ShowDialog() == true)
        {
            AlarmButtonUseFile = true;
            AlarmButtonFilePath = StoreSoundInLibrary(dialog.FileName);
        }
    }

    /// <summary>Copia un sonido elegido por el usuario a la biblioteca y devuelve la ruta guardada.</summary>
    public string StoreSoundInLibrary(string sourcePath) => _store.StoreSoundInLibrary(sourcePath);

    // ---------- Parpadeo de los botones ALARMA / LLUVIA ----------

    public bool IsAlarmBlinkOn
    {
        get => _isAlarmBlinkOn;
        private set => SetProperty(ref _isAlarmBlinkOn, value);
    }

    public bool IsRainBlinkOn
    {
        get => _isRainBlinkOn;
        private set => SetProperty(ref _isRainBlinkOn, value);
    }

    /// <summary>El botón deja de parpadear al terminar la reproducción.</summary>
    public bool IsSoundPlaying => _audio.IsPlaying;

    private void StartBlink(bool alarm)
    {
        StopBlink();
        _blinkIsAlarm = alarm;
        _blinkTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _blinkTimer.Tick -= OnBlinkTick;
        _blinkTimer.Tick += OnBlinkTick;
        SetBlinkOn(true);
        _blinkTimer.Start();
    }

    private void StopBlink()
    {
        _blinkTimer?.Stop();
        IsAlarmBlinkOn = false;
        IsRainBlinkOn = false;
    }

    private void SetBlinkOn(bool on)
    {
        if (_blinkIsAlarm)
        {
            IsAlarmBlinkOn = on;
        }
        else
        {
            IsRainBlinkOn = on;
        }
    }

    private void OnBlinkTick(object? sender, EventArgs e)
    {
        if (!_audio.IsPlaying)
        {
            StopBlink();
            return;
        }

        SetBlinkOn(!(_blinkIsAlarm ? _isAlarmBlinkOn : _isRainBlinkOn));
    }

    private void NotifyPlaybackState() => OnPropertyChanged(nameof(IsSoundPlaying));

    /// <summary>Detiene la reproducción actual (ALARMA, LLUVIA o alarma programada) con confirmación.</summary>
    [RelayCommand]
    private void StopSound()
    {
        if (!_audio.IsPlaying)
        {
            return;
        }

        string name = _activeSoundLabel ?? "el sonido en reproducción";
        if (MessageBox.Show($"¿Seguro que quieres parar {name}?", "Alarmino",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        _audio.Stop();
        _activeSoundLabel = null;
        StopBlink();
        NotifyPlaybackState();
    }

    /// <summary>Guarda los cambios sobre los presets y refresca los desplegables.</summary>
    private void SavePresetEdits()
    {
        _settings.PresetOverrides = PresetEdits
            .Where(e => e.HasOverride)
            .Select(e => new PresetOverride
            {
                PresetId = e.PresetId,
                DisplayName = e.DisplayName,
                FilePath = e.FilePath,
            })
            .ToList();
        PersistSettings();
        OnPropertyChanged(nameof(AlarmButtonPresets));
        OnPropertyChanged(nameof(RainButtonPresets));
    }

    // ---------- Sonido del botón LLUVIA ----------

    public IReadOnlyList<SoundPreset> RainButtonPresets => SoundPresets.GetAll(_settings.PresetOverrides);

    public bool RainButtonUsePreset
    {
        get => _settings.RainSound.Kind == SoundSourceKind.Preset;
        set
        {
            bool current = _settings.RainSound.Kind == SoundSourceKind.Preset;
            if (current != value)
            {
                _settings.RainSound.Kind = value ? SoundSourceKind.Preset : SoundSourceKind.File;
                OnPropertyChanged();
                OnPropertyChanged(nameof(RainButtonUseFile));
                PersistSettings();
            }
        }
    }

    public bool RainButtonUseFile
    {
        get => !RainButtonUsePreset;
        set => RainButtonUsePreset = !value;
    }

    public string RainButtonPresetId
    {
        get => _settings.RainSound.PresetId ?? SoundPresets.All[0].Id;
        set
        {
            string id = value ?? SoundPresets.All[0].Id;
            if (!string.Equals(_settings.RainSound.PresetId, id, StringComparison.Ordinal))
            {
                _settings.RainSound.PresetId = id;
                OnPropertyChanged();
                PersistSettings();
            }
        }
    }

    public string RainButtonFilePath
    {
        get => _settings.RainSound.FilePath ?? string.Empty;
        set
        {
            if (!string.Equals(_settings.RainSound.FilePath, value, StringComparison.Ordinal))
            {
                _settings.RainSound.FilePath = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(RainButtonFilePathDisplay));
                PersistSettings();
            }
        }
    }

    public string RainButtonFilePathDisplay
        => string.IsNullOrWhiteSpace(_settings.RainSound.FilePath)
            ? "Ningún archivo seleccionado"
            : System.IO.Path.GetFileName(_settings.RainSound.FilePath);

    public int RainRepeatCount
    {
        get => _rainRepeatCount;
        set
        {
            value = Math.Clamp(value, 1, 20);
            if (SetProperty(ref _rainRepeatCount, value))
            {
                _settings.RainRepeatCount = value;
                PersistSettings();
            }
        }
    }

    /// <summary>Reproduce el sonido de lluvia de la ventana principal.</summary>
    [RelayCommand]
    private void TriggerRain()
    {
        _activeSoundLabel = "el botón LLUVIA";
        StartBlink(alarm: false);
        var request = new PlaybackRequest(
            _settings.RainSound,
            PlaybackMode.Full,
            0,
            _rainRepeatCount,
            1,
            _volumePercent);
        _audio.Play(request);
        NotifyPlaybackState();
    }

    [RelayCommand]
    private void BrowseRainSound()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Selecciona el sonido de LLUVIA",
            Filter = "Archivos de sonido (*.mp3;*.wav)|*.mp3;*.wav|MP3 (*.mp3)|*.mp3|WAV (*.wav)|*.wav",
            CheckFileExists = true,
            Multiselect = false,
        };

        if (dialog.ShowDialog() == true)
        {
            RainButtonUseFile = true;
            RainButtonFilePath = StoreSoundInLibrary(dialog.FileName);
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
            AutostartService.SetEnabled(true, _startVisible);
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

    /// <summary>Detiene la previsualización activa desde el editor.</summary>
    public void StopPreview() => _audio.Stop();

    public event Action? PlaybackFinished;

    public void Silence()
    {
        _audio.Stop();
        _activeSoundLabel = null;
        StopBlink();
        NotifyPlaybackState();
    }

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
            if (_notificationSound)
            {
                _tray?.ShowNotification("Alarmino",
                    $"Se omitió la alarma «{missed.Alarm.Name}» de las {missed.Alarm.TimeText}: el equipo estaba apagado.");
            }
        }

        foreach (var alarm in result.ToPlay)
        {
            PlayAlarm(alarm);
        }
    }

    private void PlayAlarm(Alarm alarm)
    {
        _activeSoundLabel = $"la alarma «{alarm.Name}»";
        AddEvent(new EventLogEntry
        {
            UtcTime = DateTime.UtcNow,
            AlarmId = alarm.Id,
            AlarmName = alarm.Name,
            State = EventState.Played,
        });

        if (_notificationSound)
        {
            _tray?.ShowNotification("Alarmino", $"«{alarm.Name}» · {alarm.TimeText}");
        }

        var request = new PlaybackRequest(
            alarm.Sound,
            alarm.Mode,
            alarm.CustomDurationSeconds,
            alarm.RepeatCount,
            alarm.PauseBetweenRepeatSeconds,
            _volumePercent);

        _audio.Play(request);
        NotifyPlaybackState();
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
