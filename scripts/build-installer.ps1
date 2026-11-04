# Builds the NEXUS installer: publishes a self-contained build, then compiles installer\nexus.iss with Inno Setup.
# Output: installer\Output\NEXUS-Setup-<version>.exe
param(
    [string] $Version,        # defaults to <Version> in Directory.Build.props
    [switch] $SkipPublish     # reuse an existing publish\win-x64 folder
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

if (-not $Version) {
    [xml] $props = Get-Content "$root\Directory.Build.props"
    $Version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like 1.2.3 (got '$Version')." }

if (-not $SkipPublish) {
    & "$root\scripts\publish.ps1" -Runtime win-x64 -Configuration Release
    if ($LASTEXITCODE -ne 0) { throw "Publishing NEXUS failed." }
}
if (-not (Test-Path "$root\publish\win-x64\Nexus.exe")) { throw "No published build found in publish\win-x64." }

# Inno Setup's compiler: an explicit ISCC variable, then PATH, then the usual install folders.
$iscc = @(
    $env:ISCC,
    (Get-Command iscc.exe -ErrorAction SilentlyContinue).Source,
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 was not found. Install it with: winget install JRSoftware.InnoSetup" }

& $iscc "/DAppVersion=$Version" "/DPublishDir=$root\publish\win-x64" "/O$root\installer\Output" "$root\installer\nexus.iss"
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed (exit code $LASTEXITCODE)." }

$setup = "$root\installer\Output\NEXUS-Setup-$Version.exe"
Write-Host ("Installer: {0} ({1:N1} MB)" -f $setup, ((Get-Item $setup).Length / 1MB)) -ForegroundColor Green
