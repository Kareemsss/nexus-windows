using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Nexus.App.Center;
using Nexus.App.Motion;
using Nexus.App.Shelf;
using Nexus.App.Theming;
using Nexus.App.Widgets;
using Nexus.Core.Activities;
using Nexus.Core.Diagnostics;
using Nexus.Core.Motion;
using Nexus.Core.Settings;
using Nexus.Core.State;

namespace Nexus.App.Island;

/// <summary>
/// Turns an <see cref="IslandState"/> into motion. Owns the island geometry springs, the ambient colour,
/// edge pulses, the content layers (cross-fading views) and the satellite capsule.
///
/// Motion hierarchy (never everything at once):
///   1. the container morphs first (width/height/radius springs, slight overshoot);
///   2. primary content fades/slides in after a short lead (~60 ms);
///   3. secondary content (Island Center sections) staggers in last.
/// </summary>
public sealed class IslandPresenter : IAnimatable
{
    private const double SatelliteGap = 8;

    private readonly IslandWindow _w;
    private readonly ThemeEngine _themes;
    private readonly WidgetRegistry _registry;
    private readonly SettingsStore _settings;
    private WidgetContext _ctx;

    private readonly Spring _width = new(104, SpringConfig.Morph, 0.05);
    private readonly Spring _height = new(26, SpringConfig.Morph, 0.05);
    private readonly Spring _radius = new(13, SpringConfig.Morph, 0.05);
    private readonly Spring _accentStrength = new(0, SpringConfig.Ambient, 0.002);
    private readonly Spring _hover = new(0, SpringConfig.Content, 0.002);
    private readonly Spring _alert = new(0, SpringConfig.Ambient, 0.002);
    private readonly Spring _satWidth = new(30, SpringConfig.Morph, 0.05);
    private readonly Spring _satPresence = new(0, SpringConfig.Content, 0.002);
    private readonly ColorSpring _accent;
    private readonly Spring _enter = new(0, SpringConfig.Morph, 0.3);
    private readonly TranslateTransform _enterShift = new(), _satShift = new();
    private Action? _exitDone;

    private readonly List<ViewLayer> _layers = new();
    private readonly SatelliteView _satellite = new();
    private ViewLayer? _current;
    private string? _key;
    private IslandState _state = IslandState.Initial;
    private bool _satelliteHovered;

    private PulseSignal? _pulse;
    private double _pulseT, _pulseDuration;
    private double _alertPhase;

    public IslandPresenter(IslandWindow window, ThemeEngine themes, WidgetRegistry registry, SettingsStore settings, IIslandCommands commands)
    {
        _w = window;
        _themes = themes;
        _registry = registry;
        _settings = settings;
        _ctx = new WidgetContext(themes.Palette, commands, settings.Current);
        _accent = new ColorSpring(themes.Current.ThemeAccent);
        _w.Surface.Theme = themes.Current;
        _w.SatelliteSurface.Theme = themes.Current;
        _w.SatelliteFrame.Children.Add(_satellite.Content);
        _w.IslandRoot.RenderTransform = _enterShift;
        _w.SatelliteFrame.RenderTransform = _satShift;

        themes.Changed += (_, _) =>
        {
            _ctx = new WidgetContext(themes.Palette, commands, settings.Current);
            _w.Surface.Theme = themes.Current;
            _w.SatelliteSurface.Theme = themes.Current;
            Animator.Personality = themes.Current.MotionSnap;
            _key = null; // rebuild views with the new palette
            Render(_state);
        };
        Animator.Personality = themes.Current.MotionSnap;

        var root = _w.IslandRoot;
        root.MouseEnter += (_, _) => PointerEntered?.Invoke(this, EventArgs.Empty);
        root.MouseLeave += (_, _) => PointerExited?.Invoke(this, EventArgs.Empty);
        root.MouseLeftButtonUp += (_, e) => { if (!e.Handled) { e.Handled = true; Clicked?.Invoke(this, EventArgs.Empty); } };
        root.MouseRightButtonUp += (_, e) => { e.Handled = true; RightClicked?.Invoke(this, EventArgs.Empty); };
        root.MouseWheel += (_, e) => { e.Handled = true; Wheel?.Invoke(this, Math.Sign(e.Delta)); };

        // File Shelf: drags are reported to the controller, which decides the drop effect. Both the island and
        // the satellite accept files; AllowDrop is switched on only while the shelf module runs.
        root.DragEnter += (_, e) => DragEntered?.Invoke(this, e);
        root.DragOver += (_, e) => DragMoved?.Invoke(this, e);
        root.DragLeave += (_, e) => DragLeft?.Invoke(this, e);
        root.Drop += (_, e) => Dropped?.Invoke(this, e);

        var sat = _w.SatelliteFrame;
        sat.DragEnter += (_, e) => DragEntered?.Invoke(this, e);
        sat.DragOver += (_, e) => DragMoved?.Invoke(this, e);
        sat.DragLeave += (_, e) => DragLeft?.Invoke(this, e);
        sat.Drop += (_, e) => Dropped?.Invoke(this, e);
        sat.MouseEnter += (_, _) => { _satelliteHovered = true; RefreshSatellite(); };
        sat.MouseLeave += (_, _) => { _satelliteHovered = false; RefreshSatellite(); };
        sat.MouseLeftButtonUp += (_, e) => { e.Handled = true; SatelliteClicked?.Invoke(this, EventArgs.Empty); };
        sat.MouseRightButtonUp += (_, e) => { e.Handled = true; RightClicked?.Invoke(this, EventArgs.Empty); };

        Layout();
    }

