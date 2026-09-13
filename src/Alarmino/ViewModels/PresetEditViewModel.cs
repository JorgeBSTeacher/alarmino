using Alarmino.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Alarmino.ViewModels;

/// <summary>
/// ViewModel para editar un preset de sonido predeterminado: renombrar y/o asignar un archivo de audio que lo sustituya.
/// </summary>
public sealed partial class PresetEditViewModel : ObservableObject
{
    private string _displayName;
    private string? _filePath;
    private readonly Action _onChanged;
    private readonly Func<string, string> _storeSound;

    public PresetEditViewModel(SoundPreset builtin, PresetOverride? existing, Action onChanged, Func<string, string> storeSound)
    {
        PresetId = builtin.Id;
        DefaultDisplayName = builtin.DisplayName;
        _displayName = existing?.DisplayName ?? builtin.DisplayName;
        _filePath = existing?.FilePath;
        _onChanged = onChanged;
        _storeSound = storeSound;
    }

    public string PresetId { get; }

    public string DefaultDisplayName { get; }

    public string DisplayName
    {
        get => _displayName;
        set
        {
            if (SetProperty(ref _displayName, value))
            {
                OnPropertyChanged(nameof(HasOverride));
                _onChanged();
            }
        }
    }

    public string? FilePath
    {
        get => _filePath;
        set
        {
            if (SetProperty(ref _filePath, value))
            {
                OnPropertyChanged(nameof(FilePathDisplay));
                OnPropertyChanged(nameof(HasOverride));
                _onChanged();
            }
        }
    }

    public string FilePathDisplay => string.IsNullOrWhiteSpace(FilePath)
        ? "Sintetizado (sonido original)"
        : System.IO.Path.GetFileName(FilePath);

    public bool HasOverride => DisplayName != DefaultDisplayName || FilePath is not null;

    [RelayCommand]
    private void BrowseFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"Selecciona el sonido de {DefaultDisplayName}",
            Filter = "Archivos de sonido (*.mp3;*.wav)|*.mp3;*.wav|MP3 (*.mp3)|*.mp3|WAV (*.wav)|*.wav",
            CheckFileExists = true,
            Multiselect = false,
        };

        if (dialog.ShowDialog() == true)
        {
            FilePath = _storeSound(dialog.FileName);
        }
    }

    [RelayCommand]
    private void Reset()
    {
        DisplayName = DefaultDisplayName;
        FilePath = null;
    }
}
