#!/usr/bin/env bash
#
# Packs ItamiTimer into a macOS .app and installs it to ~/Applications/.
# The macOS counterpart of the Windows side's:
#     dotnet publish src/ItamiTimer.App -c Release -r win-x64 --self-contained false \
#         -o "$LOCALAPPDATA/Programs/ItamiTimer"
#
# Both sides land around 27MB: dotnet publish doesn't strip debug symbols on its own
# (127MB as-is), and the csproj's StripPdbFromPublish removes them for both platforms.
#
# ⚠️ **Packaging isn't a nice-to-have, it's part of correctness.**
#
# The default rules.json matches an "ItamiTimer" goal against the app name ActivityWatch
# reports, so the app itself can be recognized as on-task. macOS's aw-watcher-window
# reports the **foreground app's display name**, which comes from the bundle's
# CFBundleName -- a bare process with no bundle has no controllable name to report at all.
# If that match fails, the symptom is: you look at your own window, the program judges you
# OffTask, and the cell turns red.
#
# So CFBundleName must be **pinned to ItamiTimer**, the same rule and the same reasoning as
# pinning AssemblyName on the Windows side.
#
# Usage:  ./pack-macos.sh            installs to ~/Applications/ItamiTimer.app
#         ./pack-macos.sh <dir>      installs to the given directory
#         ./pack-macos.sh --dmg     also builds dist/ItamiTimer-<version>-macOS-arm64.dmg,
#                                    for handing to other people (not notarized, see the
#                                    Read Me it carries -- Apple Silicon only, framework-
#                                    dependent: the .NET 10 Runtime must already be installed)
set -euo pipefail

cd "$(dirname "$0")"

DMG=0
DEST_DIR="$HOME/Applications"
for arg in "$@"; do
    case "$arg" in
        --dmg) DMG=1 ;;
        *) DEST_DIR="$arg" ;;
    esac
done
APP="$DEST_DIR/ItamiTimer.app"
STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT

# Single source of truth for the version: Directory.Build.props, same value the app itself
# reads at runtime (see SettingsWindow.axaml.cs). Bump it in exactly one place.
VERSION="$(grep -oE '<Version>[^<]+' Directory.Build.props | sed 's/<Version>//')"

# The RID must be specified. Without -r, Skia/HarfBuzz's native libraries and debug symbols
# for **every platform** get pulled in -- measured on the Windows side going from 27MB to
# 560MB, with the three platforms' libSkiaSharp.pdb alone accounting for 244MB.
RID="osx-$(uname -m | sed 's/^x86_64$/x64/; s/^arm64$/arm64/')"

echo "==> publish (${RID}, framework-dependent)"
dotnet publish src/ItamiTimer.App -c Release -r "$RID" --self-contained false \
    -o "$STAGE/publish" --nologo -v quiet

# .pdb files are automatically deleted by the csproj's StripPdbFromPublish, on
# AfterTargets="Publish". macOS's native packages don't ship a .dSYM either, so there's
# nothing else to clean up here.
echo "    $(du -sh "$STAGE/publish" | cut -f1)"

echo "==> drawing the icon (drawn in code, no bitmap in the repository)"
"$STAGE/publish/ItamiTimer" --export-iconset "$STAGE/ItamiTimer.iconset" >/dev/null
iconutil -c icns "$STAGE/ItamiTimer.iconset" -o "$STAGE/ItamiTimer.icns"

echo "==> assembling the bundle"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$STAGE/publish/." "$APP/Contents/MacOS/"
cp "$STAGE/ItamiTimer.icns" "$APP/Contents/Resources/"

# The framework-dependent apphost needs to find the .NET runtime, and **a GUI app launched
# by double-click doesn't inherit the shell's environment variables** -- when .NET is
# installed somewhere other than /usr/local/share/dotnet (say, installed to ~/.dotnet
# without a password prompt), opening it from Finder just says "You must install .NET to
# run this application", while the very same binary runs fine from a terminal. This
# difference is deceptive enough to be worth writing down specifically.
#
# The fix is LSEnvironment: LaunchServices injects it at launch time. The path is fixed at
# **the moment of packaging**, so this script needs to run again after moving the .NET install.
if [ -n "${DOTNET_ROOT:-}" ]; then
    DOTNET_DIR="$DOTNET_ROOT"
