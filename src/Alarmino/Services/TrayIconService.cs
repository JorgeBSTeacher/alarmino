using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Forms;
using Alarmino.ViewModels;

namespace Alarmino.Services;

/// <summary>Icono de bandeja del sistema con menú contextual y notificaciones.</summary>
public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly MainViewModel _viewModel;

    public TrayIconService(MainViewModel viewModel)
    {
        _viewModel = viewModel;

        _icon = new NotifyIcon
        {
            Text = "Alarmino — Sirenas del colegio",
            Icon = LoadIcon(),
            Visible = false,
        };

        var open = new ToolStripMenuItem("Abrir");
        open.Click += (_, _) => _viewModel.ShowMainWindow();

        var silence = new ToolStripMenuItem("Silenciar alarma activa");
        silence.Click += (_, _) => _viewModel.Silence();

        var exit = new ToolStripMenuItem("Salir");
        exit.Click += (_, _) => _viewModel.ShutdownApp();

        _menu = new ContextMenuStrip();
        _menu.Items.AddRange([open, silence, new ToolStripSeparator(), exit]);
        _icon.ContextMenuStrip = _menu;
        _icon.DoubleClick += (_, _) => _viewModel.ShowMainWindow();
    }

    public NotifyIcon NotifyIcon => _icon;

    public void ShowNotification(string title, string text, bool playSound)
    {
        ToolTipIcon icon = playSound ? ToolTipIcon.Info : ToolTipIcon.None;
        _icon.ShowBalloonTip(3000, title, text, icon);
    }

    private static Icon LoadIcon()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
            {
                return Icon.ExtractAssociatedIcon(exe) ?? SystemIcons.Application;
            }
        }
        catch
        {
            // Fallo como SystemIcons.Application
        }

        return SystemIcons.Application;
    }

    public void Dispose()
    {
        _menu.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
    }
}
