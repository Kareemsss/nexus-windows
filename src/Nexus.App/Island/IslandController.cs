using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Nexus.App.Menu;
using Nexus.App.Motion;
using Nexus.App.Shelf;
using Nexus.App.Widgets;
using Nexus.Core.Activities;
using Nexus.Core.Color;
using Nexus.Core.Content;
using Nexus.Core.Shelf;
using Nexus.Core.Diagnostics;
using Nexus.Core.Scheduling;
using Nexus.Core.Settings;
using Nexus.Core.State;
using Nexus.Platform.Services;

namespace Nexus.App.Island;

/// <summary>
/// Glue between the event world and the visual world:
///   hub changes / time / pointer input → arbiter → state machine → presenter.
/// It contains no drawing and no Windows API calls of its own.
/// </summary>
public sealed class IslandController : IIslandCommands, IDisposable
{
    private readonly ActivityHub _hub;
    private readonly IslandWindow _window;
    private readonly SettingsStore _settings;
    private readonly PositionManager _position;
    private readonly PlatformServices _platform;
    private readonly IslandStateMachine _machine = new();
    private readonly DispatcherTimer _wake = new(DispatcherPriority.Normal);
    private readonly Action _openSettings;
    private readonly Action _exit;
    private IslandPresenter? _presenter;
    private bool _fullscreen;
    private bool _windowShown;
    private bool _renderPending = true;
    private IslandMode _lastMode = IslandMode.Idle;
    private readonly EdgeRevealWatcher _reveal;
    private readonly ShelfProvider _shelf;
    private readonly DispatcherTimer _dropPreview = new(DispatcherPriority.Normal) { Interval = TimeSpan.FromSeconds(3) };
    private bool _pointerOver;
    private bool _draggingOut;
    private DropInfo? _drop;
    private DateTimeOffset _lastDragOver;

    public IslandController(ActivityHub hub, IslandWindow window, SettingsStore settings, PositionManager position,
        PlatformServices platform, ShelfProvider shelf, Action openSettings, Action exit)
    {
        _hub = hub;
        _window = window;
        _settings = settings;
        _position = position;
        _platform = platform;
        _shelf = shelf;
        _openSettings = openSettings;
        _exit = exit;
        _reveal = new EdgeRevealWatcher(window, settings,
            holding: () => _pointerOver || _draggingOut || _machine.State.Mode is IslandMode.Center or IslandMode.Expanded or IslandMode.Drop);
        _dropPreview.Tick += (_, _) => { _dropPreview.Stop(); Input(m => m.Dropped(Now)); };
        _reveal.RevealChanged += (_, _) => OnRevealChanged();
        // Collapse deadlines and hover-expand timers live in the state machine; feed them the clock.
        _wake.Tick += (_, _) => { _wake.Stop(); Input(m => m.Tick(Now)); };
    }

    public ActivityHub Hub => _hub;
    public bool QuietMode => _settings.Current.Behavior.QuietMode;
    public bool? MicMuted => _platform.CaptureAudio?.Current?.Muted;

    public void Attach(IslandPresenter presenter)
    {
        _presenter = presenter;
        presenter.PointerEntered += (_, _) => { _pointerOver = true; Input(m => m.PointerEnter(Now)); };
        presenter.PointerExited += (_, _) => { _pointerOver = false; Input(m => m.PointerLeave(Now)); };
        presenter.Clicked += (_, _) => Input(m => m.PrimaryClick(Now));
        presenter.SatelliteClicked += (_, _) => Input(m => m.OpenCenter(Now));
        presenter.RightClicked += (_, _) => ShowMenu();
        presenter.Wheel += (_, delta) => OnWheel(delta);
        presenter.DragEntered += (_, e) => OnDragEnter(e);
        presenter.DragMoved += (_, e) => OnDragOver(e);
        presenter.DragLeft += (_, e) => OnDragLeave(e);
        presenter.Dropped += (_, e) => OnDrop(e);
        _shelf.StatusChanged += (_, _) => _window.Dispatcher.InvokeAsync(UpdateDropEnabled);
        UpdateDropEnabled();

        _hub.Changed += (_, _) => Reevaluate();
        _hub.Pulsed += (_, signal) => presenter.Pulse(signal);
        _settings.Changed += (_, section) => OnSettingsChanged(section);

        _window.Deactivated += (_, _) =>
        {
            // Dropping into another app can activate it; an item still being dragged out keeps Center open.
            if (_draggingOut) return;
            if (_machine.State.Mode == IslandMode.Center) Input(m => m.Collapse(Now));
        };
        _window.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Input(m => m.Collapse(Now)); };

