# NEXUS fix: (1) stop the fake "Blinding Lights" preview from appearing when you change themes,
# (2) repair garbled characters (like "a€"" instead of a dash) left by earlier patch scripts.
# Run from the folder that contains Nexus.sln. This script is pure ASCII on purpose.
$ErrorActionPreference = "Stop"
if (-not (Test-Path ".\Nexus.sln")) { throw "Run this from the folder that contains Nexus.sln." }
$utf8 = New-Object System.Text.UTF8Encoding($false)

# ---- 1. Repair double-encoded text ------------------------------------------------------------
# Windows PowerShell 5.1 read UTF-8 files as ANSI and saved them back as UTF-8, turning e.g. an
# em dash into three junk characters. Reverse it: map each char back to its ANSI byte, decode as UTF-8.
$cp1252 = @{ 0x20AC=0x80; 0x201A=0x82; 0x0192=0x83; 0x201E=0x84; 0x2026=0x85; 0x2020=0x86; 0x2021=0x87;
             0x02C6=0x88; 0x2030=0x89; 0x0160=0x8A; 0x2039=0x8B; 0x0152=0x8C; 0x017D=0x8E; 0x2018=0x91;
             0x2019=0x92; 0x201C=0x93; 0x201D=0x94; 0x2022=0x95; 0x2013=0x96; 0x2014=0x97; 0x02DC=0x98;
             0x2122=0x99; 0x0161=0x9A; 0x203A=0x9B; 0x0153=0x9C; 0x017E=0x9E; 0x0178=0x9F }
$markers = @("$([char]0xE2)$([char]0x20AC)", "$([char]0xC2)$([char]0xB7)", "$([char]0xC3)$([char]0x2014)", "$([char]0xC2)$([char]0xA0)")
$strict = New-Object System.Text.UTF8Encoding($false, $true)

Get-ChildItem src -Recurse -Include *.cs, *.csproj | ForEach-Object {
    $text = [IO.File]::ReadAllText($_.FullName, $utf8)
    if (-not ($markers | Where-Object { $text.Contains($_) })) { return }
    $bytes = New-Object byte[] $text.Length
    for ($i = 0; $i -lt $text.Length; $i++) {
        $c = [int]$text[$i]
        if ($c -eq 0xFEFF -and $i -eq 0) { $bytes[$i] = 0x20; continue }
        if ($c -lt 256) { $bytes[$i] = [byte]$c }
        elseif ($cp1252.ContainsKey($c)) { $bytes[$i] = [byte]$cp1252[$c] }
        else { Write-Host "  skipped (mixed encoding): $($_.Name)" -ForegroundColor Yellow; return }
    }
    try { $fixed = $strict.GetString($bytes).TrimStart() } catch { Write-Host "  skipped (not repairable): $($_.Name)" -ForegroundColor Yellow; return }
    [IO.File]::WriteAllText($_.FullName, $fixed, $utf8)
    Write-Host "  repaired text: $($_.Name)" -ForegroundColor Green
}

# ---- 2. Preview fixes (read/write as UTF-8 so nothing gets garbled again) ----------------------
function Patch($file, $find, $replace) {
    $text = [IO.File]::ReadAllText((Resolve-Path $file), $utf8)
    if (-not $text.Contains($find)) { Write-Host "  already done / not found: $file" -ForegroundColor DarkGray; return }
    [IO.File]::WriteAllText((Resolve-Path $file), $text.Replace($find, $replace), $utf8)
    Write-Host "  patched: $file" -ForegroundColor Green
}

$f = "src\Nexus.App\SettingsUi\SettingsWindow.cs"
# Picking a theme no longer injects a fake song.
Patch $f '_preview.Run("Now playing");' ''
Patch $f ' Picking a theme plays a preview on the island.' ' Use Preview lab to try it with sample events.'

# Preview lab songs are clearly labelled and leave after 6 seconds instead of 12.
$f = "src\Nexus.App\Composition\PreviewEvents.cs"
Patch $f '"After Hours", "Spotify", MediaPlayback.Playing' '"After Hours", "NEXUS Preview lab", MediaPlayback.Playing'
Patch $f 'Duration = TimeSpan.FromSeconds(12), Accent = red,' 'Duration = TimeSpan.FromSeconds(6), Accent = red,'

Write-Host "`nDone. Exit NEXUS, then build and publish." -ForegroundColor Cyan