else
    DOTNET_BIN="$(command -v dotnet)"
    # A brew-installed dotnet is a symlink; follow it to find the real root
    DOTNET_DIR="$(dirname "$(readlink "$DOTNET_BIN" 2>/dev/null || echo "$DOTNET_BIN")")"
fi
LS_ENV=""
if [ "$DOTNET_DIR" != "/usr/local/share/dotnet" ] && [ -d "$DOTNET_DIR" ]; then
    echo "==> .NET isn't in the default location (${DOTNET_DIR}), writing it into LSEnvironment"
    LS_ENV="    <key>LSEnvironment</key>
    <dict>
        <key>DOTNET_ROOT</key><string>$DOTNET_DIR</string>
    </dict>"
fi

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
$LS_ENV
    <!-- ⚠️ CFBundleName is pinned to ItamiTimer: it's exactly what aw-watcher-window
         reports, and the default rules.json's "ItamiTimer" goal is matched against it so
         the app can recognize itself as on-task. Changing this causes a feedback loop. -->
    <key>CFBundleName</key>              <string>ItamiTimer</string>
    <key>CFBundleDisplayName</key>       <string>ItamiTimer</string>
    <key>CFBundleExecutable</key>        <string>ItamiTimer</string>
    <key>CFBundleIdentifier</key>        <string>net.achilles.itamitimer</string>
    <key>CFBundlePackageType</key>       <string>APPL</string>
    <key>CFBundleIconFile</key>          <string>ItamiTimer.icns</string>
    <key>CFBundleShortVersionString</key><string>$VERSION</string>
    <key>CFBundleVersion</key>           <string>$VERSION</string>
    <key>LSMinimumSystemVersion</key>    <string>12.0</string>
    <!-- The dial is drawn as vectors and must render at physical-pixel resolution, otherwise it blurs on Retina displays -->
    <key>NSHighResolutionCapable</key>   <true/>
</dict>
</plist>
PLIST

# An unsigned bundle gets blocked by Gatekeeper when double-clicked from Finder. Since this
# is built and run on the same machine, an ad-hoc signature applied in place avoids having
# to right-click "Open" every time.
echo "==> ad-hoc signing"
codesign --force --deep --sign - "$APP" 2>/dev/null || echo "   (signing failed; right-click -> Open the first time)"

echo
echo "Installed: $APP"
echo "Run:       open -a \"$APP\""

if [ "$DMG" = "1" ]; then
    echo
    echo "==> building .dmg"
    DMG_STAGE="$(mktemp -d)"
    trap 'rm -rf "$STAGE" "$DMG_STAGE"' EXIT
    cp -R "$APP" "$DMG_STAGE/"
    ln -s /Applications "$DMG_STAGE/Applications"

    # The only thing an end user needs to know that Finder can't tell them itself:
    # this build isn't notarized, and it's framework-dependent (needs the .NET 10
    # Runtime already on the machine). Both are one-time, first-run problems.
    cat > "$DMG_STAGE/Read Me.txt" <<NOTE
ItamiTimer $VERSION for macOS (Apple Silicon)

Requires the .NET 10 Runtime (not the SDK):
https://dotnet.microsoft.com/download/dotnet/10.0

This build isn't notarized by Apple. The first time you open it, Gatekeeper
will say it's from an unidentified developer -- right-click (or Control-click)
ItamiTimer.app and choose "Open", then confirm once. After that it opens
normally, including by double-click.
NOTE

    mkdir -p dist
    DMG_PATH="dist/ItamiTimer-$VERSION-macOS-arm64.dmg"
    rm -f "$DMG_PATH"
    hdiutil create -volname "ItamiTimer" -srcfolder "$DMG_STAGE" -ov -format UDZO -quiet "$DMG_PATH"
    echo "Release image: $DMG_PATH"
fi
