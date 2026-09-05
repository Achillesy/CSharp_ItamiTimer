#!/usr/bin/env bash
#
# Packs ItamiTimer into dist/ItamiTimer-<version>-macOS-arm64.dmg.
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
# Usage:  ./pack-macos.sh [--dmg]
#
# ⚠️ **This script produces the release image and nothing else.** It used to also install
# the .app to ~/Applications (or a directory given as $1), which made it double as "put a
# runnable build somewhere for me to poke at" -- that role is gone (CLAUDE.md: local
# testing runs the bin/Debug or bin/Release output right inside the project directory; an
# installed copy sitting beside the one from the .dmg only creates "which one am I
# actually running?"). The bundle is now assembled in a temp dir and thrown away with it.
#
# --dmg is accepted and ignored: it's what CLAUDE.md and muscle memory both say, and the
# .dmg is the only thing this script makes either way.
#
# The image is not notarized and is framework-dependent (the .NET 10 Runtime must already
# be installed) -- see the Read Me it carries. Apple Silicon only.
set -euo pipefail

cd "$(dirname "$0")"

for arg in "$@"; do
    case "$arg" in
        --dmg) ;;   # accepted and ignored, see the note above
        *) echo "usage: $0 [--dmg]   (builds dist/ItamiTimer-<version>-macOS-arm64.dmg)" >&2; exit 2 ;;
    esac
done

STAGE="$(mktemp -d)"
DMG_STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE" "$DMG_STAGE"' EXIT

# The bundle is built straight into the .dmg staging dir -- nothing is installed anywhere.
APP="$DMG_STAGE/ItamiTimer.app"

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

# CLI 也要装（2026-08-09，DECISIONS L22）。**2.2.4 之后理由换了一个，但结论没变**：
# App 到点时不再借道 CLI（两个平台都改成自己直接跑，L28），所以少了它闹钟照样会响——
# 但 `itami commands` 是**选命令和试命令的唯一入口**，也是"命令看着没反应"时唯一能看到
# 真实报错的地方（README 的诊断指引就指向它）。装机的机器上没有它，用户就只能手改
# rules.json、且没有任何试跑手段。
# 发到**同一个 publish 目录**：两边共用 ItamiTimer.Core.dll 等程序集（同一次 Release
# 构建，内容一致），各自的 .deps.json / .runtimeconfig.json 按程序集名区分，不打架。
echo "==> publish itami CLI (same folder)"
dotnet publish src/ItamiTimer.Cli -c Release -r "$RID" --self-contained false \
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
echo "==> building .dmg"
ln -s /Applications "$DMG_STAGE/Applications"

# What the .dmg carries besides the app: the things Finder can't tell the user itself --
# this build isn't notarized, it's framework-dependent (needs the .NET 10 Runtime already
# on the machine), the itami CLI exists, and alarms.cron is hand-written.
# ⚠️ This heredoc is **one of the four user-facing documents** (CLAUDE.md): README.md,
# README_ZH.md, installer/README.txt, and this. It has been missed once (DECISIONS L30).
cat > "$DMG_STAGE/Read Me.txt" <<NOTE
ItamiTimer $VERSION for macOS (Apple Silicon)

Requires the .NET 10 Runtime (not the SDK):
https://dotnet.microsoft.com/download/dotnet/10.0

This build isn't notarized by Apple. The first time you open it, Gatekeeper
will say it's from an unidentified developer -- right-click (or Control-click)
ItamiTimer.app and choose "Open", then confirm once. After that it opens
normally, including by double-click.


The itami command line tool
===========================

Inside the app bundle there is a second program:

  ItamiTimer.app/Contents/MacOS/itami

You never have to use it. It exists for one job the window deliberately has no
buttons for: choosing, and testing, the command the alarm runs.

rules.json (in ~/Library/Application Support/ItamiTimer/) can hold a shortlist
of shell commands under "executeCommand". When the Execute switch in Settings
is on and the alarm goes off, ItamiTimer runs THE FIRST ONE -- always #0, never
any other. The list is a shortlist, not a menu: to change which command is
armed, you move one to the top.