    public event EventHandler? PointerEntered;
    public event EventHandler? PointerExited;
    public event EventHandler? Clicked;
    public event EventHandler? RightClicked;
    public event EventHandler? SatelliteClicked;
    public event EventHandler<int>? Wheel;
    public event EventHandler<DragEventArgs>? DragEntered;
    public event EventHandler<DragEventArgs>? DragMoved;
    public event EventHandler<DragEventArgs>? DragLeft;
    public event EventHandler<DragEventArgs>? Dropped;

    /// <summary>What is being dragged over the island; read when the drop zone view is built.</summary>
    public DropInfo DropInfo { get; set; } = DropInfo.Preview;

    public void SetDropEnabled(bool enabled)
    {
        _w.IslandRoot.AllowDrop = enabled;
        _w.SatelliteFrame.AllowDrop = enabled;
    }

    #region State â†’ targets

    public void Render(IslandState state)
    {
        _state = state;
        if (state.Mode == IslandMode.Hidden)
        {
            // The window is about to hide: stop timers/animations so a hidden island costs nothing.
            // The view is rebuilt (and OnShown called) when the island returns.
            try { _current?.View.OnHidden(); } catch (Exception ex) { Log.Warn("Presenter", "OnHidden failed", ex); }
            _key = null;
            return;
        }

        var key = KeyFor(state);
        if (key != _key || _current is null)
        {
            _key = key;
            SwapTo(CreateView(state));
        }
        else
        {
            try { _current.View.Update(state.Foreground); }
            catch (Exception ex) { Log.Error("Presenter", "View update failed; rebuilding", ex); _key = null; SwapTo(CreateView(state)); }
        }

        var size = Measure(_current!.View, state.Mode);
        _current.SetSize(size);

        bool grow = state.Hovered && state.Mode == IslandMode.Compact && _settings.Current.Behavior.Hover != HoverBehavior.None;
        _width.Target = size.Width + (grow ? 8 : 0);
        _height.Target = size.Height + (grow ? 2 : 0);
        _radius.Target = _themes.Current.RadiusFor(_height.Target);
        if (_settings.Current.Appearance.IdleStyle == IdleStyle.Line && state.Mode == IslandMode.Idle) _radius.Target = 3;

        _hover.Target = state.Hovered && _settings.Current.Behavior.Hover != HoverBehavior.None ? 1 : 0;
        _alert.Target = state.Alert ? 1 : 0;
        _accent.SetTarget(AccentFor(state));
        _accentStrength.Target = StrengthFor(state);

        RefreshSatellite();
        Animator.Wake(this);
    }

    private void RefreshSatellite()
    {
        bool visible = _state.Indicators.Count > 0 && _state.Mode is not (IslandMode.Center or IslandMode.Hidden);
        if (visible) _satellite.Update(_state.Indicators, _themes.Palette, _satelliteHovered);
        _satPresence.Target = visible ? 1 : 0;
        if (visible) _satWidth.Target = _satellite.Measure().Width;
        _w.SatelliteFrame.IsHitTestVisible = visible;
        Animator.Wake(this);
    }

    private string KeyFor(IslandState s) => s.Mode switch
    {
        IslandMode.Idle => "idle:" + _settings.Current.Appearance.IdleStyle,
        IslandMode.Peek => "peek",
        IslandMode.Center => "center",
        IslandMode.Drop => $"drop:{DropInfo}",
        _ =>$"{s.Mode}:{s.Foreground?.Id}:{s.Foreground?.Activity.Content.GetType().Name}",
    };

