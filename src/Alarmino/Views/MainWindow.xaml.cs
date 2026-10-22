using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using Alarmino.Core.Models;
using Alarmino.Core.Services;
using Alarmino.ViewModels;

namespace Alarmino.Views;

public partial class MainWindow : Window
{
    private AlarmEditorViewModel? _activeEditor;

    private MainViewModel ViewModel => (MainViewModel)DataContext;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.EditRequested += OnEditRequested;
        ViewModel.PlaybackFinished += OnPlaybackFinished;

        // El registro se dibuja solo al final: se sigue la última línea sin
        // que el usuario tenga que bajar el scroll a mano.
        ViewModel.LogLines.CollectionChanged += OnLogLinesChanged;
    }

    private void OnLogLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Con el panel oculto no hay nada que desplazar: el registro sigue
        // escribiéndose y guardándose, pero la vista no hace trabajo inútil.
        // Al abrirlo se recarga el historial, que vuelve a disparar este evento.
        if (!ViewModel.IsLogExpanded)
        {
            return;
        }

        // El scroll se pide después del layout: durante la medición el visor
        // todavía no sabe cuán alto es el contenido.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => LogScroll.ScrollToEnd());
    }

    private void OnEditRequested(AlarmViewModel? existing)
    {
        Alarm model = existing?.Model ?? new Alarm();
        var editor = new AlarmEditorViewModel(model, ViewModel.AlarmButtonPresets)
        {
            PreviewVolumePercent = ViewModel.VolumePercent,
            StoreSound = ViewModel.StoreSoundInLibrary,
        };
        editor.PreviewRequested += OnEditorPreview;
        editor.StopPreviewRequested += OnEditorStopPreview;
        _activeEditor = editor;

        var window = new EditorWindow(editor) { Owner = this };
        bool saved = window.ShowDialog() == true;

        if (editor.IsPreviewing)
        {
            OnEditorStopPreview();
        }

        editor.PreviewRequested -= OnEditorPreview;
        editor.StopPreviewRequested -= OnEditorStopPreview;
        _activeEditor = null;

        if (saved)
        {
            if (existing is null)
            {
                ViewModel.AddAlarm(new AlarmViewModel(model));
            }
            else
            {
                existing.Refresh();
                ViewModel.PersistAlarms();
                ViewModel.AlarmsView.Refresh();
            }
        }
    }

    private void OnEditorPreview(PlaybackRequest request) => ViewModel.Preview(request);

    private void OnEditorStopPreview() => ViewModel.StopPreview();

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow { Owner = this, DataContext = ViewModel };
        window.ShowDialog();
    }

    private void OnPlaybackFinished()
    {
        Dispatcher.InvokeAsync(() => _activeEditor?.OnPlaybackFinished());
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!App.IsExiting)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }
}
