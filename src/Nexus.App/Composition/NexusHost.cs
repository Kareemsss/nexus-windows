using System.IO;
using System.Windows;
using Nexus.App.Island;
using Nexus.App.Menu;
using Nexus.App.Motion;
using Nexus.App.SettingsUi;
using Nexus.App.Shelf;
using Nexus.App.Theming;
using Nexus.App.Tray;
using Nexus.App.Widgets;
using Nexus.Core.Activities;
using Nexus.Core.Color;
using Nexus.Core.Content;
using Nexus.Core.Diagnostics;
using Nexus.Core.Settings;
using Nexus.Core.Shelf;
using Nexus.Platform.Interop;
using Nexus.Platform.Providers;
using Nexus.Platform.Services;

namespace Nexus.App.Composition;

/// <summary>
/// Composition root: creates every service once, wires them together, and tears them down in order.
/// No other class news-up services, which keeps dependencies explicit and the island window thin.
/// </summary>
public sealed class NexusHost : IAsyncDisposable
{
    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nexus");

    private readonly Application _app;
    private SettingsStore? _settings;
    private ActivityHub? _hub;
    private PlatformServices? _platform;
    private IslandWindow? _window;
    private PositionManager? _position;
    private IslandController? _controller;
    private FullscreenDetector? _fullscreen;
    private ProviderManager? _providers;
    private PreviewEvents? _preview;
    private SongPopupService? _songPopup;
    private TrayIcon? _tray;
    private SettingsWindow? _settingsWindow;
    private string? _calendarFeed;
    private bool _disposed;
    private MemoryTrimmer? _trimmer;
    private ShelfProvider? _shelf;

    public NexusHost(Application app) => _app = app;

    public async Task StartAsync(bool openSettings)
    {
        var settingsPath = Path.Combine(DataDirectory, "settings.json");
        bool firstRun = !File.Exists(settingsPath);
        _settings = new SettingsStore(settingsPath);
        // The Run key is the source of truth (the user may have disabled us in Task Manager › Startup apps).
        _settings.Current.General.StartWithWindows = StartupService.IsEnabled();
        _calendarFeed = _settings.Current.Modules.CalendarFeed;

        var ui = SynchronizationContext.Current ?? throw new InvalidOperationException("NEXUS must start on the UI thread.");
        _hub = new ActivityHub(ui);
        var themes = new ThemeEngine(_settings);
        _platform = new PlatformServices();

        Animator.Speed = _settings.Current.Appearance.AnimationSpeed;
        Animator.ReduceMotion = _settings.Current.Appearance.ReduceMotion;

        _window = new IslandWindow();
        _window.ApplyScale(_settings.Current.Appearance.Scale);
        _position = new PositionManager(_window, _settings);
        // Hide completely may keep the island from ever being shown: create and place its window anyway, so the
        // edge reveal knows where the island lives.
        new System.Windows.Interop.WindowInteropHelper(_window).EnsureHandle();
        _position.Apply();
        var shelfModel = new ShelfModel();
        var shelfStore = new ShelfStore(Path.Combine(DataDirectory, "shelf.json"));
        if (_settings.Current.Shelf.Remember) shelfModel.Load(shelfStore.Load());
        _shelf = new ShelfProvider(shelfModel, shelfStore, _settings);

        _controller = new IslandController(_hub, _window, _settings, _position, _platform, _shelf, OpenSettings, () => _ = ExitAsync());
        var presenter = new IslandPresenter(_window, themes, BuildWidgets(), _settings, _controller);
        _controller.Attach(presenter);

        _fullscreen = new FullscreenDetector(IsOwnWindow);
        _fullscreen.Changed += (_, fs) => { _controller.SetFullscreen(fs); _window.ReassertTopmost(); };
        _fullscreen.Start();

        _providers = new ProviderManager(_hub, _settings, ui);
        RegisterProviders(_providers, _platform, _shelf);
        if (_settings.Current.General.LoadPlugins) _providers.LoadPlugins(Path.Combine(DataDirectory, "plugins"));
        var controller = _controller;
        var shelf = _shelf;
        _preview = new PreviewEvents(_hub)
        {
            ShowDropZone = controller.ShowDropZonePreview,
            AddShelfSamples = () => { if (shelf.IsActive) shelf.AddSamples(_hub.Now); },
        };
        _songPopup = new SongPopupService(_hub, _settings);

        _tray = new TrayIcon(
            leftClick: () => _app.Dispatcher.InvokeAsync(() => _controller.OpenCenter()),
            rightClick: () => _app.Dispatcher.InvokeAsync(ShowTrayMenu));
        _tray.Visible = _settings.Current.General.ShowTrayIcon;

        _settings.Changed += (_, section) => OnSettingsChanged(section);

        await _providers.StartEnabledAsync();
        Log.Info("Host", $"NEXUS started ({_providers.Providers.Count} providers)");
        _trimmer = new MemoryTrimmer();
        _trimmer.Start();

        if (firstRun)
        {
            _hub.Pulse(new PulseSignal(SemanticColors.Notification));
            _hub.Post(new Activity
            {
                Id = "nexus.welcome",
                SourceId = "nexus",
                Priority = ActivityPriority.Medium,
                Category = ActivityCategory.System,
                Duration = TimeSpan.FromSeconds(6),
                Content = new GlanceContent(Glyphs.Grid, "NEXUS is ready", SemanticColors.Notification, "Click the island to open Island Center"),
            });
        }
        if (openSettings || firstRun) OpenSettings();
    }