    private IWidgetView CreateView(IslandState s)
    {
        try
        {
            switch (s.Mode)
            {
                case IslandMode.Idle:
                    return new IdleView(IdleGeometry.For(_settings.Current.Appearance.IdleStyle));
                case IslandMode.Peek:
                    return new PeekView(_ctx);
                case IslandMode.Center:
                    return new CenterView(_ctx);
                case IslandMode.Drop:
                    return new DropZoneView(_ctx, DropInfo,
                        _ctx.Commands.Shelf?.Model.Items ?? (IReadOnlyList<Nexus.Core.Shelf.ShelfItem>)Array.Empty<Nexus.Core.Shelf.ShelfItem>());
                default:
                    if (s.Foreground is null) return new IdleView(IdleGeometry.For(_settings.Current.Appearance.IdleStyle));
                    return _registry.Resolve(s.Foreground.Activity.Content).Create(s.Foreground, s.Mode, _ctx);
            }
        }
        catch (Exception ex)
        {
            // A broken widget (e.g. a plugin) must not break the island: fall back to an empty capsule.
            Log.Error("Presenter", $"Widget creation failed for {s.Foreground?.Id}", ex);
            return new IdleView(new Size(140, 32));
        }
    }

    private static Size Measure(IWidgetView view, IslandMode mode)
    {
        var e = view.Element;
        e.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var d = e.DesiredSize;
        (double minW, double maxW, double minH, double maxH) = mode switch
        {
            IslandMode.Idle => (4, 200, 4, 40),
            IslandMode.Peek => (200, 320, 28, 40),
            IslandMode.Compact => (140, 440, 28, 60),
            IslandMode.Expanded => (280, 440, 64, 320),
            IslandMode.Drop => (320, 440, 60, 160),
            _ => (420, IslandWindow.CanvasSize.Width - 40, 240, IslandWindow.CanvasSize.Height - 60),
        };
        // Whole DIPs at rest keep text crisp; springs still interpolate sub-pixel during motion.
        return new Size(Math.Ceiling(Math.Clamp(d.Width, minW, maxW)), Math.Ceiling(Math.Clamp(d.Height, minH, maxH)));
    }

    private Color AccentFor(IslandState s)
    {
        var t = _themes.Current;
        if (t.Accent == AccentBehavior.Monochrome) return Color.FromRgb(0xE6, 0xE8, 0xEC);
        if (t.Accent == AccentBehavior.ThemeAccent || s.Mode == IslandMode.Drop) return t.ThemeAccent;
        if (s.Foreground?.Activity.Accent is { } a) return Palette.ToColor(a);
        // Ambient drift: persistent state (low battery, privacy) tints the idle island very slightly.
        var ambient = s.Indicators.FirstOrDefault(i => i.Activity.Accent is not null && i.Activity.Category is ActivityCategory.Power)
                      ?? s.Indicators.FirstOrDefault(i => i.Activity.Accent is not null && i.Activity.Category is ActivityCategory.Privacy);
        return ambient?.Activity.Accent is { } c ? Palette.ToColor(c) : t.ThemeAccent;
    }

    private static double StrengthFor(IslandState s)
    {
        if (s.Alert) return 1;
        bool contextual = s.Foreground?.Activity.Accent is not null || s.Indicators.Any(i => i.Activity.Accent is not null);
        return s.Mode switch
        {
            IslandMode.Idle => contextual ? 0.45 : 0.18,
            IslandMode.Peek => 0.3,
            IslandMode.Compact => contextual ? 0.75 : 0.35,
            IslandMode.Expanded => contextual ? 1.0 : 0.45,
            IslandMode.Center => 0.55,
            IslandMode.Drop => 0.8, // the island is a live target: it lights up like an expanded activity
            _ => 0,
        };
    }

    private void SwapTo(IWidgetView view)
    {
        if (_current is not null)
        {
            _current.Leave();
            try { _current.View.OnHidden(); } catch (Exception ex) { Log.Warn("Presenter", "OnHidden failed", ex); }
        }
        if (view is IResizableView resizable)
            resizable.ContentResized += (_, _) => { if (ReferenceEquals(_current?.View, view)) Render(_state); };
        var layer = new ViewLayer(view, _w.Viewport, firstLayer: _current is null);
        _layers.Add(layer);
        _current = layer;
        try { view.OnShown(); } catch (Exception ex) { Log.Warn("Presenter", "OnShown failed", ex); }
    }