In Terminal, with the app in /Applications:

  ITAMI=/Applications/ItamiTimer.app/Contents/MacOS/itami

  \$ITAMI commands --list
        Print the list. The * marks #0 -- the one the alarm will run.
        Changes nothing.

  \$ITAMI commands --select 3
        Move entry 3 to the top, so it becomes #0.
        Rewrites rules.json and keeps a .bak copy next to it. Your comments and
        indentation survive exactly as you wrote them.
        A running ItamiTimer picks this up on its own -- no restart needed.

  \$ITAMI commands --execute
        Run #0 right now, so you can see whether it actually works without
        waiting for an alarm. Asks y/N first, because that list usually has
        "shut down" and "restart" in it.

Anything else -- a misspelled switch, --select with no number, a number that
isn't in the list -- just prints the list and changes nothing. Only those exact
forms do anything, so a typo can never run or rewrite something by accident.


What happens when the alarm fires
---------------------------------

The alarm runs your command and returns immediately -- it never waits, so a
command that hangs can't stall the clock. No window opens. Everything the
command reports -- exit code, output, errors -- goes to itami.log.

Most of the default macOS commands drive System Events (restart, sleep, log
out, shut down). The first time one runs, macOS asks whether to allow it --
System Settings > Privacy & Security > Automation. Until it is allowed, those
commands fail with "Not authorized to send Apple events (-1743)", and itami.log
is where you will see that.

Note that the request comes from ItamiTimer itself, which is not in the
Automation list until it asks for the first time. If an alarm fires while you
are away from the keyboard, the permission dialog may simply be sitting there
unanswered -- running "\$ITAMI commands --execute" once from Terminal gets it
asked and answered while you are actually there.


Recurring reminders (alarms.cron)
=================================

Separate from the alarm hand on the dial, ItamiTimer watches a standard crontab
for the things that come back. Create alarms.cron in the folder below, by hand:

  # m    h     dom mon dow    reminder
    0    14    *   *   *      Take medication
    30   21    *   *   1-5    Evening check-in
  # 0    7     *   *   *      Commented out, stays quiet
    @daily                    Daily review

The five time fields follow crontab(5) exactly, including the classic gotcha:
when neither the day-of-month nor the day-of-week field starts with *, they are
OR'd, so "0 0 1 * MON" fires on the 1st OR on Mondays. Column 6 is the reminder
text -- it is NEVER executed. This file cannot run anything.

Nothing is validated. A line that doesn't parse is skipped in silence. The one
signal you get is the opposite one: every reminder that DOES fire writes a line
to itami.log with the expression that matched. If a reminder never arrives,
look there -- no line means the rule never matched, so you mistyped it.

The program only reads this file; it never writes it and never creates it. To
silence a line, comment it out with #. Reminders missed while the program was
closed are skipped, never caught up.

UPGRADING FROM 3.6.x: this replaces the old alarms.md checklist, which is no
longer read at all -- not migrated, not warned about. Move anything you still
want into alarms.cron by hand. A genuinely one-off reminder has no crontab
equivalent; use the dial's alarm hand for that.


Where things live
-----------------

  ~/Library/Application Support/ItamiTimer/rules.json      goals + executeCommand
  ~/Library/Application Support/ItamiTimer/alarms.cron     recurring reminders (you write it)
  ~/Library/Application Support/ItamiTimer/settings.json   sounds, switches, theme, position
  ~/Library/Application Support/ItamiTimer/during.json     accumulated focus time per goal
  ~/Library/Application Support/ItamiTimer/layout.json     optional: {"layout": "compact"} (you write it)
  ~/Library/Application Support/ItamiTimer/itami.log       what happened, and why

A smaller window
----------------

The window is tall. Write layout.json in the folder above:

    {
      // "standard" or "compact"
      "layout": "compact"
    }

and the dial and the dominoes shrink to about three quarters, with the window
narrowed to match. Controls and text keep their size -- only the drawing gets
smaller. Comments and trailing commas are fine, the same as rules.json.

It is read once at startup: edit it while ItamiTimer is running and nothing happens
until you launch it again. Delete the file to go back to the normal size.


The window itself never explains anything and never shows a report -- that is
deliberate. itami.log is where you look afterwards.
NOTE

mkdir -p dist
DMG_PATH="dist/ItamiTimer-$VERSION-macOS-arm64.dmg"
rm -f "$DMG_PATH"
hdiutil create -volname "ItamiTimer" -srcfolder "$DMG_STAGE" -ov -format UDZO -quiet "$DMG_PATH"
echo "Release image: $DMG_PATH"
