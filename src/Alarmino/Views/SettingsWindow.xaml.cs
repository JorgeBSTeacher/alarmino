using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Alarmino.Core.Services;
using Alarmino.ViewModels;

namespace Alarmino.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshDiagnostics();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnRefreshDiagnosticsClick(object sender, RoutedEventArgs e) => RefreshDiagnostics();

    /// <summary>
    /// Vuelca en el cuadro de diagnóstico las últimas líneas del registro de
    /// operaciones. Es una foto puntual (al abrir la ventana o al pulsar
    /// «Actualizar»), no un flujo en vivo, y nunca lanza.
    /// </summary>
    private void RefreshDiagnostics()
    {
        IReadOnlyList<string> lines = OperationLog.RecentLines;

        DiagnosticsTextBox.Text = lines.Count == 0
            ? "Todavía no hay operaciones registradas en esta sesión."
            : string.Join(Environment.NewLine, lines);

        DiagnosticsTextBox.CaretIndex = DiagnosticsTextBox.Text.Length;
        DiagnosticsTextBox.ScrollToEnd();
    }
}