        ApplyBehaviorSettings();
        Reevaluate();
    }

    private DateTimeOffset Now => _hub.Now;

    public void SetFullscreen(bool fullscreen)
    {
        if (_fullscreen == fullscreen) return;
        _fullscreen = fullscreen;
        Log.Info("Island", fullscreen ? "Fullscreen app detected" : "Fullscreen app closed");
        UpdateRevealArming(); // fullscreen changed
        Invalidate();
    }

    private ArbitrationPolicy Policy() => new(
        FullscreenActive: _fullscreen,
        Fullscreen: _reveal.IsRevealed ? FullscreenBehavior.AlwaysShow : _settings.Current.Behavior.Fullscreen,
        QuietMode: _settings.Current.Behavior.QuietMode,
        HideAll: _settings.Current.Behavior.HideCompletely && !_reveal.IsRevealed,
        MediaPopupsOnly: _settings.Current.Behavior.SongPopup && !_reveal.IsRevealed);

    /// <summary>Single entry point for "something may have changed". Cheap enough to call on every event.</summary>
    public void Reevaluate()
    {
        try
        {
            var now = Now;
            _hub.Prune(now);
            var policy = Policy();
            var previous = _machine.ForegroundId;
            var arbitration = ActivityArbiter.Arbitrate(_hub.Snapshot(), previous, policy, now);

            if (arbitration.Foreground is { } fg)
            {
                bool mutated = false;
                if (fg.ShownAt is null) { _hub.MarkShown(fg.Id, now); mutated = true; }
                else if (_machine.State.Hovered && fg.Activity.IsTransient) { _hub.Hold(fg.Id, now); mutated = true; }
                if (mutated) arbitration = ActivityArbiter.Arbitrate(_hub.Snapshot(), fg.Id, policy, now);
            }
            if (previous is not null && previous != arbitration.Foreground?.Id) _hub.Requeue(previous, now);

            // Only touch the visual tree when the state really changed. Island Center is a live dashboard,
            // so it refreshes on any hub change.
            bool changed = _machine.Apply(arbitration, now);
            if (changed || _renderPending || _machine.State.Mode == IslandMode.Center)
            {
                _renderPending = false;
                Render();
            }

            // Wake exactly when time alone can change the picture: an expiry, a collapse deadline, or a
            // hover-hold refresh. Otherwise the island sleeps – no polling.
            DateTimeOffset? next = Min(arbitration.NextWakeup, _machine.Deadline);
            if (_machine.State.Hovered && arbitration.Foreground?.Activity.IsTransient == true) next = Min(next, now.AddSeconds(1));
            Schedule(next, now);
        }
        catch (Exception ex)
        {
            Log.Error("Island", "Re-evaluation failed", ex);
        }
    }

    private void Input(Func<IslandStateMachine, bool> action)
    {
        _renderPending |= action(_machine);
        Reevaluate();
    }

    /// <summary>Forces a render on the next evaluation (settings changed how the same state looks).</summary>
    private void Invalidate()
    {
        _renderPending = true;
        Reevaluate();
    }

    private void Render()
    {
        if (_presenter is null) return;
        var state = _machine.State;

        if (state.Mode == IslandMode.Hidden)
        {
            _presenter.Render(state);
            if (_windowShown)
            {
                _windowShown = false;
                _presenter.PlayExit(() => { if (!_windowShown) _window.Hide(); });
            }
            _lastMode = state.Mode;
            return;
        }
        if (!_windowShown)
        {
            _presenter.PrepareEntrance();
            _window.Show();
            _windowShown = true;
            _position.Apply();
        }

        // Follow-cursor mode: hop to the monitor in use when the island wakes up from idle.
        bool waking = (_lastMode is IslandMode.Idle or IslandMode.Peek or IslandMode.Hidden)
                      && (state.Mode is IslandMode.Compact or IslandMode.Expanded or IslandMode.Center);
        if (waking && _settings.Current.Position.Monitor == MonitorMode.FollowCursor) _position.Apply();

        if (state.Mode == IslandMode.Center && _lastMode != IslandMode.Center)
        {
            _window.SetActivatable(true);
            _window.Activate();
        }
        else if (state.Mode != IslandMode.Center && _lastMode == IslandMode.Center)
        {
            _window.SetActivatable(false);
        }

        _lastMode = state.Mode;
        _presenter.Render(state);
    }

    private void Schedule(DateTimeOffset? at, DateTimeOffset now)
    {
        _wake.Stop();
        if (at is null) return;
        var delay = at.Value - now;
        _wake.Interval = delay < TimeSpan.FromMilliseconds(15) ? TimeSpan.FromMilliseconds(15) : delay;
        _wake.Start();
    }

    private static DateTimeOffset? Min(DateTimeOffset? a, DateTimeOffset? b) =>
        a is null ? b : b is null ? a : (a < b ? a : b);

    private void OnWheel(int direction)
    {
        if (!_settings.Current.Behavior.ScrollAdjustsVolume || direction == 0) return;
        _hub.LatestContent<VolumeContent>()?.Controls.Step(direction > 0 ? 2 : -2);
    }

    private void OnSettingsChanged(string section)
    {
        switch (section)
        {
            case "behavior":
            case "appearance":
            case "center":
                ApplyBehaviorSettings();
                Invalidate();
                break;
            case "position":
                _position.Apply();
                Invalidate();
                break;
        }
    }

    private void ApplyBehaviorSettings()
    {
        var b = _settings.Current.Behavior;
        _machine.AutoCollapseDelay = TimeSpan.FromSeconds(b.AutoCollapseSeconds);
        _machine.ClickBehavior = b.Click;
        _machine.HoverBehavior = b.Hover;
        UpdateRevealArming(); // settings changed
        Animator.Speed = _settings.Current.Appearance.AnimationSpeed;
        Animator.ReduceMotion = _settings.Current.Appearance.ReduceMotion;
        Animator.Eco = _settings.Current.Appearance.EcoMode;
    }

    private void ShowMenu()
    {
        var menu = new NexusMenu(_settings.Current.Behavior.QuietMode, _machine.State.Mode == IslandMode.Center);
        menu.CenterRequested += (_, _) => Input(m => m.State.Mode == IslandMode.Center ? m.Collapse(Now) : m.OpenCenter(Now));
        menu.QuietToggled += (_, _) => ToggleQuietMode();
        menu.SettingsRequested += (_, _) => OpenSettings();
        menu.ExitRequested += (_, _) => _exit();
        menu.ShowAtCursor();
    }

    private void UpdateRevealArming()
    {
        if (_reveal is null) return;
        var b = _settings.Current.Behavior;
        _reveal.SetArmed(b.RevealOnEdgeHover && (b.HideCompletely || (_fullscreen && b.Fullscreen == FullscreenBehavior.Hide)));
    }

    private void OnRevealChanged()
    {
        if (_reveal.IsRevealed)
        {
            Input(m => m.PointerEnter(Now));
            _window.ReassertTopmost();
        }
        else
        {
            _pointerOver = false;
            Input(m => m.PointerLeave(Now));
        }
    }

    #region File Shelf

    private void UpdateDropEnabled() => _presenter?.SetDropEnabled(_shelf.IsActive);

    private DragDropEffects EffectFor(DragEventArgs e)
    {
        if (_drop?.Kind != DropKind.Files) return DragDropEffects.None;
        // NEXUS only keeps the path, it never moves the file: offer Copy (or Link when that is all the source allows).
        if (e.AllowedEffects.HasFlag(DragDropEffects.Copy)) return DragDropEffects.Copy;
        if (e.AllowedEffects.HasFlag(DragDropEffects.Link)) return DragDropEffects.Link;
        return DragDropEffects.None;
    }

    private void OnDragEnter(DragEventArgs e)
    {
        e.Handled = true;
        if (!_shelf.IsActive || _draggingOut) { e.Effects = DragDropEffects.None; return; } // no dropping onto ourselves
        _dropPreview.Stop();
        _drop = DropInfo.Inspect(e.Data, _shelf.Model);
        e.Effects = EffectFor(e);
        if (_presenter is not null) _presenter.DropInfo = _drop;
        _lastDragOver = Now;
        Input(m => m.DragEnter(Now));
    }

    private void OnDragOver(DragEventArgs e)
    {
        e.Handled = true;
        if (!_shelf.IsActive || _draggingOut || _drop is null) { e.Effects = DragDropEffects.None; return; }
        e.Effects = EffectFor(e);
        // OLE calls DragOver many times a second, even with the pointer still. Only refresh the safety
        // timeout once a second instead of re-evaluating the island on every call.
        var now = Now;
        if (now - _lastDragOver < TimeSpan.FromSeconds(1)) return;
        _lastDragOver = now;
        Input(m => m.DragOver(now));
    }

    private void OnDragLeave(DragEventArgs e)
    {
        e.Handled = true;
        if (_drop is null) return;
        Input(m => m.DragLeave(Now)); // confirmed after a short grace: moving between child elements also "leaves"
    }

    private void OnDrop(DragEventArgs e)
    {
        e.Handled = true;
        // Always tell the source what really happened. Left unset, WPF reports every allowed effect, including
        // Move, and a source app may then delete the original file as if NEXUS had taken it.
        e.Effects = _shelf.IsActive && !_draggingOut ? EffectFor(e) : DragDropEffects.None;
        var info = _drop;
        _drop = null;
        Input(m => m.Dropped(Now));
        if (!_shelf.IsActive || info?.Kind != DropKind.Files) return;

        try
        {
            var paths = DropInfo.ReadPaths(e.Data) ?? throw new InvalidOperationException("The drop carried no file list.");
            var result = _shelf.AddPaths(paths, Now);
            if (result.Total == 0) return;
            _hub.Pulse(new PulseSignal(SemanticColors.Neutral));
            string detail = result.Added == 0
                ? "Moved to the top"
                : ShelfModel.Describe(_shelf.Model.Items.Take(result.Total)) + (result.Evicted > 0 ? $" · oldest {result.Evicted} removed" : "");
            PostShelfFeedback(result.Added == 0 ? "Already on the shelf" : "Added to the shelf", detail, SemanticColors.Neutral);
            Log.Info("Shelf", $"Dropped {paths.Length} path(s): {result.Added} added, {result.MovedToTop} moved, {result.Evicted} evicted");
        }
        catch (Exception ex)
        {
            Log.Warn("Shelf", "Drop failed: " + ex.GetType().Name); // never the paths
            PostShelfFeedback("Couldn't add these files", "Try dragging them again", SemanticColors.Critical);
        }
    }

    /// <summary>A short glance after a drop. Feedback to the user's own action: quiet mode never hides it.</summary>
    private void PostShelfFeedback(string label, string detail, NexusColor color) => _hub.Post(new Activity
    {
        Id = "shelf.feedback",
        SourceId = ShelfProvider.ProviderId,
        Priority = ActivityPriority.Low,
        Category = ActivityCategory.Feedback,
        Duration = TimeSpan.FromSeconds(2.2),
        Accent = color,
        Content = new GlanceContent(Glyphs.Shelf, label, color, detail),
    });

    /// <summary>Preview lab: shows the drop zone for three seconds without a real drag.</summary>
    public void ShowDropZonePreview()
    {
        if (!_shelf.IsActive || _presenter is null) return;
        _presenter.DropInfo = DropInfo.Preview;
        Input(m => m.DragEnter(Now));
        _dropPreview.Stop();
        _dropPreview.Start();
    }

    #endregion

    #region IIslandCommands

    public ShelfProvider? Shelf => _shelf.IsActive ? _shelf : null;

    public void DragOutStarted() => _draggingOut = true;

    public void DragOutEnded()
    {
        _draggingOut = false;
        // If the drop target took activation, Island Center lost its "click elsewhere" signal while the drag
        // ran: close it now, as an outside click would have.
        if (_machine.State.Mode == IslandMode.Center && !_window.IsActive) Input(m => m.Collapse(Now));
    }

    public void RunAction(ActivityAction action)
    {
        _ = RunActionAsync(action);
        Input(m => m.Collapse(Now));
    }

    private static async Task RunActionAsync(ActivityAction action)
    {
        try { await action.Execute(); }
        catch (Exception ex) { Log.Warn("Island", $"Action '{action.Id}' failed", ex); }
    }

    public void Dismiss(TrackedActivity activity)
    {
        var own = activity.Activity.Actions.FirstOrDefault(a => a.Id == "dismiss");
        if (own is not null) _ = RunActionAsync(own);
        else if (activity.Activity.IsTransient) _hub.Remove(activity.Id);
        Input(m => m.Collapse(Now));
    }

    public void OpenCenter() => Input(m => m.OpenCenter(Now));
    public void Collapse() => Input(m => m.Collapse(Now));
    public void OpenSettings() => _openSettings();

    public void ToggleQuietMode() =>
        _settings.Update("behavior", s => s.Behavior.QuietMode = !s.Behavior.QuietMode);

    public void ToggleMicMute()
    {
        var capture = _platform.CaptureAudio;
        if (capture?.Current is { } c) capture.SetMuted(!c.Muted);
    }

    #endregion

    public void Dispose() { _wake.Stop(); _dropPreview.Stop(); _reveal.Dispose(); }
}
