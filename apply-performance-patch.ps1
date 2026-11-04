# NEXUS performance patch: lower RAM and CPU, adds Settings > Appearance > Eco mode.
# Run from the folder that contains Nexus.sln, after extracting this patch over it.
$ErrorActionPreference = "Stop"
if (-not (Test-Path ".\Nexus.sln")) { throw "Run this from the folder that contains Nexus.sln." }
if (-not (Test-Path ".\src\Nexus.App\Composition\MemoryTrimmer.cs")) { throw "MemoryTrimmer.cs is missing: extract the patch zip into this folder first." }

function Patch($file, $marker, $find, $replace) {
    $text = Get-Content $file -Raw
    if ($text.Contains($marker)) { Write-Host "  already done: $file ($marker)" -ForegroundColor DarkGray; return }
    if (-not $text.Contains($find)) { throw "Expected code not found in $file`n  looking for: $find`nSend this message to Copilot." }
    Set-Content $file ($text.Replace($find, $replace)) -Encoding UTF8 -NoNewline
    Write-Host "  patched: $file" -ForegroundColor Green
}

# 1. Runtime: leaner GC, ReadyToRun (less JIT CPU/RAM at start), English-only resources.
$f = "src\Nexus.App\Nexus.App.csproj"
Patch $f "ConcurrentGarbageCollection" '<TieredPGO>true</TieredPGO>' @'
<TieredPGO>false</TieredPGO>
    <!-- Memory: no background GC thread; the heap is small so blocking collections take milliseconds. -->
    <ConcurrentGarbageCollection>false</ConcurrentGarbageCollection>
    <PublishReadyToRun>true</PublishReadyToRun>
    <SatelliteResourceLanguages>en</SatelliteResourceLanguages>
'@
Patch $f "ConserveMemory" '</Project>' @'
  <ItemGroup>
    <!-- Ask the GC to favour a small heap over raw allocation speed. -->
    <RuntimeHostConfigurationOption Include="System.GC.ConserveMemory" Value="7" />
  </ItemGroup>
</Project>
'@

# 2. Eco mode setting.
Patch "src\Nexus.Core\Settings\NexusSettings.cs" "EcoMode" `
  'public IdleStyle IdleStyle { get; set; } = IdleStyle.Capsule;' @'
public IdleStyle IdleStyle { get; set; } = IdleStyle.Capsule;
    /// <summary>Lower RAM/CPU: 30 FPS cap, static equalizer, lighter shadow, more frequent memory trims.</summary>
    public bool EcoMode { get; set; }
'@

# 3. Frame clock: FPS cap (skipped ticks change nothing, so WPF does not redraw) + idle tracking.
$f = "src\Nexus.App\Motion\Animator.cs"
Patch $f "MaxFps =>" 'public static bool ReduceMotion { get; set; }' @'
public static bool ReduceMotion { get; set; }
    /// <summary>Eco mode (see AppearanceSettings.EcoMode).</summary>
    public static bool Eco { get; set; }
    /// <summary>Frame cap. On high-refresh monitors this avoids redrawing the overlay 120-165 times per second.</summary>
    public static int MaxFps => Eco ? 30 : 60;
    public static bool IsRunning => _hooked;
    public static DateTime LastFrameUtc { get; private set; } = DateTime.UtcNow;
'@
Patch $f "1.0 / MaxFps" 'if (time == _lastTime) return;' @'
if (time == _lastTime) return;
        // Frame cap: leaving visuals untouched on this tick means WPF neither re-renders nor re-copies the layered window.
        if (_lastTime != TimeSpan.Zero && (time - _lastTime).TotalSeconds < 1.0 / MaxFps - 0.002) return;
'@
Patch $f "LastFrameUtc = DateTime.UtcNow;" '_lastTime = time;' @'
_lastTime = time;
        LastFrameUtc = DateTime.UtcNow;
'@

# 4. Equalizer static in eco mode; spinner at 20 FPS.
$f = "src\Nexus.App\Ui\Equalizer.cs"
Patch $f "Animator.Eco" 'if (!animated) running = false;' 'if (!animated || Nexus.App.Motion.Animator.Eco) running = false;'
Patch $f "SetDesiredFrameRate(a, 20)" 'Timeline.SetDesiredFrameRate(a, 30);' 'Timeline.SetDesiredFrameRate(a, 20);'

# 5. Lighter shadow in eco mode (fewer primitives per animated frame).
Patch "src\Nexus.App\Rendering\IslandSurface.cs" "Animator.Eco ? 3 : 7" 'const int layers = 7;' 'int layers = Nexus.App.Motion.Animator.Eco ? 3 : 7;'

# 6. Apply eco mode live.
Patch "src\Nexus.App\Island\IslandController.cs" "Animator.Eco =" `
  'Animator.ReduceMotion = _settings.Current.Appearance.ReduceMotion;' @'
Animator.ReduceMotion = _settings.Current.Appearance.ReduceMotion;
        Animator.Eco = _settings.Current.Appearance.EcoMode;
'@

# 7. Settings window: really close (free its UI) instead of hiding; no leaked event handler; toggle for eco mode.
$f = "src\Nexus.App\SettingsUi\SettingsWindow.cs"
Patch $f "e.Cancel = false;" 'if (_settings.Current.General.KeepRunningWhenSettingsClosed) Hide();' `
  'if (_settings.Current.General.KeepRunningWhenSettingsClosed) e.Cancel = false; // close for real: frees the settings UI from RAM'
Patch $f "onStatus" 'providers.StatusChanged += (_, _) => Dispatcher.InvokeAsync(() => _refreshModules?.Invoke());' @'
EventHandler onStatus = (_, _) => Dispatcher.InvokeAsync(() => _refreshModules?.Invoke());
        providers.StatusChanged += onStatus;
        Closed += (_, _) => providers.StatusChanged -= onStatus;
'@
Patch $f 'Row("Eco mode"' 'Row("Animated equalizer", "Subtle bars while music plays (capped at 24 FPS).",' @'
Row("Eco mode", "Lower RAM and CPU: 30 FPS animations, static equalizer, lighter shadow and more frequent memory release. Recommended on 8 GB PCs and on battery.",
                Toggle(a.EcoMode, "appearance", (s, v) => s.Appearance.EcoMode = v)),
            Row("Animated equalizer", "Subtle bars while music plays (capped at 24 FPS).",
'@

# 8. Host: memory trimmer + recreate the settings window on demand.
$f = "src\Nexus.App\Composition\NexusHost.cs"
Patch $f "MemoryTrimmer? _trimmer" 'private bool _disposed;' @'
private bool _disposed;
    private MemoryTrimmer? _trimmer;
'@
Patch $f "_trimmer = new MemoryTrimmer()" 'Log.Info("Host", $"NEXUS started ({_providers.Providers.Count} providers)");' @'
Log.Info("Host", $"NEXUS started ({_providers.Providers.Count} providers)");
        _trimmer = new MemoryTrimmer();
        _trimmer.Start();
'@
Patch $f "_trimmer?.Dispose();" '_fullscreen?.Dispose();' @'
_trimmer?.Dispose();
        _fullscreen?.Dispose();
'@
Patch $f "_settingsWindow = null" '_settingsWindow ??= new SettingsWindow(_settings, _providers, _preview, () => _ = ExitAsync());' @'
if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(_settings, _providers, _preview, () => _ = ExitAsync());
            _settingsWindow.Closed += (_, _) => { _settingsWindow = null; MemoryTrimmer.RequestSoon(); };
        }
'@

Write-Host "`nDone. Rebuild and republish (see instructions)." -ForegroundColor Cyan
