# Packs ItamiTimer into a Windows installer at dist\ItamiTimer-<version>-win-x64.exe.
# The Windows counterpart of the macOS side's ./pack-macos.sh --dmg.
#
# Unlike the macOS .dmg (which just tells the user in a Read Me to go install the
# .NET Runtime themselves), this installer actively checks for the .NET Desktop
# Runtime at install time and offers to download + run the official installer if
# it's missing -- see installer\ItamiTimer.iss's [Code] section.
#
# Requires Inno Setup 6's ISCC.exe as a build-time tool (not shipped to end users):
#     winget install --id JRSoftware.InnoSetup -e
#
# Usage:  .\pack-windows.ps1              builds dist\ItamiTimer-<version>-win-x64.exe

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

# Single source of truth for the version: Directory.Build.props, the same value
# pack-macos.sh and the app itself (SettingsWindow.axaml.cs) read. Bump it in
# exactly one place.
$propsContent = Get-Content "Directory.Build.props" -Raw
if ($propsContent -notmatch "<Version>([^<]+)</Version>") {
    throw "Could not read <Version> from Directory.Build.props"
}
$Version = $Matches[1]

$StageDir = Join-Path $env:TEMP "ItamiTimer-pack-windows-$([guid]::NewGuid())"
New-Item -ItemType Directory -Path $StageDir | Out-Null
try {
    Write-Host "==> publish (win-x64, framework-dependent)"
    dotnet publish src\ItamiTimer.App -c Release -r win-x64 --self-contained false -o $StageDir --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

    $candidates = @(
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    )
    $iscc = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $iscc) {
        throw "ISCC.exe not found. Install Inno Setup 6 first: winget install --id JRSoftware.InnoSetup -e"
    }

    New-Item -ItemType Directory -Force -Path "dist" | Out-Null

    Write-Host "==> compiling installer (version $Version)"
    & $iscc "/DMyAppVersion=$Version" "/DStageDir=$StageDir" "installer\ItamiTimer.iss"
    if ($LASTEXITCODE -ne 0) { throw "ISCC compile failed" }
}
finally {
    Remove-Item $StageDir -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "Installer image: dist\ItamiTimer-$Version-win-x64.exe"
