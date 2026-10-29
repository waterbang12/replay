<#
.SYNOPSIS
  Builds, tests and publishes LifeReplay for Windows into dist\LifeReplay.

.EXAMPLE
  .\build.ps1                       # test + publish, .NET runtime bundled (runs on any Windows 10 2004+/11 PC)
  .\build.ps1 -FrameworkDependent   # smaller; needs the .NET 10 Desktop + ASP.NET Core runtimes installed
  .\build.ps1 -SkipTests
#>
param(
    [switch]$FrameworkDependent,
    [switch]$SkipTests,
    [string]$Configuration = "Release"
)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$dist = Join-Path $root "dist\LifeReplay"

# Find a .NET 10 SDK: PATH first, then the per-user install location used by dotnet-install.ps1.
$dotnet = $null
foreach ($candidate in @((Get-Command dotnet -ErrorAction SilentlyContinue).Source, "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe", "$env:ProgramFiles\dotnet\dotnet.exe")) {
    if ($candidate -and (Test-Path $candidate)) {
        $sdks = & $candidate --list-sdks 2>$null
        if ($sdks -match '^10\.') { $dotnet = $candidate; break }
    }
}
if (-not $dotnet) {
    Write-Host "No .NET 10 SDK found. Install it with one of:" -ForegroundColor Yellow
    Write-Host "  winget install Microsoft.DotNet.SDK.10"
    Write-Host "  or: powershell -c `"& { iwr https://dot.net/v1/dotnet-install.ps1 -OutFile dotnet-install.ps1; .\dotnet-install.ps1 -Channel 10.0 }`""
    exit 1
}
$env:DOTNET_ROOT = Split-Path $dotnet
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"
Write-Host "Using $dotnet"

$sln = Join-Path $root "desktop\LifeReplay.slnx"
if (-not $SkipTests) {
    Write-Host "`n== Tests" -ForegroundColor Cyan
    & $dotnet test (Join-Path $root "desktop\tests\LifeReplay.Tests") -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw "Tests failed" }
}

Write-Host "`n== Publish to $dist" -ForegroundColor Cyan
# Empty the folder rather than deleting it: an Explorer window or terminal open in it would block deletion.
if (Test-Path $dist) {
    if (Get-Process LifeReplay, LifeReplay.Viewer -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$dist\*" }) {
        throw "LifeReplay is running from $dist. Exit it (tray > Exit, or: $dist\LifeReplay.exe --shutdown) and run the build again."
    }
    Get-ChildItem $dist -Force | Remove-Item -Recurse -Force
}
$common = @("-c", $Configuration, "-o", $dist, "-r", "win-x64", "-p:DebugType=embedded")
if ($FrameworkDependent) { $common += @("--self-contained", "false") } else { $common += @("--self-contained", "true") }
foreach ($proj in @("desktop\src\LifeReplay.Recorder", "desktop\src\LifeReplay.Viewer")) {
    & $dotnet publish (Join-Path $root $proj) @common
    if ($LASTEXITCODE -ne 0) { throw "Publish of $proj failed" }
}
Write-Host "`nDone. Start the recorder with:  $dist\LifeReplay.exe" -ForegroundColor Green
Write-Host "Open the replay viewer with:    $dist\LifeReplay.Viewer.exe"
