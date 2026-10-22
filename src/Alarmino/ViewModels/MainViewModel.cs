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
    private readonly YouTubeAudioResolver _youtube;
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
    private bool _isLogExpanded;
    private DispatcherTimer? _blinkTimer;
    private string? _activeSoundLabel;
    private DateTime _playbackStartedUtc;
    private bool _isDownloading;
    private string _playerStatusText = PlayerStatusFormatter.IdleText;
    private DateTime _nextPrefetchSweepUtc = DateTime.MinValue;
    private int _prefetchRunning;

    // ---------- Panel de diagnóstico en vivo ----------

    /// <summary>Líneas del log de la sesión (en vivo, las últimas <see cref="OperationLog.MaxRecentLines"/>).</summary>
    private readonly ObservableCollection<string> _logLines = new();

    /// <summary>Vigila la conexión sin martillear la red.</summary>
    private readonly NetworkStatusMonitor _network;

    /// <summary>Estado de la red del último panel de diagnóstico.</summary>
    private NetworkState _networkState = NetworkState.Unknown;

    /// <summary>Detalle del estado (caché y reproductor), ya redactado.</summary>
    private string _diagnosticsDetails = string.Empty;

    /// <summary>Evita dos refrescos del panel a la vez.</summary>
    private int _refreshingDiagnostics;

    /// <summary>Próxima vez que se refresca el panel (cada 2 s).</summary>
    private DateTime _nextDiagnosticsRefreshUtc = DateTime.MinValue;

    /// <summary>Cada cuánto se refresca el panel de diagnóstico (la red se comprueba aparte).</summary>
    private const double DiagnosticsRefreshSeconds = 2;

    // El barrido de playlists abandonadas se pide desde cada cambio de ajustes o
    // de alarma (muchos llamadores) y se ejecuta una sola vez por recojo: sin
    // esta coalescencia, cada cambio programaría su propio trabajo de fondo.
    private readonly PendingWorkSignal _orphanSweepRequested = new();

    public MainViewModel()
    {
        _store = new JsonDataStore();
        _settings = _store.LoadSettings();
        _youtube = new YouTubeAudioResolver
        {
            NoRepeatDays = _settings.NoRepeatDays,
            TargetBufferCount = _settings.TargetBufferCount,
        };
        _audio = new AudioService(
            resolvePresetFile: id => SoundPresets.ResolveFilePath(id, _settings.PresetOverrides),
            youtube: _youtube,
            resolveFallbackFile: () => _settings.OfflineFallbackFilePath);
        _network = new NetworkStatusMonitor(_youtube.Connectivity);
        _evaluator = new SchedulerEvaluator();
        _themes = new ThemeService();
        _dispatcher = Dispatcher.CurrentDispatcher;

        // La suscripción al log va después de tener el despachador: una línea
        // escrita entre medias saltaría al recibirla en otro hilo.
        OperationLog.LineWritten += OnOperationLogLine;
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

        // El resolutor descarga en hilos de fondo: la vista se refresca siempre en
        // el hilo de interfaz. Con un giro pendiente basta; los repetidos se agrupan.
        _youtube.DownloadActivityChanged += downloading =>
        {
            if (_dispatcher.CheckAccess())
            {
                SetDownloadingActivity(downloading);
            }
            else
            {
                _dispatcher.BeginInvoke(() => SetDownloadingActivity(downloading));
            }
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

    public string AppVersion => "Alarmino v0.1.6";

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
            if (value && _settings.AlarmSound.Kind != SoundSourceKind.Preset)
            {
                _settings.AlarmSound.Kind = SoundSourceKind.Preset;
                OnPropertyChanged();
                OnPropertyChanged(nameof(AlarmButtonUseFile));
                OnPropertyChanged(nameof(AlarmButtonUseYouTube));
                PersistSettings();
            }
        }
    }

    public bool AlarmButtonUseFile
    {
        get => _settings.AlarmSound.Kind == SoundSourceKind.File;
        set
        {
            if (value && _settings.AlarmSound.Kind != SoundSourceKind.File)
            {
                _settings.AlarmSound.Kind = SoundSourceKind.File;
                OnPropertyChanged();
                OnPropertyChanged(nameof(AlarmButtonUsePreset));
                OnPropertyChanged(nameof(AlarmButtonUseYouTube));
                PersistSettings();
            }
        }
    }

    public bool AlarmButtonUseYouTube
    {
        get => _settings.AlarmSound.Kind == SoundSourceKind.YouTube;
        set
        {
            if (value && _settings.AlarmSound.Kind != SoundSourceKind.YouTube)
            {
                _settings.AlarmSound.Kind = SoundSourceKind.YouTube;
                OnPropertyChanged();
                OnPropertyChanged(nameof(AlarmButtonUsePreset));
                OnPropertyChanged(nameof(AlarmButtonUseFile));
                PersistSettings();
            }
        }
    }

    public string AlarmButtonYoutubeUrl
    {
        get => _settings.AlarmSound.YouTubePlaylistUrl ?? string.Empty;
        set
        {
            if (!string.Equals(_settings.AlarmSound.YouTubePlaylistUrl, value, StringComparison.Ordinal))
            {
                _settings.AlarmSound.YouTubePlaylistUrl = value;
                OnPropertyChanged();
                PersistSettings();
            }
        }
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
        MarkPlaybackStarted();
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

    // ---------- Sonido de respaldo sin conexión ----------

    public string OfflineFallbackFilePathDisplay
        => string.IsNullOrWhiteSpace(_settings.OfflineFallbackFilePath)
            ? "Ningún archivo seleccionado"
            : System.IO.Path.GetFileName(_settings.OfflineFallbackFilePath);

    [RelayCommand]
    private void BrowseOfflineFallback()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Selecciona el MP3 de respaldo (sin conexión)",
            Filter = "Archivos de sonido (*.mp3;*.wav)|*.mp3;*.wav|MP3 (*.mp3)|*.mp3|WAV (*.wav)|*.wav",
            CheckFileExists = true,
            Multiselect = false,
        };

        if (dialog.ShowDialog() == true)
        {
            _settings.OfflineFallbackFilePath = StoreSoundInLibrary(dialog.FileName);
            OnPropertyChanged(nameof(OfflineFallbackFilePathDisplay));
            PersistSettings();
        }
    }

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

    /// <summary>
    /// Estado visible del reproductor: <c>En espera</c>, <c>Descargando…</c> o
    /// <c>Reproduciendo — mm:ss</c>. El contador lo refresca el temporizador de
    /// 500 ms ya existente, sin añadir ninguno nuevo.
    /// </summary>
    public string PlayerStatusText
    {
        get => _playerStatusText;
        private set => SetProperty(ref _playerStatusText, value);
    }

    /// <summary>
    /// Vuelca el estado actual en <see cref="PlayerStatusText"/>: reproduce,
    /// descarga o espera, con los segundos transcurridos cuando suena algo.
    /// </summary>
    private void RefreshPlayerStatus()
    {
        string text = PlayerStatusFormatter.Describe(
            _audio.IsPlaying,
            _isDownloading,
            _activeSoundLabel,
            DateTime.UtcNow - _playbackStartedUtc);
        PlayerStatusText = text;
    }

    /// <summary>Marca el arranque del contador de reproducción (el «00:00» visible).</summary>
    private void MarkPlaybackStarted() => _playbackStartedUtc = DateTime.UtcNow;

    // ---------- Panel de diagnóstico en vivo ----------

    /// <summary>Líneas del log de la sesión, en vivo (las más recientes al final).</summary>
    public ObservableCollection<string> LogLines => _logLines;

    /// <summary>
    /// True si el panel de log está desplegado. Lo mueve el botón «Ver log» de la
    /// ventana principal; al abrirlo se carga el historial y se comprueba el estado.
    /// </summary>
    public bool IsLogExpanded
    {
        get => _isLogExpanded;
        set
        {
            if (SetProperty(ref _isLogExpanded, value))
            {
                OnLogVisibilityChanged();
            }
        }
    }

    /// <summary>Estado de la red tal y como se muestra en el panel.</summary>
    public NetworkState NetworkState => _networkState;

    /// <summary>True si hay conexión (el indicador se pinta en verde).</summary>
    public bool IsNetworkOnline => _networkState == NetworkState.Online;

    /// <summary>Texto del indicador de red: con/sin conexión y cuándo se comprobó.</summary>
    public string NetworkStatusText => DiagnosticsFormatter.DescribeNetwork(_networkState, _network.CheckedUtc);

    /// <summary>Texto del indicador de pistas: si hay alguna descargada y lista.</summary>
    public string TrackStatusText => DiagnosticsFormatter.DescribeTracks(
        _youtube.ReadyTrackCount,
        _youtube.TargetTrackCount);

    /// <summary>True si hay alguna pista descargada lista (el indicador se pinta en verde).</summary>
    public bool HasReadyTrack => _youtube.ReadyTrackCount > 0;

    /// <summary>Detalle del estado: caché de pistas y reproductor.</summary>
    public string DiagnosticsDetails => _diagnosticsDetails;

    /// <summary>Al desplegar el panel se carga el historial y se comprueba el estado.</summary>
    private void OnLogVisibilityChanged()
    {
        if (!IsLogExpanded)
        {
            return;
        }

        _logLines.Clear();
        foreach (string line in OperationLog.RecentLines)
        {
            _logLines.Add(line);
        }

        _ = RefreshDiagnosticsAsync(force: true);
    }

    /// <summary>
    /// Recibe las líneas del log desde cualquier hilo (el log se escribe en
    /// segundo plano) y las lleva a la colección de la vista.
    /// </summary>
    private void OnOperationLogLine(string line)
    {
        if (_dispatcher.CheckAccess())
        {
            AppendLogLine(line);
            return;
        }

        _dispatcher.BeginInvoke(() => AppendLogLine(line));
    }

    /// <summary>Añade una línea al log visible y descarta las más antiguas.</summary>
    private void AppendLogLine(string line)
    {
        _logLines.Add(line);

        while (_logLines.Count > OperationLog.MaxRecentLines)
        {
            _logLines.RemoveAt(0);
        }
    }

    /// <summary>
    /// Refresca el panel de diagnóstico: red, pistas en caché y reproductor. La
    /// red se comprueba como mucho cada 15 s (lo decide el monitor) y el resto
    /// es de disco, así que repetirlo cada 2 s no cuesta nada.
    /// </summary>
    private async Task RefreshDiagnosticsAsync(bool force = false)
    {
        if (Interlocked.Exchange(ref _refreshingDiagnostics, 1) == 1)
        {
            return;
        }

        try
        {
            NetworkState previous = _networkState;
            NetworkState state = await _network.RefreshAsync(force).ConfigureAwait(true);
            _networkState = state;

            if (state != previous && state != NetworkState.Unknown)
            {
                // El cambio de red queda anotado en el propio log: el historial
                // explica por qué una alarma usó el respaldo.
                OperationLog.Write("red", state == NetworkState.Online
                    ? "conexión disponible."
                    : "sin conexión con YouTube.");
            }

            var snapshot = new DiagnosticsSnapshot(
                state,
                _network.CheckedUtc,
                _youtube.ReadyTrackCount,
                _youtube.TargetTrackCount,
                _youtube.DescribeCachedTracks(),
                _isDownloading,
                PlayerStatusText);

            _diagnosticsDetails = string.Join(Environment.NewLine, DiagnosticsFormatter.Describe(snapshot));

            OnPropertyChanged(nameof(NetworkState));
            OnPropertyChanged(nameof(NetworkStatusText));
            OnPropertyChanged(nameof(IsNetworkOnline));
            OnPropertyChanged(nameof(TrackStatusText));
            OnPropertyChanged(nameof(HasReadyTrack));
            OnPropertyChanged(nameof(DiagnosticsDetails));
        }
        catch
        {
            // Un panel de diagnóstico roto no puede impedir que suene la alarma.
        }
        finally
        {
            Interlocked.Exchange(ref _refreshingDiagnostics, 0);
        }
    }

    /// <summary>Refresca el panel como mucho cada <see cref="DiagnosticsRefreshSeconds"/> segundos.</summary>
    private void MaybeRefreshDiagnostics()
    {
        DateTime now = DateTime.UtcNow;
        if (now < _nextDiagnosticsRefreshUtc)
        {
            return;
        }

        _nextDiagnosticsRefreshUtc = now.AddSeconds(DiagnosticsRefreshSeconds);
        _ = RefreshDiagnosticsAsync();
    }

    /// <summary>Anota el estado de descarga que llega del resolutor (ya en el hilo de interfaz).</summary>
    private void SetDownloadingActivity(bool downloading)
    {
        _isDownloading = downloading;
        RefreshPlayerStatus();
    }

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

    private void NotifyPlaybackState()
    {
        OnPropertyChanged(nameof(IsSoundPlaying));
        RefreshPlayerStatus();
    }

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
            if (value && _settings.RainSound.Kind != SoundSourceKind.Preset)
            {
                _settings.RainSound.Kind = SoundSourceKind.Preset;
                OnPropertyChanged();
                OnPropertyChanged(nameof(RainButtonUseFile));
                OnPropertyChanged(nameof(RainButtonUseYouTube));
                PersistSettings();
            }
        }
    }

    public bool RainButtonUseFile
    {
        get => _settings.RainSound.Kind == SoundSourceKind.File;
        set
        {
            if (value && _settings.RainSound.Kind != SoundSourceKind.File)
            {
                _settings.RainSound.Kind = SoundSourceKind.File;
                OnPropertyChanged();
                OnPropertyChanged(nameof(RainButtonUsePreset));
                OnPropertyChanged(nameof(RainButtonUseYouTube));
                PersistSettings();
            }
        }
    }

    public bool RainButtonUseYouTube
    {
        get => _settings.RainSound.Kind == SoundSourceKind.YouTube;
        set
        {
            if (value && _settings.RainSound.Kind != SoundSourceKind.YouTube)
            {
                _settings.RainSound.Kind = SoundSourceKind.YouTube;
                OnPropertyChanged();
                OnPropertyChanged(nameof(RainButtonUsePreset));
                OnPropertyChanged(nameof(RainButtonUseFile));
                PersistSettings();
            }
        }
    }

    public string RainButtonYoutubeUrl
    {
        get => _settings.RainSound.YouTubePlaylistUrl ?? string.Empty;
        set
        {
            if (!string.Equals(_settings.RainSound.YouTubePlaylistUrl, value, StringComparison.Ordinal))
            {
                _settings.RainSound.YouTubePlaylistUrl = value;
                OnPropertyChanged();
                PersistSettings();
            }
        }
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
        MarkPlaybackStarted();
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

    // ---------- Precarga de pistas de YouTube ----------

    /// <summary>
    /// Descarga en segundo plano la pista de las playlists configuradas para
    /// que las alarmas con sonido de YouTube empiecen a su hora. La propia
    /// descarga se descarta si ya hay una pista reciente o si no hay conexión.
    /// </summary>
    public void PrefetchYouTube() => RunYouTubeBackground((url, ct) => _youtube.PrefetchAsync(url, ct));

    /// <summary>
    /// Pide el barrido de las playlists que ya no usa ninguna alarma tras un
    /// cambio de ajustes o de alarma. Es la vía del guardado: a diferencia del
    /// arranque, aquí el cambio ocurre con la aplicación en marcha, así que hay
    /// que limpiar la playlist abandonada en cuanto se abandona y no esperar al
    /// próximo reinicio.
    /// <para>
    /// No ejecuta nada en el hilo de quien llama (jamás bloquea la interfaz):
    /// solo deja la petición anotada y lanza el barrido en segundo plano. Las
    /// peticiones repetidas se agrupan en una sola, así que llamarlo en cada
    /// cambio de ajustes (hay más de treinta rutas) no acumula trabajos.
    /// </para>
    /// </summary>
    private void RequestOrphanSweep()
    {
        _orphanSweepRequested.Request();

        // Si ya hay una operación de YouTube en marcha, el pestillo la protege:
        // esta petición queda pendiente y el siguiente recojo la atenderá (al
        // terminar la operación en curso, véase RunYouTubeBackground).
        if (Interlocked.Exchange(ref _prefetchRunning, 1) == 1)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                do
                {
                    RemoveOrphanedPlaylists();
                }
                while (_orphanSweepRequested.TryTake());
            }
            finally
            {
                Interlocked.Exchange(ref _prefetchRunning, 0);
            }
        });
    }

    /// <summary>
    /// Secuencia de YouTube del arranque, en segundo plano: primero el veredicto
    /// de descarga de cada playlist (solo disco, sin bloquear) y la descarga
    /// forzosa de las que no tienen nada listo; después la sincronización de la
    /// caché con la lista remota (descarga lo que falte y borra lo que ya no está),
    /// y por último el barrido de las playlists que ya no usa ninguna alarma.
    /// Si una consulta falla o llega vacía, esa playlist no se toca.
    /// </summary>
    public void SyncYouTubePlaylists() => StartBootYouTubeSequence();

    /// <summary>
    /// Ejecuta una operación de YouTube sobre cada playlist configurada en un
    /// hilo de fondo. Como mucho una operación a la vez: las llamadas repetidas
    /// se ignoran, y ningún fallo llega a la interfaz.
    /// </summary>
    private void RunYouTubeBackground(Func<string?, CancellationToken, Task> operation)
    {
        // Como mucho una operación en marcha: las llamadas repetidas se ignoran.
        if (Interlocked.Exchange(ref _prefetchRunning, 1) == 1)
        {
            return;
        }

        string[] urls = YouTubePlaylistUrls().ToArray();
        _ = Task.Run(async () =>
        {
            try
            {
                await RunYouTubeOperationAsync(operation, urls).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Exchange(ref _prefetchRunning, 0);

                // Un barrido pedido mientras esta operación corría (su petición
                // encontró el pestillo tomado) no puede quedarse pendiente hasta
                // el próximo cambio: se atiende aquí, todavía en segundo plano.
                // Se hace DESPUÉS de soltar el pestillo y volviendo a pedirlo por
                // la vía normal, para no dejar la ventana en la que una petición
                // nueva se pierde entre la comprobación y el soltado.
                DrainPendingOrphanSweep();
            }
        });
    }

    /// <summary>
    /// Atiende las peticiones de barrido pendientes en segundo plano, tomando de
    /// nuevo el pestillo de «una operación a la vez» si hace falta. Si otra
    /// operación ya lo tiene, la petición sigue pendiente para su propio recojo.
    /// </summary>
    private void DrainPendingOrphanSweep()
    {
        if (!_orphanSweepRequested.IsPending)
        {
            return;
        }

        RequestOrphanSweep();
    }

    /// <summary>
    /// Aplica una operación de YouTube a cada URL. Nunca propaga errores: la
    /// operación de fondo jamás debe romper la aplicación.
    /// </summary>
    private static async Task RunYouTubeOperationAsync(
        Func<string?, CancellationToken, Task> operation,
        IReadOnlyList<string> urls)
    {
        try
        {
            foreach (string url in urls)
            {
                await operation(url, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch
        {
            // La operación nunca debe romper la aplicación.
        }
    }

    /// <summary>URLs de playlist distintas usadas por las alarmas, ALARMA y LLUVIA.</summary>
    private IEnumerable<string> YouTubePlaylistUrls()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (_settings.AlarmSound.Kind == SoundSourceKind.YouTube &&
            seen.Add(_settings.AlarmSound.YouTubePlaylistUrl ?? string.Empty))
        {
            yield return _settings.AlarmSound.YouTubePlaylistUrl!;
        }

        if (_settings.RainSound.Kind == SoundSourceKind.YouTube &&
            seen.Add(_settings.RainSound.YouTubePlaylistUrl ?? string.Empty))
        {
            yield return _settings.RainSound.YouTubePlaylistUrl!;
        }

        foreach (var alarm in Alarms)
        {
            if (alarm.Model.Sound.Kind == SoundSourceKind.YouTube &&
                seen.Add(alarm.Model.Sound.YouTubePlaylistUrl ?? string.Empty))
            {
                yield return alarm.Model.Sound.YouTubePlaylistUrl!;
            }
        }
    }

    /// <summary>
    /// Ids de las playlists en uso (las mismas URLs que sincroniza el arranque).
    /// Devuelve <c>null</c> si alguna URL no es una playlist: sin la lista entera
    /// no se puede saber qué playlists sobran, y borrar con una lista incompleta
    /// borraría pistas que sí se usan. Es un fallo cerrado, como los demás.
    /// </summary>
    private HashSet<string>? YouTubePlaylistIds()
    {
        var urls = YouTubePlaylistUrls().ToArray();
        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (string url in urls)
        {
            string? playlistId = YouTubeAudioResolver.ExtractPlaylistId(url);
            if (playlistId is null)
            {
                return null;
            }

            ids.Add(playlistId);
        }

        return ids;
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

        EnsureOfflineFallback();

        _timer.Start();

        // Al encender el equipo, una sola secuencia en segundo plano:
        // 1) el veredicto de arranque (solo disco, sin bloquear) comprueba que cada
        //    playlist tenga algo descargado y lo deja en el log; una playlist sin
        //    nada listo fuerza la descarga de inmediato (la ventana de 30 minutos no
        //    puede dejar una caché vacía al arrancar);
        // 2) después se sincroniza la caché con la playlist remota: descarga lo que
        //    falte y borra lo que ya no está en la lista;
        // 3) por último se barren las playlists que ya no usa ninguna alarma, para
        //    que no ocupen plazas de la caché ni desalojen una pista necesaria.
        StartBootYouTubeSequence();
    }

    /// <summary>
    /// Secuencia de YouTube del arranque, en un único trabajo de fondo (comparte
    /// el pestillo de «una operación a la vez»). Nunca bloquea el arranque y nunca
    /// propaga errores a la interfaz.
    /// </summary>
    private void StartBootYouTubeSequence()
    {
        // Como mucho una operación en marcha: las llamadas repetidas se ignoran.
        if (Interlocked.Exchange(ref _prefetchRunning, 1) == 1)
        {
            return;
        }

        string[] urls = YouTubePlaylistUrls().ToArray();
        _ = Task.Run(async () =>
        {
            try
            {
                await CheckBootReadinessAsync(urls).ConfigureAwait(false);
                await RunYouTubeOperationAsync(_youtube.PrefetchAndSyncAsync, urls).ConfigureAwait(false);
                RemoveOrphanedPlaylists();
            }
            finally
            {
                Interlocked.Exchange(ref _prefetchRunning, 0);
            }
        });
    }

    /// <summary>
    /// Borra de la caché las playlists que ya no usa ninguna alarma (el usuario
    /// cambió la alarma de la playlist A a la B y A se quedaba ocupando una de las
    /// tres plazas de la caché hasta que la purga por antigüedad la desalojara).
    /// </summary>
    /// <remarks>
    /// El barrido es puramente local: solo compara los ids de playlist en uso
    /// (ajustes y alarmas) con lo guardado, sin consultar la red. No se confunde
    /// con un fallo de descarga de una playlist que sigue en uso: esa conserva su
    /// pista, porque su id sigue en el conjunto (de eso ya se encarga el borrado
    /// por lista remota). Si el conjunto no se puede construir (ninguna URL, o
    /// alguna que no es una playlist), no se borra nada. Nunca rompe la interfaz.
    /// </remarks>
    private void RemoveOrphanedPlaylists()
    {
        try
        {
            HashSet<string>? usedIds = YouTubePlaylistIds();
            if (usedIds is null)
            {
                // No se pudo reunir la lista completa de playlists en uso: no se
                // sabe qué sobra, así que no se borra nada (y la caché tampoco).
                OperationLog.Write("cache", "no se barren playlists abandonadas: la lista de playlists en uso no está disponible.");
                return;
            }

            _youtube.RemovePlaylistsNotIn(usedIds);
        }
        catch
        {
            // El barrido de playlists abandonadas nunca debe romper la aplicación.
        }
    }

    /// <summary>
    /// Comprueba (solo disco, sin esperar a la red) el estado de descarga de las
    /// playlists al arrancar y fuerza la descarga de las que no tienen nada listo.
    /// </summary>
    private async Task CheckBootReadinessAsync(IReadOnlyList<string> urls)
    {
        try
        {
            string?[] playlistUrls = urls.Cast<string?>().ToArray();
            IReadOnlyList<TrackReadiness> verdicts =
                await _youtube.CheckBootReadinessAsync(playlistUrls).ConfigureAwait(false);

            // Nada listo justo al arrancar ⇒ descarga inmediata (force), saltando la
            // ventana de 30 minutos que sí respeta el barrido normal. Se empareja cada
            // veredicto con su URL por id de playlist, porque el veredicto deduplica.
            var ready = new HashSet<string>(
                verdicts.Where(v => v.IsReady).Select(v => v.PlaylistId),
                StringComparer.Ordinal);

            foreach (string url in urls)
            {
                string? playlistId = YouTubeAudioResolver.ExtractPlaylistId(url);
                if (playlistId is not null && !ready.Contains(playlistId))
                {
                    await _youtube.PrefetchAsync(url, force: true).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            // El veredicto de arranque nunca debe romper la aplicación.
        }
    }

    /// <summary>
    /// El MP3 de respaldo por defecto es null, así que sin conexión la alarma
    /// quedaría en silencio. Al arrancar se asegura uno: si el ajuste está vacío
    /// o el archivo ya no existe, se elige un sonido predeterminado válido y se
    /// persiste. Si no hay ningún candidato, se registra y se deja null.
    /// </summary>
    private void EnsureOfflineFallback()
    {
        // Los sonidos predeterminados van embebidos: se materializan primero en
        // la biblioteca (sounds\presets) para poder apuntarlos como respaldo.
        SoundLibrary library = _store.SoundLibrary;
        library.EnsureDefaultPresets();

        var candidates = new List<string>();
        foreach (var preset in SoundPresets.All)
        {
            string? overrideFile = SoundPresets.ResolveFilePath(preset.Id, _settings.PresetOverrides);
            if (!string.IsNullOrWhiteSpace(overrideFile))
            {
                candidates.Add(overrideFile);
            }
        }

        candidates.Add(library.PresetsDirectory);

        string? chosen = OfflineFallbackSelector.Select(
            _settings.OfflineFallbackFilePath, candidates, out IReadOnlyList<string> examined);

        // El log del arranque confirma SIEMPRE el respaldo, tanto el validado
        // (el camino normal, que antes no registraba nada) como el que falta.
        bool keptConfigured = chosen is not null && chosen == _settings.OfflineFallbackFilePath;
        OperationLog.Write(
            "arranque",
            OfflineFallbackReadiness.Describe(new OfflineFallbackOutcome(chosen, keptConfigured, examined)));

        if (chosen is null || keptConfigured)
        {
            return;
        }

        _settings.OfflineFallbackFilePath = chosen;
        PersistSettings();
        OnPropertyChanged(nameof(OfflineFallbackFilePathDisplay));
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

    public void PersistAlarms()
    {
        _store.SaveAlarms(Alarms.Select(v => v.Model));
        PrefetchYouTube();
        RequestOrphanSweep();
    }

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

        MaybePrefetchStaleYouTubeTracks();

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

        // Refresca el estado visible (y el contador de segundos) sin un temporizador extra.
        RefreshPlayerStatus();

        // El panel de log solo se refresca si está desplegado: si no, no hay
        // motivo para tocar la red ni el disco.
        if (IsLogExpanded)
        {
            MaybeRefreshDiagnostics();
        }
    }

    /// <summary>
    /// Barredura periódica (cada 30 min) que vuelve a precargar las pistas
    /// antiguas; así una alarma de última hora nunca espera una descarga.
    /// </summary>
    private void MaybePrefetchStaleYouTubeTracks()
    {
        DateTime now = DateTime.UtcNow;
        if (now < _nextPrefetchSweepUtc)
        {
            return;
        }

        _nextPrefetchSweepUtc = now.AddMinutes(30);
        PrefetchYouTube();
    }

    private void PlayAlarm(Alarm alarm)
    {
        _activeSoundLabel = $"la alarma «{alarm.Name}»";
        MarkPlaybackStarted();
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

    private void PersistSettings()
    {
        _store.SaveSettings(_settings);
        _youtube.NoRepeatDays = _settings.NoRepeatDays;
        _youtube.TargetBufferCount = _settings.TargetBufferCount;
        PrefetchYouTube();
        RequestOrphanSweep();
    }

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
