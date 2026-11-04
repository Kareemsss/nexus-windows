using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Interop;
using Nexus.Core.Activities;
using Nexus.Core.Color;
using Nexus.Core.Content;
using Nexus.Core.Plugins;
using Nexus.Platform.Interop;
using Nexus.Platform.Services;

namespace Nexus.Platform.Providers;

/// <summary>
/// Screenshot confirmation. Two legitimate signals, no keyboard hooks:
///  * clipboard updates (AddClipboardFormatListener) carrying an image while a capture tool is in front
///    (Snipping Tool / Win+Shift+S, ShareX, Greenshot...);
///  * new images in the Screenshots folder (Win+PrtScn, Snipping Tool auto-save).
/// Both are merged into one glance so a single capture never announces twice.
/// </summary>
public sealed class CaptureProvider : ProviderBase
{
    private static readonly string[] CaptureTools =
        { "ScreenClippingHost", "SnippingTool", "ShareX", "Greenshot", "Lightshot", "Snagit32", "SnagitEditor", "flameshot", "ScreenSketch" };

    private HwndSource? _clipboardWindow;
    private FileSystemWatcher? _folderWatcher;
    private DateTimeOffset _lastAnnounce;

    public override string Id => "capture";
    public override string DisplayName => "Screenshots";
    public override string Description => "A quick flash confirmation when you capture the screen.";

    protected override Task OnStartAsync(CancellationToken ct)
    {
        // A hidden zero-size tool window that only exists to receive WM_CLIPBOARDUPDATE.
        var p = new HwndSourceParameters("NEXUS.Clipboard") { Width = 0, Height = 0, WindowStyle = 0, ExtendedWindowStyle = NativeMethods.WS_EX_TOOLWINDOW };
        _clipboardWindow = new HwndSource(p);
        _clipboardWindow.AddHook(WndProc);
        if (!NativeMethods.AddClipboardFormatListener(_clipboardWindow.Handle))
            SetStatus(new ProviderStatus(ProviderState.Running, "Clipboard listener unavailable; folder detection only."));

        if (KnownFolders.Screenshots is { } folder)
        {
            _folderWatcher = new FileSystemWatcher(folder, "*.png") { NotifyFilter = NotifyFilters.FileName, IncludeSubdirectories = false };
            _folderWatcher.Created += (_, e) => Guard("screenshot file", () => Announce(e.FullPath));
            _folderWatcher.EnableRaisingEvents = true;
        }
        return Task.CompletedTask;
    }

    protected override ValueTask OnStopAsync()
    {
        if (_clipboardWindow is not null)
        {
            NativeMethods.RemoveClipboardFormatListener(_clipboardWindow.Handle);
            _clipboardWindow.RemoveHook(WndProc);
            _clipboardWindow.Dispose();
            _clipboardWindow = null;
        }
        _folderWatcher?.Dispose();
        return ValueTask.CompletedTask;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_CLIPBOARDUPDATE) Guard("clipboard", OnClipboard);
        return IntPtr.Zero;
    }

    private void OnClipboard()
    {
        bool image;
        try { image = System.Windows.Clipboard.ContainsImage(); }
        catch { return; } // clipboard locked by another process: ignore this update
        if (!image) return;

        var fgProcess = ShellService.ProcessNameOfWindow(NativeMethods.GetForegroundWindow()) ?? string.Empty;
        bool tool = Array.Exists(CaptureTools, t => fgProcess.Equals(t, StringComparison.OrdinalIgnoreCase));
        if (tool) Announce(null);
        else Pulse(new PulseSignal(SemanticColors.Capture, PulseStyle.Flash)); // ordinary image copy: flash only
    }

    private void Announce(string? savedPath)
    {
        var now = DateTimeOffset.UtcNow;
        bool duplicate = now - _lastAnnounce < TimeSpan.FromSeconds(3);
        _lastAnnounce = now;
        if (!duplicate) Pulse(new PulseSignal(SemanticColors.Capture, PulseStyle.Flash));

        var actions = savedPath is null
            ? Array.Empty<ActivityAction>()
            : new[]
            {
                new ActivityAction("open", "Open", Glyphs.Open, () => { ShellService.Open(savedPath); return Task.CompletedTask; }, IsPrimary: true),
                new ActivityAction("folder", "Show in folder", Glyphs.Folder, () => { ShellService.ShowInFolder(savedPath); return Task.CompletedTask; }),
            };
        Post(new Activity
        {
            Id = "capture.event",
            SourceId = Id,
            Priority = ActivityPriority.Low,
            Category = ActivityCategory.Feedback,
            Duration = TimeSpan.FromSeconds(savedPath is null ? 2.2 : 3.5),
            Content = new GlanceContent(Glyphs.Screenshot, "Screenshot captured", SemanticColors.Capture,
                savedPath is null ? "Copied to clipboard" : "Saved to Screenshots"),
            Accent = SemanticColors.Capture,
            Actions = actions,
        });
    }
}