    #endregion

    #region Edge entrance / exit

    private double OffscreenOffset()
    {
        double d = PositionManager.NearMargin + Math.Max(_height.Value, _height.Target) + 40;
        return _settings.Current.Position.Anchor == IslandAnchor.BottomCenter ? d : -d;
    }

    public void PrepareEntrance()
    {
        bool midExit = _exitDone is not null;
        _exitDone = null;
        if (Animator.ReduceMotion) { _enter.Snap(0); return; }
        if (!midExit && !_w.IsVisible) _enter.Snap(OffscreenOffset());
        _enter.Target = 0;
        Animator.Wake(this);
    }

    public void PlayExit(Action done)
    {
        if (Animator.ReduceMotion || !_w.IsVisible) { done(); return; }
        _exitDone = done;
        _enter.Target = OffscreenOffset();
        Animator.Wake(this);
    }

    #endregion

    #region Pulses

    public void Pulse(PulseSignal signal)
    {
        if (_state.Mode == IslandMode.Hidden) return;
        _pulse = signal;
        _pulseT = 0;
        _pulseDuration = signal.Style switch
        {
            PulseStyle.Sweep => 1.5,
            PulseStyle.Flash => 0.6,
            _ => 0.95,
        };
        if (Animator.ReduceMotion) _pulseDuration *= 0.6;
        Animator.Wake(this);
    }

    #endregion

    #region Frame loop

    public bool Advance(double dt)
    {
        bool moving = false;
        moving |= _width.Step(dt);
        moving |= _height.Step(dt);
        moving |= _radius.Step(dt);
        moving |= _accentStrength.Step(dt);
        moving |= _hover.Step(dt);
        moving |= _alert.Step(dt);
        moving |= _accent.Step(dt);
        moving |= _satWidth.Step(dt);
        moving |= _satPresence.Step(dt);
        bool enterMoving = _enter.Step(dt);
        moving |= enterMoving;
        if (_exitDone is not null && !enterMoving)
        {
            var done = _exitDone;
            _exitDone = null;
            done();
            _enter.Snap(0);
        }

        foreach (var layer in _layers) moving |= layer.Step(dt);
        _layers.RemoveAll(l => l.IsGone);

        if (_pulse is not null)
        {
            _pulseT += dt / _pulseDuration;
            if (_pulseT >= 1) _pulse = null;
            moving = true;
        }

        if (_alert.Value > 0.01)
        {
            _alertPhase = (_alertPhase + dt / 1.8) % 1.0; // slow breathing while a critical alert is up
            moving = true;
        }

        Layout();
        return moving;
    }

    private void Layout()
    {
        var anchor = _settings.Current.Position.Anchor;
        double w = Math.Max(1, _width.Value), h = Math.Max(1, _height.Value);
        double r = Math.Clamp(_radius.Value, 0, Math.Min(w, h) / 2);
        var canvas = IslandWindow.CanvasSize;
        const double near = PositionManager.NearMargin, side = PositionManager.SideMargin;

        double left = anchor switch
        {
            IslandAnchor.TopLeft => side,
            IslandAnchor.TopRight => canvas.Width - side - w,
            _ => (canvas.Width - w) / 2,
        };
        bool bottom = anchor == IslandAnchor.BottomCenter;
        double top = bottom ? canvas.Height - near - h : near;

        // Island frame
        Canvas.SetLeft(_w.Frame, left);
        Canvas.SetTop(_w.Frame, top);
        _w.Frame.Width = w;
        _w.Frame.Height = h;
        _w.Viewport.Clip = new RectangleGeometry(new Rect(0, 0, w, h), r, r);

        // Generous, invisible hover target for the thin idle styles.
        double padH = Math.Max(h, 24);
        Canvas.SetLeft(_w.HitPad, left - 6);
        Canvas.SetTop(_w.HitPad, bottom ? top + h - padH : top);
        _w.HitPad.Width = w + 12;
        _w.HitPad.Height = padH;

        var s = _w.Surface;
        s.Radius = r;
        s.Accent = _accent.Value;
        s.AccentStrength = Math.Clamp(_accentStrength.Value, 0, 1);
        s.Hover = Math.Clamp(_hover.Value, 0, 1);
        s.Alert = Math.Clamp(_alert.Value, 0, 1);
        s.AlertPhase = _alertPhase;
        if (_pulse is { } p)
        {
            s.PulseColor = Palette.ToColor(p.Color);
            s.PulseStyle = p.Style;
            s.PulseProgress = Math.Clamp(_pulseT, 0.0001, 1);
        }
        else s.PulseProgress = 0;
        s.InvalidateVisual();

        // Satellite: docks beside the island and is physically pushed outward as the island stretches.
        double presence = Math.Clamp(_satPresence.Value, 0, 1);
        double sw = Math.Max(SatelliteView.Height, _satWidth.Value);
        double sh = SatelliteView.Height;
        double row = Math.Min(h, Widgets.Layout.CompactHeight);
        double satTop = bottom ? top + h - row + (row - sh) / 2 : top + (row - sh) / 2;
        double slide = (1 - presence) * 14; // emerges from under the island
        double satLeft = anchor == IslandAnchor.TopRight ? left - SatelliteGap - sw + slide : left + w + SatelliteGap - slide;
        Canvas.SetLeft(_w.SatelliteFrame, satLeft);
        Canvas.SetTop(_w.SatelliteFrame, satTop);
        _w.SatelliteFrame.Width = sw;
        _w.SatelliteFrame.Height = sh;
        _w.SatelliteFrame.Opacity = presence;
        _enterShift.Y = _enter.Value;
        _satShift.Y = _enter.Value;
        _w.SatelliteFrame.Visibility = presence < 0.01 ? Visibility.Hidden : Visibility.Visible;
        var ss = _w.SatelliteSurface;
        ss.Radius = sh / 2;
        ss.Accent = _accent.Value;
        ss.AccentStrength = 0.25;
        ss.ShadowStrength = 0.7;
        ss.InvalidateVisual();
    }

