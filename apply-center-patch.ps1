# Adds "Settings > Island Center" customization to NEXUS.
# Run from the NEXUS folder (the one containing Nexus.sln) AFTER extracting this patch over it.
$ErrorActionPreference = "Stop"
if (-not (Test-Path ".\Nexus.sln")) { throw "Run this from the folder that contains Nexus.sln (e.g. C:\Dev\Nexus)." }

function Patch($file, $marker, $find, $replace) {
    $text = Get-Content $file -Raw
    if ($text.Contains($marker)) { Write-Host "  already patched: $file" -ForegroundColor DarkGray; return }
    if (-not $text.Contains($find)) { throw "Could not find the expected code in $file. Send this message to Copilot." }
    Set-Content $file ($text.Replace($find, $replace)) -Encoding UTF8 -NoNewline
    Write-Host "  patched: $file" -ForegroundColor Green
}

# 1. Settings model: add the Center section and normalise it on load.
$f = "src\Nexus.Core\Settings\NexusSettings.cs"
Patch $f "CenterSettings Center" `
  "public ModuleSettings Modules { get; set; } = new();" `
  "public ModuleSettings Modules { get; set; } = new();`r`n    public CenterSettings Center { get; set; } = new();"
Patch $f "Center.Normalize()" `
  "Modules.Enabled ??= new(StringComparer.OrdinalIgnoreCase);" `
  "Modules.Enabled ??= new(StringComparer.OrdinalIgnoreCase);`r`n        Center ??= new();`r`n        Center.Normalize();"

# 2. Settings window: new "Island Center" page in the navigation.
$f = "src\Nexus.App\SettingsUi\SettingsWindow.cs"
Patch $f 'AddNav("center"' `
  'AddNav("position",' `
  ('AddNav("center", "\uE8A9", "Island Center");' + "`r`n        " + 'AddNav("position",')
Patch $f 'CenterSettingsPage.Build' `
  '"position" => PositionPage(),' `
  ('"center" => CenterSettingsPage.Build(_settings, () => Navigate("center")),' + "`r`n            " + '"position" => PositionPage(),')

# 3. Controller: re-render when Center settings change.
$f = "src\Nexus.App\Island\IslandController.cs"
Patch $f 'case "center":' `
  'case "appearance":' `
  ('case "appearance":' + "`r`n            " + 'case "center":')

Write-Host "`nDone. Now run: dotnet run --project src\Nexus.App" -ForegroundColor Cyan
