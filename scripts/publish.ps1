# Produces a self-contained, single-folder build of NEXUS in .\publish (no .NET install needed on the target PC).
param(
    [ValidateSet("win-x64", "win-arm64")] [string] $Runtime = "win-x64",
    [string] $Configuration = "Release"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
dotnet publish "$root\src\Nexus.App\Nexus.App.csproj" `
    -c $Configuration -r $Runtime --self-contained true `
    -p:PublishReadyToRun=true `
    -o "$root\publish\$Runtime"
Write-Host "Published to $root\publish\$Runtime\Nexus.exe" -ForegroundColor Green