    private static WidgetRegistry BuildWidgets()
    {
        var registry = new WidgetRegistry(new GenericWidget());
        registry.Register(new GlanceWidget());
        registry.Register(new NotificationWidget());
        registry.Register(new MeetingWidget());
        registry.Register(new BatteryWidget());
        registry.Register(new VolumeWidget());
        registry.Register(new MediaWidget());
        return registry;
    }

    private static void RegisterProviders(ProviderManager m, PlatformServices platform, ShelfProvider shelf)
    {
        // Order only affects the Modules page; scheduling is decided by priorities, not registration order.
        m.Register(new MediaProvider());
        m.Register(new VolumeProvider(platform));
        m.Register(new BatteryProvider());
        m.Register(new PrivacyProvider());
        m.Register(new MeetingProvider(platform));
        m.Register(new NetworkProvider());
        m.Register(new BluetoothProvider());
        m.Register(new NotificationProvider());
        m.Register(new DownloadsProvider());
        m.Register(new CaptureProvider());
        m.Register(shelf);
    }

    private static bool IsOwnWindow(IntPtr hwnd)
    {
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        return pid == (uint)Environment.ProcessId;
    }

    private void OnSettingsChanged(string section)
    {
        if (_settings is null) return;
        switch (section)
        {
            case "general":
                if (_tray is not null) _tray.Visible = _settings.Current.General.ShowTrayIcon;
                if (StartupService.IsEnabled() != _settings.Current.General.StartWithWindows)
                    StartupService.SetEnabled(_settings.Current.General.StartWithWindows);
                break;
            case "appearance":
                if (_window is not null && Math.Abs(_window.Scale - _settings.Current.Appearance.Scale) > 0.001)
                {
                    _window.ApplyScale(_settings.Current.Appearance.Scale);
                    _position?.Apply();
                }
                break;
            case "shelf":
                if (_shelf is null) break;
                if (_settings.Current.Shelf.Remember) _shelf.Persist();
                else _shelf.ForgetSaved();
                break;
            case "modules":
                if (_calendarFeed != _settings.Current.Modules.CalendarFeed)
                {
                    _calendarFeed = _settings.Current.Modules.CalendarFeed;
                    _ = _providers?.RestartAsync("meetings");
                }
                break;
        }
    }

    private void ShowTrayMenu()
    {
        if (_settings is null || _controller is null) return;
        var menu = new NexusMenu(_settings.Current.Behavior.QuietMode, false);
        menu.CenterRequested += (_, _) => _controller.OpenCenter();
        menu.QuietToggled += (_, _) => _controller.ToggleQuietMode();
        menu.SettingsRequested += (_, _) => OpenSettings();
        menu.ExitRequested += (_, _) => _ = ExitAsync();
        menu.ShowAtCursor();
    }

    public void OpenSettings()
    {
        if (_settings is null || _providers is null || _preview is null) return;
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(_settings, _providers, _preview, () => _ = ExitAsync());
            _settingsWindow.Closed += (_, _) => { _settingsWindow = null; MemoryTrimmer.RequestSoon(); };
        }
        if (!_settingsWindow.IsVisible) _settingsWindow.Show();
        if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
        _settingsWindow.Activate();
    }

    public async Task ExitAsync()
    {
        await DisposeAsync();
        _app.Shutdown();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        Log.Info("Host", "Shutting down");
        try { _settingsWindow?.ForceClose(); } catch { }
        _trimmer?.Dispose();
        _fullscreen?.Dispose();
        if (_providers is not null) await _providers.DisposeAsync();
        _songPopup?.Dispose();
        _controller?.Dispose();
        _position?.Dispose();
        _tray?.Dispose();
        _platform?.Dispose();
        _settings?.Dispose();
        try { _window?.Close(); } catch { }
    }

    /// <summary>Synchronous last-chance cleanup (session end). Removes the tray icon and flushes settings.</summary>
    public void EmergencyCleanup()
    {
        if (_disposed) return;
        _disposed = true;
        try { _tray?.Dispose(); } catch { }
        try { _settings?.Dispose(); } catch { }
        try { _platform?.Dispose(); } catch { }
    }
}
