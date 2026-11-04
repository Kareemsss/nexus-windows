# NEXUS: "Hide completely" + "Song pop-ups". Run from the folder that contains Nexus.sln,
# after extracting this patch over it. Requires the edge-reveal patch to be applied first.
$ErrorActionPreference = "Stop"
if (-not (Test-Path ".\Nexus.sln")) { throw "Run this from the folder that contains Nexus.sln." }
if (-not (Test-Path ".\src\Nexus.App\Composition\SongPopupService.cs")) { throw "SongPopupService.cs is missing: extract the patch zip into this folder first." }
if (-not (Select-String -Quiet -Path "src\Nexus.App\Island\IslandController.cs" -Pattern "EdgeRevealWatcher")) { throw "Apply the edge-reveal patch first." }

function Patch($file, $marker, $find, $replace) {
    $text = Get-Content $file -Raw
    if ($text.Contains($marker)) { Write-Host "  already done: $file ($marker)" -ForegroundColor DarkGray; return }
    if (-not $text.Contains($find)) { throw "Expected code not found in $file`n  looking for: $find`nSend this message to Copilot." }
    Set-Content $file ($text.Replace($find, $replace)) -Encoding UTF8 -NoNewline
    Write-Host "  patched: $file" -ForegroundColor Green
}

# 1. Activity flag for things allowed to surface while the island is hidden.
Patch "src\Nexus.Core\Activities\Activity.cs" "ShowWhenHidden" `
  'public bool PreferExpanded { get; init; }' @'
public bool PreferExpanded { get; init; }

    /// <summary>May appear even when the island is set to Hide completely (song pop-ups).</summary>
    public bool ShowWhenHidden { get; init; }
'@

# 2. Settings.
$f = "src\Nexus.Core\Settings\NexusSettings.cs"
Patch $f "HideCompletely" 'public bool QuietMode { get; set; }' @'
public bool QuietMode { get; set; }
    /// <summary>Keep the island hidden at all times (critical alerts, song pop-ups and edge reveal still work).</summary>
    public bool HideCompletely { get; set; }
    /// <summary>Show new songs briefly, then tuck away, instead of keeping music on the island while it plays.</summary>
    public bool SongPopup { get; set; }
    public double SongPopupSeconds { get; set; } = 3;
'@
Patch $f "SongPopupSeconds = Math.Clamp" 'Behavior.AutoCollapseSeconds = Math.Clamp(Behavior.AutoCollapseSeconds, 0.5, 15);' @'
Behavior.AutoCollapseSeconds = Math.Clamp(Behavior.AutoCollapseSeconds, 0.5, 15);
        Behavior.SongPopupSeconds = Math.Clamp(Behavior.SongPopupSeconds, 1, 10);
'@

# 3. Controller: policy + edge reveal also armed by Hide completely.
$f = "src\Nexus.App\Island\IslandController.cs"
Patch $f "HideAll:" 'QuietMode: _settings.Current.Behavior.QuietMode);' @'
QuietMode: _settings.Current.Behavior.QuietMode,
        HideAll: _settings.Current.Behavior.HideCompletely && !_reveal.IsRevealed,
        MediaPopupsOnly: _settings.Current.Behavior.SongPopup && !_reveal.IsRevealed);
'@
Patch $f "b.HideCompletely ||" '_reveal.SetArmed(_fullscreen && b.Fullscreen == FullscreenBehavior.Hide && b.RevealOnEdgeHover);' `
  '_reveal.SetArmed(b.RevealOnEdgeHover && (b.HideCompletely || (_fullscreen && b.Fullscreen == FullscreenBehavior.Hide)));'

# 4. Host: song pop-up service; create the window handle up front so a never-shown island can still be revealed.
$f = "src\Nexus.App\Composition\NexusHost.cs"
Patch $f "SongPopupService? _songPopup" 'private PreviewEvents? _preview;' @'
private PreviewEvents? _preview;
    private SongPopupService? _songPopup;
'@
Patch $f "new SongPopupService" '_preview = new PreviewEvents(_hub);' @'
_preview = new PreviewEvents(_hub);
        _songPopup = new SongPopupService(_hub, _settings);
'@
Patch $f "_songPopup?.Dispose();" '_controller?.Dispose();' @'
_songPopup?.Dispose();
        _controller?.Dispose();
'@
Patch $f "EnsureHandle()" '_position = new PositionManager(_window, _settings);' @'
_position = new PositionManager(_window, _settings);
        // Hide completely may keep the island from ever being shown: create and place its window anyway, so the
        // edge reveal knows where the island lives.
        new System.Windows.Interop.WindowInteropHelper(_window).EnsureHandle();
        _position.Apply();
'@

# 5. Settings > Behavior: Visibility section.
Patch "src\Nexus.App\SettingsUi\SettingsWindow.cs" 'Row("Hide completely"' `
  'Toggle(b.QuietMode, "behavior", (s, v) => s.Behavior.QuietMode = v))));' @'
Toggle(b.QuietMode, "behavior", (s, v) => s.Behavior.QuietMode = v))));

        p.Children.Add(SectionTitle("Visibility"));
        p.Children.Add(Card(
            Row("Hide completely", "Keeps the island off-screen all the time, not only during fullscreen apps. Critical alerts and song pop-ups still slide out; with Reveal at screen edge on, push the pointer against the top edge to bring it out. Open Settings from the tray icon.",
                Toggle(b.HideCompletely, "behavior", (s, v) => s.Behavior.HideCompletely = v), "\uED1A"),
            Row("Song pop-ups", "When a new song starts in Spotify, YouTube or any media app, show it briefly and then tuck it away. Off: music stays on the island while it plays.",
                Toggle(b.SongPopup, "behavior", (s, v) => s.Behavior.SongPopup = v), Glyphs.Music),
            Row("Pop-up duration", null,
                new SettingSlider(1, 10, b.SongPopupSeconds, v => $"{v:0.0} s", v => _settings.Update("behavior", s => s.Behavior.SongPopupSeconds = Math.Round(v, 1)), liveUpdate: false), Glyphs.Clock)));
'@

Write-Host "`nDone. Now build and publish." -ForegroundColor Cyan
