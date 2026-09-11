using System.Windows;
using Alarmino.ViewModels;

namespace Alarmino.Views;

public partial class EditorWindow : Window
{
    private readonly AlarmEditorViewModel _viewModel;

    public EditorWindow(AlarmEditorViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Title = viewModel.Name is { Length: > 0 } ? $"Alarmino — {viewModel.Name}" : "Alarmino — Nueva alarma";
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Validate())
        {
            DialogResult = true;
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
