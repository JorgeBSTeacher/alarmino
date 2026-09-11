using Alarmino.Core.Models;
using Alarmino.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Alarmino.ViewModels;

/// <summary>
/// Editor de una alarma (alta o modificación). Valida sobre el propio modelo
/// y expone comandos para Probar sonido, Examinar archivo, Guardar y Cancelar.
/// </summary>
public sealed partial class AlarmEditorViewModel : ObservableObject
{
    private static readonly DayOfWeek[] WeekOrder = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
        DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];

    private readonly bool[] _days = new bool[7];
    private bool _usePreset = true;
    private string _selectedPresetId = "bell";
    private string _filePath = string.Empty;
    private bool _fullMode = true;
    private string _durationText;
    private bool _repeatEnabled;
    private string _repeatCountText = "2";
    private string _pauseText = "1";
    private string _errorMessage = string.Empty;
    private bool _isPreviewing;

    public AlarmEditorViewModel(Alarm alarm)
    {
        Alarm = alarm;

        Name = alarm.Name;
        TimeText = alarm.Time.ToString(@"hh\:mm");

        for (int i = 0; i < 7; i++)
        {
            _days[i] = alarm.Days.HasFlag(DayFlagsExtensions.ToFlag(WeekOrder[i]));
        }

        _usePreset = alarm.Sound.Kind == SoundSourceKind.Preset;
        _selectedPresetId = alarm.Sound.PresetId ?? "bell";
        _filePath = alarm.Sound.FilePath ?? string.Empty;

        _fullMode = alarm.Mode == PlaybackMode.Full;
        _durationText = alarm.CustomDurationSeconds.ToString();
        _repeatEnabled = alarm.RepeatCount > 1;
        _repeatCountText = Math.Max(1, alarm.RepeatCount).ToString();
        _pauseText = Math.Max(0, alarm.PauseBetweenRepeatSeconds).ToString();
    }

    public Alarm Alarm { get; }

    public IReadOnlyList<SoundPreset> Presets => SoundPresets.All;

    public string ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    public string Name { get; set; }

    public string TimeText { get; set; }

    public bool DayMonday { get => _days[0]; set { _days[0] = value; ValidateRequired(); } }
    public bool DayTuesday { get => _days[1]; set { _days[1] = value; ValidateRequired(); } }
    public bool DayWednesday { get => _days[2]; set { _days[2] = value; ValidateRequired(); } }
    public bool DayThursday { get => _days[3]; set { _days[3] = value; ValidateRequired(); } }
    public bool DayFriday { get => _days[4]; set { _days[4] = value; ValidateRequired(); } }
    public bool DaySaturday { get => _days[5]; set { _days[5] = value; ValidateRequired(); } }
    public bool DaySunday { get => _days[6]; set { _days[6] = value; ValidateRequired(); } }

    public bool UsePreset
    {
        get => _usePreset;
        set
        {
            if (SetProperty(ref _usePreset, value))
            {
                OnPropertyChanged(nameof(UseFile));
            }
        }
    }

    public bool UseFile
    {
        get => !_usePreset;
        set => UsePreset = !value;
    }

    public string SelectedPresetId
    {
        get => _selectedPresetId;
        set => SetProperty(ref _selectedPresetId, value);
    }

    public string FilePath
    {
        get => _filePath;
        set
        {
            if (SetProperty(ref _filePath, value))
            {
                OnPropertyChanged(nameof(FilePathDisplay));
            }
        }
    }

    public string FilePathDisplay
        => string.IsNullOrWhiteSpace(_filePath) ? "Ningún archivo seleccionado" : System.IO.Path.GetFileName(_filePath);

    public bool FullMode
    {
        get => _fullMode;
        set
        {
            if (SetProperty(ref _fullMode, value))
            {
                OnPropertyChanged(nameof(CustomMode));
            }
        }
    }

    public bool CustomMode
    {
        get => !_fullMode;
        set => FullMode = !value;
    }

    public string DurationText { get => _durationText; set => SetProperty(ref _durationText, value); }

    public bool RepeatEnabled
    {
        get => _repeatEnabled;
        set => SetProperty(ref _repeatEnabled, value);
    }

    public string RepeatCountText { get => _repeatCountText; set => SetProperty(ref _repeatCountText, value); }
    public string PauseText { get => _pauseText; set => SetProperty(ref _pauseText, value); }

    /// <summary>Volumen global actual, inyectado por la ventana para el botón Probar.</summary>
    public int PreviewVolumePercent { get; set; } = 80;

    public event Action<PlaybackRequest>? PreviewRequested;

    /// <summary>Solicita detener la previsualización.</summary>
    public event Action? StopPreviewRequested;

    public bool IsPreviewing
    {
        get => _isPreviewing;
        private set
        {
            if (SetProperty(ref _isPreviewing, value))
            {
                OnPropertyChanged(nameof(PreviewButtonText));
            }
        }
    }

    public string PreviewButtonText => IsPreviewing ? "Parar sonido" : "Probar sonido";

    /// <summary>Restablece el botón cuando el audio termina solo (dispatcher del editor).</summary>
    public void OnPlaybackFinished() => IsPreviewing = false;

    [RelayCommand]
    private void Browse()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Selecciona un sonido",
            Filter = "Archivos de sonido (*.mp3;*.wav)|*.mp3;*.wav|MP3 (*.mp3)|*.mp3|WAV (*.wav)|*.wav",
            CheckFileExists = true,
            Multiselect = false,
        };

        if (dialog.ShowDialog() == true)
        {
            FilePath = dialog.FileName;
        }
    }

    [RelayCommand]
    private void Preview()
    {
        if (IsPreviewing)
        {
            StopPreviewRequested?.Invoke();
            IsPreviewing = false;
            return;
        }

        if (!TryValidateCore(out string _) && !string.IsNullOrEmpty(ErrorMessage))
        {
            return;
        }

        ApplyToModel();
        PreviewRequested?.Invoke(new PlaybackRequest(
            Alarm.Sound,
            PlaybackMode.Full,
            Alarm.CustomDurationSeconds,
            1,
            0,
            PreviewVolumePercent));
        IsPreviewing = true;
    }

    [RelayCommand]
    private void Cancel()
    {
        // El cierre se gestiona por el DialogResult en la ventana.
    }

    public bool Validate()
    {
        if (!TryValidateCore(out string error))
        {
            ErrorMessage = error;
            return false;
        }

        ApplyToModel();
        ErrorMessage = string.Empty;
        return true;
    }

    private void ValidateRequired()
    {
        if (_days.All(d => !d))
        {
            ErrorMessage = "Selecciona al menos un día de la semana.";
        }
        else if (!string.IsNullOrWhiteSpace(ErrorMessage) && ErrorMessage.Contains("día"))
        {
            ErrorMessage = string.Empty;
        }
    }

    private bool TryValidateCore(out string error)
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            error = "Pon un nombre o alias a la alarma (p. ej. «Recreo»).";
            return false;
        }

        if (!TimeSpan.TryParse(TimeText.Trim(), out var hour) || hour < TimeSpan.Zero || hour >= TimeSpan.FromDays(1))
        {
            error = "La hora debe tener formato HH:mm (p. ej. 09:00).";
            return false;
        }

        if (_days.All(d => !d))
        {
            error = "Selecciona al menos un día de la semana.";
            return false;
        }

        if (UseFile && !System.IO.File.Exists(FilePath))
        {
            error = "Selecciona un archivo de sonido válido (.mp3 / .wav).";
            return false;
        }

        if (UseFile && FilePath is not null &&
            !(FilePath.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) ||
              FilePath.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)))
        {
            error = "Solo se admiten archivos .mp3 o .wav.";
            return false;
        }

        if (!_fullMode &&
            (!int.TryParse(DurationText.Trim(), out int duration) || duration < 1 || duration > 3600))
        {
            error = "La duración personalizada debe ser un número de segundos entre 1 y 3600.";
            return false;
        }

        if (RepeatEnabled &&
            (!int.TryParse(RepeatCountText.Trim(), out int repeats) || repeats < 2 || repeats > 20))
        {
            error = "El número de toques debe estar entre 2 y 20.";
            return false;
        }

        if (RepeatEnabled &&
            (!int.TryParse(PauseText.Trim(), out int pause) || pause < 0 || pause > 300))
        {
            error = "La pausa entre toques debe estar entre 0 y 300 segundos.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private void ApplyToModel()
    {
        Alarm.Name = Name.Trim();
        Alarm.Time = TimeSpan.Parse(TimeText.Trim());

        DayFlags days = DayFlags.None;
        for (int i = 0; i < 7; i++)
        {
            if (_days[i])
            {
                days |= DayFlagsExtensions.ToFlag(WeekOrder[i]);
            }
        }

        Alarm.Days = days;
        Alarm.Sound.Kind = UsePreset ? SoundSourceKind.Preset : SoundSourceKind.File;
        Alarm.Sound.PresetId = UsePreset ? SelectedPresetId : null;
        Alarm.Sound.FilePath = UsePreset ? null : FilePath;
        Alarm.Mode = _fullMode ? PlaybackMode.Full : PlaybackMode.CustomDuration;
        Alarm.CustomDurationSeconds = _fullMode ? 0 : int.Parse(DurationText.Trim());
        Alarm.RepeatCount = RepeatEnabled ? int.Parse(RepeatCountText.Trim()) : 1;
        Alarm.PauseBetweenRepeatSeconds = RepeatEnabled ? int.Parse(PauseText.Trim()) : 0;
    }
}