    #endregion

    /// <summary>One content layer inside the island viewport, with its own enter/leave motion.</summary>
    private sealed class ViewLayer
    {
        private readonly Grid _viewport;
        private readonly Grid _host;
        private readonly ScaleTransform _scale = new(1, 1);
        private readonly TranslateTransform _shift = new();
        private readonly Spring _opacity, _scaleSpring, _offset;
        private readonly Spring _fadeOut = new(1, new SpringConfig(650, 1.0), 0.003);
        private double _delay;
        private bool _leaving;

        public ViewLayer(IWidgetView view, Grid viewport, bool firstLayer)
        {
            View = view;
            _viewport = viewport;
            _host = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new TransformGroup { Children = { _scale, _shift } },
            };
            _host.Children.Add(view.Element);
            bool reduce = Animator.ReduceMotion;
            _opacity = new Spring(firstLayer ? 1 : 0, SpringConfig.Content, 0.003) { Target = 1 };
            _scaleSpring = new Spring(firstLayer || reduce ? 1 : 0.94, SpringConfig.Content, 0.0005) { Target = 1 };
            _offset = new Spring(firstLayer || reduce ? 0 : 6, SpringConfig.Content, 0.05) { Target = 0 };
            _delay = firstLayer || reduce ? 0 : 0.06; // container leads, content follows
            Apply();
            viewport.Children.Add(_host);
        }

        public IWidgetView View { get; }
        public bool IsGone { get; private set; }

        public void SetSize(Size size)
        {
            _host.Width = size.Width;
            _host.Height = size.Height;
        }

        public void Leave()
        {
            _leaving = true;
            _delay = 0;
            // Leaving content uses a fast, critically damped curve: it must never linger behind new content.
            _fadeOut.Snap(_opacity.Value);
            _fadeOut.Target = 0;
            _scaleSpring.Target = 0.97;
            _offset.Target = -4;
            _host.IsHitTestVisible = false;
        }

        /// <summary>Returns true while moving.</summary>
        public bool Step(double dt)
        {
            if (IsGone) return false;
            if (_delay > 0) { _delay -= dt; return true; }
            bool moving;
            if (_leaving)
            {
                moving = _fadeOut.Step(dt) | _scaleSpring.Step(dt) | _offset.Step(dt);
                if (_fadeOut.Value <= 0.004)
                {
                    _viewport.Children.Remove(_host);
                    IsGone = true;
                    return false;
                }
            }
            else
            {
                moving = _opacity.Step(dt) | _scaleSpring.Step(dt) | _offset.Step(dt);
            }
            Apply();
            return moving;
        }

        private void Apply()
        {
            _host.Opacity = Math.Clamp(_leaving ? _fadeOut.Value : _opacity.Value, 0, 1);
            _scale.ScaleX = _scale.ScaleY = _scaleSpring.Value;
            _shift.Y = _offset.Value;
        }
    }
}
