using System.IO;
using Nexus.Core.Diagnostics;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace Nexus.App.Tray;

/// <summary>
/// Notification-area icon. WinForms NotifyIcon is used only as a thin Shell_NotifyIcon wrapper: it re-adds
/// itself when Explorer restarts (TaskbarCreated). The menu itself is NEXUS's own glass menu.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;

    public TrayIcon(Action leftClick, Action rightClick)
    {
        _icon = new Forms.NotifyIcon { Text = "NEXUS – Your Windows Activity Center", Visible = false };
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "nexus.ico");
            _icon.Icon = File.Exists(path)
                ? new Drawing.Icon(path, Forms.SystemInformation.SmallIconSize)
                : Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? string.Empty);
        }
        catch (Exception ex)
        {
            Log.Warn("Tray", "Icon load failed, using default", ex);
            _icon.Icon = Drawing.SystemIcons.Application;
        }
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) leftClick();
            else if (e.Button == Forms.MouseButtons.Right) rightClick();
        };
    }

    public bool Visible
    {
        get => _icon.Visible;
        set => _icon.Visible = value;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
