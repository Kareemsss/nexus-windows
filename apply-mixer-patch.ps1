# NEXUS: per-app volume mixer in Island Center (Batch 1, part 1).
# Run from the folder that contains Nexus.sln, after extracting this patch over it.
# Requires the Island Center customization patch. This script is pure ASCII and edits files as UTF-8.
$ErrorActionPreference = "Stop"
if (-not (Test-Path ".\Nexus.sln")) { throw "Run this from the folder that contains Nexus.sln." }
foreach ($f in "src\Nexus.Platform\Services\AudioSessionService.cs", "src\Nexus.App\Widgets\IResizableView.cs", "src\Nexus.App\Center\QuickActionCatalog.cs") {
    if (-not (Test-Path $f)) { throw "$f is missing. Extract the patch zip here first (and make sure the Island Center patch is installed)." }
}
$utf8 = New-Object System.Text.UTF8Encoding($false)

function Patch($file, $marker, $find, $replace) {
    $path = (Resolve-Path $file).Path
    $text = [IO.File]::ReadAllText($path, $utf8)
    if ($text.Contains($marker)) { Write-Host "  already done: $file" -ForegroundColor DarkGray; return }
    if (-not $text.Contains($find)) { throw "Expected code not found in $file`n  looking for: $find`nSend this message to Copilot." }
    [IO.File]::WriteAllText($path, $text.Replace($find, $replace), $utf8)
    Write-Host "  patched: $file" -ForegroundColor Green
}

# 1. More room for Island Center (the overlay is transparent and click-through, so this costs ~1 MB at most).
Patch "src\Nexus.App\Island\IslandWindow.cs" "new(860, 720)" `
  "public static readonly Size CanvasSize = new(860, 560);" `
  "public static readonly Size CanvasSize = new(860, 720);"

# 2. Presenter: re-measure when a view changes size by itself (mixer rows appearing/disappearing).
Patch "src\Nexus.App\Island\IslandPresenter.cs" "IResizableView resizable" `
  "        var layer = new ViewLayer(view, _w.Viewport, firstLayer: _current is null);" @"
        if (view is IResizableView resizable)
            resizable.ContentResized += (_, _) => { if (ReferenceEquals(_current?.View, view)) Render(_state); };
        var layer = new ViewLayer(view, _w.Viewport, firstLayer: _current is null);
"@

Write-Host "`nDone. Now build and publish." -ForegroundColor Cyan
