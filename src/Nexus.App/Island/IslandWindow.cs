using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Nexus.App.Rendering;
using Nexus.Platform.Interop;

namespace Nexus.App.Island;

/// <summary>
/// The overlay window. Deliberately thin: it only owns the visual tree skeleton and Win32 window behaviour.
///
/// Windows-specific behaviour:
///  * Per-pixel transparent (layered) window. Fully transparent pixels are click-through at the OS level, so
///    the large fixed canvas never blocks the desktop – only the island's painted pixels receive input.
///  * WS_EX_TOOLWINDOW keeps it out of Alt+Tab/taskbar; WS_EX_NOACTIVATE means clicking the island never steals
///    focus from the app you are working in. Activation is enabled temporarily only for Island Center so it can
///    close when you click elsewhere and receive Esc.
///  * Fixed canvas size: the island morphs inside it, so no window resizing happens during animation (resizing
///    a layered window mid-animation causes visible tearing). Idle cost is zero because nothing redraws.
/// </summary>
public sealed class IslandWindow : Window
{
    // Tall enough for Island Center with every section, the File Shelf included. Transparent pixels are
    // click-through, so the extra height costs memory only (about 0.6 MB), never desktop clicks.
    public static readonly Size CanvasSize = new(860, 880);

    public IslandWindow()
    {
        Title = "NEXUS";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        ShowActivated = false;
        Focusable = true;
        Width = CanvasSize.Width;
        Height = CanvasSize.Height;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -10000; // positioned in physical pixels right after the handle exists
        Top = -10000;
        UseLayoutRounding = false;
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale); // ClearType is unavailable on layered windows

        Stage = new Canvas { Width = CanvasSize.Width, Height = CanvasSize.Height, ClipToBounds = false };
        IslandRoot = new Canvas();
        HitPad = new Border { Background = new SolidColorBrush(Color.FromArgb(1, 255, 255, 255)) }; // alpha 1: invisible, but hit-testable
        Surface = new IslandSurface();
        Viewport = new Grid { ClipToBounds = false };
        Frame = new Grid();
        Frame.Children.Add(Surface);
        Frame.Children.Add(Viewport);
        IslandRoot.Children.Add(HitPad);
        IslandRoot.Children.Add(Frame);

        SatelliteSurface = new IslandSurface();
        SatelliteFrame = new Grid { Cursor = Cursors.Hand };
        SatelliteFrame.Children.Add(SatelliteSurface);

        Stage.Children.Add(SatelliteFrame);
        Stage.Children.Add(IslandRoot);
        Content = Stage;

        SourceInitialized += (_, _) =>
        {
            Handle = new WindowInteropHelper(this).Handle;
            NativeMethods.SetExStyle(Handle, NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE, 0);
        };
    }

    public IntPtr Handle { get; private set; }
    public Canvas Stage { get; }
    public Canvas IslandRoot { get; }
    public Border HitPad { get; }
    public Grid Frame { get; }
    public IslandSurface Surface { get; }
    public Grid Viewport { get; }
    public Grid SatelliteFrame { get; }
    public IslandSurface SatelliteSurface { get; }

    public event EventHandler? DpiScaleChanged;

    /// <summary>User "island scale" setting. Applied as a layout transform so text stays crisp at any scale.</summary>
    public double Scale { get; private set; } = 1;

    public void ApplyScale(double scale)
    {
        Scale = Math.Clamp(scale, 0.5, 2);
        Stage.LayoutTransform = Math.Abs(Scale - 1) < 0.001 ? Transform.Identity : new ScaleTransform(Scale, Scale);
        Width = CanvasSize.Width * Scale;
        Height = CanvasSize.Height * Scale;
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        DpiScaleChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Allows/disallows activation (Island Center needs focus to detect clicks elsewhere and Esc).</summary>
    public void SetActivatable(bool activatable)
    {
        if (Handle == IntPtr.Zero) return;
        if (activatable) NativeMethods.SetExStyle(Handle, 0, NativeMethods.WS_EX_NOACTIVATE);
        else NativeMethods.SetExStyle(Handle, NativeMethods.WS_EX_NOACTIVATE, 0);
    }

    public void PlacePixels(int x, int y)
    {
        if (Handle == IntPtr.Zero) return;
        NativeMethods.SetWindowPos(Handle, NativeMethods.HWND_TOPMOST, x, y, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    /// <summary>Other topmost windows (and Explorer restarts) can push us down the z-order: re-assert cheaply.</summary>
    public void ReassertTopmost()
    {
        if (Handle == IntPtr.Zero || !IsVisible) return;
        NativeMethods.SetWindowPos(Handle, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }
}
