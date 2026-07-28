#!/usr/bin/env bash
#
# 把 ItamiTimer 打成一个 macOS 的 .app，装到 ~/Applications/。
# 对应 Windows 那边的：
#     dotnet publish src/ItamiTimer.App -c Release -r win-x64 --self-contained false \
#         -o "$LOCALAPPDATA/Programs/ItamiTimer"
#
# 两边都是 27MB 上下：dotnet publish 自己不剔除调试符号（原样输出 127MB），
# csproj 里的 StripPdbFromPublish 替两个平台都删掉了。详见 DESIGN.md §8.3.8。
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

# .pdb 由 csproj 的 StripPdbFromPublish 在 AfterTargets="Publish" 上自动删掉了
# （DESIGN.md §8.3.8）。macOS 的原生包目前也不带 .dSYM，所以这里不用再清理。
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
