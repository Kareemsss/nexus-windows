$ErrorActionPreference = "Stop"
if (-not (Test-Path ".\Nexus.sln")) { throw "Run this from the folder that contains Nexus.sln." }
if (-not (Test-Path ".\src\Nexus.App\Island\EdgeRevealWatcher.cs")) { throw "Create src\Nexus.App\Island\EdgeRevealWatcher.cs first." }

function Patch($file, $marker, $find, $replace) {
    $text = Get-Content $file -Raw
    if ($text.Contains($marker)) { Write-Host "  already done: $file ($marker)" -ForegroundColor DarkGray; return }
    if (-not $text.Contains($find)) { throw "Expected code not found in $file`n  looking for: $find`nSend this message to Copilot." }
    Set-Content $file ($text.Replace($find, $replace)) -Encoding UTF8 -NoNewline
    Write-Host "  patched: $file" -ForegroundColor Green
}

# 1. Setting
Patch "src\Nexus.Core\Settings\NexusSettings.cs" "RevealOnEdgeHover" `
  'public bool ScrollAdjustsVolume { get; set; } = true;' @'
public bool ScrollAdjustsVolume { get; set; } = true;
    public bool RevealOnEdgeHover { get; set; } = true;
'@

# 2. Presenter: slide in from / out to the screen edge
$f = "src\Nexus.App\Island\IslandPresenter.cs"
Patch $f "_enterShift" 'private readonly ColorSpring _accent;' @'
private readonly ColorSpring _accent;
    private readonly Spring _enter = new(0, SpringConfig.Morph, 0.3);
    private readonly TranslateTransform _enterShift = new(), _satShift = new();
    private Action? _exitDone;
'@
Patch $f "RenderTransform = _enterShift" '_w.SatelliteFrame.Children.Add(_satellite.Content);' @'
_w.SatelliteFrame.Children.Add(_satellite.Content);
        _w.IslandRoot.RenderTransform = _enterShift;
        _w.SatelliteFrame.RenderTransform = _satShift;
'@
Patch $f "enterMoving" 'moving |= _satPresence.Step(dt);' @'
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
'@
Patch $f "_enterShift.Y =" '_w.SatelliteFrame.Opacity = presence;' @'
_w.SatelliteFrame.Opacity = presence;
        _enterShift.Y = _enter.Value;
        _satShift.Y = _enter.Value;
'@
Patch $f "PrepareEntrance" '#region Pulses' @'
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
'@

# 3. Controller
$f = "src\Nexus.App\Island\IslandController.cs"
Patch $f "EdgeRevealWatcher _reveal" 'private IslandMode _lastMode = IslandMode.Idle;' @'
private IslandMode _lastMode = IslandMode.Idle;
    private readonly EdgeRevealWatcher _reveal;
    private bool _pointerOver;
'@
Patch $f "new EdgeRevealWatcher" '_exit = exit;' @'
_exit = exit;
        _reveal = new EdgeRevealWatcher(window, settings,
            holding: () => _pointerOver || _machine.State.Mode is IslandMode.Center or IslandMode.Expanded);
        _reveal.RevealChanged += (_, _) => OnRevealChanged();
'@
Patch $f "_pointerOver = true" 'presenter.PointerEntered += (_, _) => Input(m => m.PointerEnter(Now));' `
  'presenter.PointerEntered += (_, _) => { _pointerOver = true; Input(m => m.PointerEnter(Now)); };'
Patch $f "_pointerOver = false; Input" 'presenter.PointerExited += (_, _) => Input(m => m.PointerLeave(Now));' `
  'presenter.PointerExited += (_, _) => { _pointerOver = false; Input(m => m.PointerLeave(Now)); };'
Patch $f "_reveal.IsRevealed ? FullscreenBehavior.AlwaysShow" 'Fullscreen: _settings.Current.Behavior.Fullscreen,' `
  'Fullscreen: _reveal.IsRevealed ? FullscreenBehavior.AlwaysShow : _settings.Current.Behavior.Fullscreen,'
Patch $f "UpdateRevealArming(); // fullscreen changed" 'Log.Info("Island", fullscreen ? "Fullscreen app detected" : "Fullscreen app closed");' @'
Log.Info("Island", fullscreen ? "Fullscreen app detected" : "Fullscreen app closed");
        UpdateRevealArming(); // fullscreen changed
'@
Patch $f "UpdateRevealArming(); // settings changed" '_machine.HoverBehavior = b.Hover;' @'
_machine.HoverBehavior = b.Hover;
        UpdateRevealArming(); // settings changed
'@
Patch $f "PlayExit(" 'if (_windowShown) { _window.Hide(); _windowShown = false; }' @'
if (_windowShown)
            {
                _windowShown = false;
                _presenter.PlayExit(() => { if (!_windowShown) _window.Hide(); });
            }
'@
Patch $f "PrepareEntrance()" '_window.Show();' @'
_presenter.PrepareEntrance();
            _window.Show();
'@
Patch $f "void OnRevealChanged" '#region IIslandCommands' @'
private void UpdateRevealArming()
    {
        if (_reveal is null) return;
        var b = _settings.Current.Behavior;
        _reveal.SetArmed(_fullscreen && b.Fullscreen == FullscreenBehavior.Hide && b.RevealOnEdgeHover);
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

    #region IIslandCommands
'@
Patch $f "_reveal.Dispose()" 'public void Dispose() => _wake.Stop();' 'public void Dispose() { _wake.Stop(); _reveal.Dispose(); }'

# 4. Settings > Behavior toggle
Patch "src\Nexus.App\SettingsUi\SettingsWindow.cs" "Reveal at screen edge" `
  'b.Fullscreen, v => _settings.Update("behavior", s => s.Behavior.Fullscreen = v)))));' @'
b.Fullscreen, v => _settings.Update("behavior", s => s.Behavior.Fullscreen = v)))));
        p.Children.Add(Card(Row("Reveal at screen edge",
            "With Hide completely, push the pointer against the top edge (bottom edge if NEXUS sits at the bottom) to slide the island out. Move away and it tucks back in.",
            Toggle(b.RevealOnEdgeHover, "behavior", (s, v) => s.Behavior.RevealOnEdgeHover = v))).WithMargin(0, 8, 0, 0));
'@

Write-Host "`nDone. Now build and publish." -ForegroundColor Cyan