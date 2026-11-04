using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Nexus.App.Motion;
using Nexus.App.Rendering;
using Nexus.App.Theming;
using Nexus.App.Ui;
using Nexus.Core.Activities;
using Nexus.Core.Motion;
using Nexus.Platform.Interop;
using Nexus.Platform.Services;

namespace Nexus.App.Menu;

/// <summary>
/// Lightweight context menu drawn with the island's own glass (not a stock WPF/WinForms menu). Used for the
/// island right-click and the tray icon. Closes on outside click (deactivation) or Esc.
/// </summary>
public sealed class NexusMenu : Window, IAnimatable
{
    private readonly Spring _presence = new(0, SpringConfig.Content, 0.003);
    private readonly ScaleTransform _scale = new(0.96, 0.96);
    private readonly Grid _root;
    private bool _closing;

    public NexusMenu(bool quietMode, bool centerOpen)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -10000;
        Top = -10000;
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);

        var palette = new Palette(ThemePresets.Obsidian);
        var surface = new IslandSurface { Theme = ThemePresets.Obsidian, Radius = 14, AccentStrength = 0, ShadowStrength = 1 };
        var items = new StackPanel { Margin = new Thickness(6) };
        items.Children.Add(Item(palette, Glyphs.Grid, centerOpen ? "Close Island Center" : "Island Center", null, () => CenterRequested));
        items.Children.Add(Item(palette, Glyphs.QuietHours, "Quiet mode", quietMode, () => QuietToggled));
        items.Children.Add(Item(palette, Glyphs.Settings, "Settings", null, () => SettingsRequested));
        items.Children.Add(new Border { Height = 1, Margin = new Thickness(10, 5, 10, 5), Background = palette.CardStroke });
        items.Children.Add(Item(palette, Glyphs.Power, "Exit NEXUS", null, () => ExitRequested));

        _root = new Grid { Width = 220, Margin = new Thickness(18, 10, 18, 26), RenderTransform = _scale, RenderTransformOrigin = new Point(0.5, 0) };
        _root.Children.Add(surface);
        _root.Children.Add(items);
        _root.Opacity = 0;
        Content = _root;

        SourceInitialized += (_, _) =>
            NativeMethods.SetExStyle(new WindowInteropHelper(this).Handle, NativeMethods.WS_EX_TOOLWINDOW, 0); // keep out of Alt+Tab
        Deactivated += (_, _) => CloseAnimated();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) CloseAnimated(); };
    }

    public event EventHandler? CenterRequested;
    public event EventHandler? QuietToggled;
    public event EventHandler? SettingsRequested;
    public event EventHandler? ExitRequested;

    private NexusButton Item(Palette p, string glyph, string label, bool? check, Func<EventHandler?> handler)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var g = Kit.Glyph(glyph, 13, p.TextSecondary).WithMargin(0, 0, 12, 0);
        var t = Kit.Text(label, Kit.Type.Body, p.TextPrimary);
        Grid.SetColumn(t, 1);
        row.Children.Add(g);
        row.Children.Add(t);
        if (check is not null)
        {
            var pill = new Border
            {
                Width = 26, Height = 14, CornerRadius = new CornerRadius(7),
                Background = check.Value ? p.TextPrimary : p.ControlFill,
                Child = new Border
                {
                    Width = 10, Height = 10, CornerRadius = new CornerRadius(5),
                    Background = check.Value ? Palette.Freeze(Color.FromRgb(0x0B, 0x0B, 0x0E)) : p.TextSecondary,
                    HorizontalAlignment = check.Value ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                    Margin = new Thickness(2, 0, 2, 0),
                },
            };
            Grid.SetColumn(pill, 2);
            row.Children.Add(pill);
        }
        var b = new NexusButton(row, Colors.Transparent, p.Theme.ControlHover, 9) { Height = 34, Padding = new Thickness(10, 0, 10, 0) };
        b.Click += (_, _) =>
        {
            CloseAnimated();
            handler()?.Invoke(this, EventArgs.Empty);
        };
        return b;
    }

    /// <summary>Opens just below/left of the cursor, kept fully on the cursor's monitor.</summary>
    public void ShowAtCursor()
    {
        Show();
        UpdateLayout();
        var hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.GetCursorPos(out var pt);
        var mon = MonitorService.FromPoint(pt.X, pt.Y);
        int w = (int)Math.Ceiling(ActualWidth * mon.Scale), h = (int)Math.Ceiling(ActualHeight * mon.Scale);
        int x = Math.Clamp(pt.X - (int)(18 * mon.Scale), mon.WorkArea.Left, Math.Max(mon.WorkArea.Left, mon.WorkArea.Right - w));
        int y = pt.Y + h > mon.WorkArea.Bottom ? pt.Y - h + (int)(26 * mon.Scale) : pt.Y - (int)(6 * mon.Scale);
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, x, y, 0, 0, NativeMethods.SWP_NOSIZE);
        Activate();
        _presence.Target = 1;
        Animator.Wake(this);
    }

    private void CloseAnimated()
    {
        if (_closing) return;
        _closing = true;
        _presence.Target = 0;
        Animator.Wake(this);
    }

    public bool Advance(double dt)
    {
        bool moving = _presence.Step(dt);
        var v = Math.Clamp(_presence.Value, 0, 1);
        _root.Opacity = v;
        _scale.ScaleX = _scale.ScaleY = 0.96 + 0.04 * v;
        if (_closing && v < 0.02)
        {
            Close();
            return false;
        }
        return moving;
    }
}
