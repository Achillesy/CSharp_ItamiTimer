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

    # CLI 也要装（2026-08-09，DECISIONS L22）：闹钟到点时 App 起一个 shell 去跑
    # `itami.exe commands --execute --yes`，那个文件必须真的在安装目录里。在这之前
    # 这里只 publish 了 App，装机的机器上根本没有 itami.exe。
    # 发到**同一个 StageDir**：两边共用 ItamiTimer.Core.dll 等程序集（同一次 Release
    # 构建，内容一致），各自的 .deps.json / .runtimeconfig.json 按程序集名区分，不打架。
    Write-Host "==> publish itami CLI (same folder)"
    dotnet publish src\ItamiTimer.Cli -c Release -r win-x64 --self-contained false -o $StageDir --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish (cli) failed" }

    # 命令行工具不写文档等于不存在——D6"能猜的就让人猜"只管 GUI，不管 CLI（L22）。
    Copy-Item "installer\README.txt" -Destination $StageDir -Force

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
