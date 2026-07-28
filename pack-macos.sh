#!/usr/bin/env bash
#
# 把 ItamiTimer 打成一个 macOS 的 .app，装到 ~/Applications/。
# 对应 Windows 那边的：
#     dotnet publish src/ItamiTimer.App -c Release -r win-x64 --self-contained false \
#         -o "$LOCALAPPDATA/Programs/ItamiTimer"
#     find "$LOCALAPPDATA/Programs/ItamiTimer" -name '*.pdb' -delete
#
# 那第二行 find 不是可选的：dotnet publish 只把 NuGet 原生包照搬出来、不剔除调试
# 符号，Windows 那边实测原样输出 127MB，删掉 pdb 才是 27MB（差额就 libSkiaSharp.pdb
# 80MB + libHarfBuzzSharp.pdb 20MB 两个文件）。详见 DESIGN.md §8.3.8。
#
# 本脚本这边同理 —— 见下面 publish 之后那一步。
#
# ⚠️ **打包不是锦上添花，是正确性的一部分。**
#
# GroupRules 里那条硬编码的自身豁免（§5.3 第 1 步）靠 AW 上报的 app 名认出自己。
# macOS 的 aw-watcher-window 报的是**前台应用的显示名**，而那个名字来自 bundle 的
# CFBundleName —— 裸进程跑起来根本没有 bundle，报出来的名字不可控。豁免一旦失效，
# 症状正是文档反复警告的那种：你看自己的窗口，程序把你判成 OffTask，格子变红。
#
# 所以 CFBundleName 必须**钉死成 ItamiTimer**，跟 Windows 那边 AssemblyName 钉死
# 是同一条纪律、同一个理由。
#
# 用法：  ./pack-macos.sh            装到 ~/Applications/ItamiTimer.app
#         ./pack-macos.sh <目录>     装到指定目录
set -euo pipefail

cd "$(dirname "$0")"

DEST_DIR="${1:-$HOME/Applications}"
APP="$DEST_DIR/ItamiTimer.app"
STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT

# 必须限定 RID。不加 -r 会把 Skia / HarfBuzz **所有平台**的原生库和调试符号
# 一起塞进来 —— Windows 那边实测从 27MB 涨到 560MB，光三个平台的
# libSkiaSharp.pdb 就 244MB。
RID="osx-$(uname -m | sed 's/^x86_64$/x64/; s/^arm64$/arm64/')"

echo "==> publish（$RID，框架依赖）"
dotnet publish src/ItamiTimer.App -c Release -r "$RID" --self-contained false \
    -o "$STAGE/publish" --nologo -v quiet

# dotnet publish 只把 NuGet 原生包照搬出来，**不剔除调试符号**。Windows 那边实测
# 原样输出 127MB、删掉 pdb 才 27MB（差额就 libSkiaSharp.pdb 80MB +
# libHarfBuzzSharp.pdb 20MB 两个文件）。macOS 的原生包目前不带 .pdb，但 SkiaSharp
# 哪天改了打包方式就会跟着长——一条无害的清理胜过一个装进 .app 才发现的意外。
# 详见 DESIGN.md §8.3.8。
find "$STAGE/publish" \( -name '*.pdb' -o -name '*.dSYM' \) -exec rm -rf {} + 2>/dev/null || true
echo "    $(du -sh "$STAGE/publish" | cut -f1)"

echo "==> 画图标（代码画的，仓库里不放位图）"
"$STAGE/publish/ItamiTimer" --export-iconset "$STAGE/ItamiTimer.iconset" >/dev/null
iconutil -c icns "$STAGE/ItamiTimer.iconset" -o "$STAGE/ItamiTimer.icns"

echo "==> 组 bundle"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$STAGE/publish/." "$APP/Contents/MacOS/"
cp "$STAGE/ItamiTimer.icns" "$APP/Contents/Resources/"

# 框架依赖的 apphost 要能找到 .NET 运行时，而**双击启动的 GUI 应用不继承 shell 的
# 环境变量** —— .NET 装在 /usr/local/share/dotnet 之外（比如免密码装到 ~/.dotnet）
# 时，从 Finder 点开就是「You must install .NET to run this application」，
# 而在终端里跑同一个二进制却一切正常。这个差别很能骗人，专门记一笔。
#
# 解法是 LSEnvironment：LaunchServices 会在启动时把它注入进去。路径在**打包这一刻**
# 定死，所以之后挪动了 .NET 安装位置要重新跑一次本脚本。
if [ -n "${DOTNET_ROOT:-}" ]; then
    DOTNET_DIR="$DOTNET_ROOT"
else
    DOTNET_BIN="$(command -v dotnet)"
    # brew 装的 dotnet 是个符号链接，要跟过去才是真正的 root
    DOTNET_DIR="$(dirname "$(readlink "$DOTNET_BIN" 2>/dev/null || echo "$DOTNET_BIN")")"
fi
LS_ENV=""
if [ "$DOTNET_DIR" != "/usr/local/share/dotnet" ] && [ -d "$DOTNET_DIR" ]; then
    echo "==> .NET 不在默认位置（$DOTNET_DIR），写进 LSEnvironment"
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
    <!-- ⚠️ CFBundleName 钉死成 ItamiTimer：aw-watcher-window 上报的就是它，
         §5.3 第 1 步的自身豁免靠它认出自己。改了这里会陷入死循环。 -->
    <key>CFBundleName</key>              <string>ItamiTimer</string>
    <key>CFBundleDisplayName</key>       <string>ItamiTimer</string>
    <key>CFBundleExecutable</key>        <string>ItamiTimer</string>
    <key>CFBundleIdentifier</key>        <string>net.achilles.itamitimer</string>
    <key>CFBundlePackageType</key>       <string>APPL</string>
    <key>CFBundleIconFile</key>          <string>ItamiTimer.icns</string>
    <key>CFBundleShortVersionString</key><string>1.0</string>
    <key>CFBundleVersion</key>           <string>1</string>
    <key>LSMinimumSystemVersion</key>    <string>12.0</string>
    <!-- 表盘是矢量画的，必须按物理像素渲染，否则 Retina 上会糊 -->
    <key>NSHighResolutionCapable</key>   <true/>
</dict>
</plist>
PLIST

# 未签名的 bundle 从 Finder 双击会被 Gatekeeper 拦。本机自己编自己用，
# 就地做一个 ad-hoc 签名，省掉每次右键「打开」。
echo "==> ad-hoc 签名"
codesign --force --deep --sign - "$APP" 2>/dev/null || echo "   （签名失败，首次打开需右键 → 打开）"

echo
echo "装好了：$APP"
echo "运行：   open -a \"$APP\""
