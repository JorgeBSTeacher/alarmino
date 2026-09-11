using System.ComponentModel;
using System.Windows;
using Alarmino.Core.Models;
using Alarmino.Core.Services;
using Alarmino.ViewModels;

namespace Alarmino.Views;

public partial class MainWindow : Window
{
    private MainViewModel ViewModel => (MainViewModel)DataContext;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.EditRequested += OnEditRequested;
    }

    private void OnEditRequested(AlarmViewModel? existing)
    {
        Alarm model = existing?.Model ?? new Alarm();
        var editor = new AlarmEditorViewModel(model)
        {
            PreviewVolumePercent = ViewModel.VolumePercent,
        };
        editor.PreviewRequested += OnEditorPreview;

        var window = new EditorWindow(editor) { Owner = this };
        bool saved = window.ShowDialog() == true;

        editor.PreviewRequested -= OnEditorPreview;

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
