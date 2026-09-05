# CLAUDE.md

本文件为在此仓库中工作的 Claude Code 提供指引。

## 项目是什么

ItamiTimer（一袋米要扛几楼）：桌面端带强制约束的专注计时器（Windows + macOS，
Avalonia 12 / .NET 10）。勾选允许的小目标后提交任务，程序拿开始时刻查 ActivityWatch
的事件历史重放整段——只有命中规则且人在座的时间计入。偷懒不弹窗不出声，只有表盘上
的红格和越滑越远的截止弧。**没有退化模式**——AW 不可用由判定模型自己吸收（DESIGN §3.1）。

**功能已完整，真机验证过多轮。当前工作模式是改进和修 bug**，不是从设计推进实现。

## 三份文档，各司其职

| 文件 | 内容 | 改代码前 |
|---|---|---|
| [`DESIGN.md`](./DESIGN.md) | 当前系统设计（按主题组织）：判定模型、重放算法、时间模型、表盘规格、闹钟、跨平台 | **必读相关章节** |
| [`DECISIONS.md`](./DECISIONS.md) | 护栏清单：被推翻的方案、知情接受的代价、「不要翻案」 | **动手前先查**——你觉得「显然可以改进」的地方多半在这里有一条 |
| [`README.md`](./README.md) | 英文对外介绍 | 用户可见的行为变了要同步——**面向用户的说明书是四份**：`README.md`、`README_ZH.md`、`installer/README.txt`、**外加 `pack-macos.sh` 里那段进 .dmg 的 heredoc**（漏过一次，DECISIONS L30） |

**这个项目两天里推翻过自己很多次，全部有案可查。** 想改一条现有行为时：先查
DECISIONS.md 有没有这条；有，就先跟用户确认再动；没有，也要在改完后把新决策补进去。

## 硬性约束（违反即事故）

- `App.axaml` 的 `Name="ItamiTimer"`、csproj 的 `AssemblyName=ItamiTimer` **永不改**
  ——自身豁免的依据，改了不报错，只是安静把账算错（DECISIONS F1/F2）。
- Core 保持 `net10.0`、零 UI 引用；App 的 TFM 也是 `net10.0`，`-windows` 别加回去。
- 不落盘任务状态；不做缓存；不自动降级模式；不自动开始下一轮。
- **UI 是 Avalonia，不是 WPF / WinForms / Win32。** API 名字像，语义未必一样，而且
  这类错**不报错**，只是安静失效。搬任何「WPF 那边是这么写的」之前先查 Avalonia 的
  实际语义。已经栽过一次：滚轮 `PointerWheelEventArgs.Delta.Y` 在 Avalonia 里
  **一格就是 1.0**，不是 Win32 `WM_MOUSEWHEEL` 的 `WHEEL_DELTA = 120`——照搬那个
  120 去做除法，加速档位整整两天一次都没生效过（DECISIONS H12）。
  同理：别引 `System.Windows.*`、别用只有 Windows 装机自带的字体图标（D5）、
  平台调用一律收口在 `App/Platform/` 的单个文件里。
- 仓库不放位图和音频；界面文字英文（窗口标题中文是产品名）；`rules.json` 是用户数据
  不翻译。
- **CLI 跑的必须是 App 跑的那份代码**：`Command.cs` / `Log.cs` / `AppData.cs` /
  `Settings.cs` / `During.cs` 由 Cli 的 csproj 用 `<Compile Include>` **link** 进去，
  不是抄一份。抄一份 = CLI 测过了 App 照样能坏（DECISIONS L5），这个工具就没意义了。
  ⚠️ CLI 对 `during.json` **只读**：推进 checkpoint 是界面点 Start 那一刻唯一的写入点
  （DESIGN §11.2）。

## ⚠️ 平台验证进度：两边各过了一轮，各自还欠一些

**逐条状态和依据见 DESIGN §13.1 那两张表**，这里只留结论：

| | 已程序化实测 | 只人眼简测 / 没验 |
|---|---|---|
| macOS 无边框透明窗口（§8.7） | `WorkingArea` 扣除、位置记忆、坐标原点、置顶 | 透明观感、拖动手感（人眼）；命中范围、右键菜单（没验） |
| Windows 分钟序列（§9.2） | 编译、170 个单测、`itami commands --list` | `shutdown /s /t 0` 的 `await` 行为、winmm 截断顺序（前者不打算测——那条命令真会关机） |
| macOS 闹钟执行（§9.3） | 两条执行路的输出收集、`Preview` 与执行同源、卡片顺序 | 测法是临时探针直接调 `LaunchDetached`，**没走 `OnMinute` 第 ④ 步、没开过真实窗口** |

**执行命令的当前形态**（DESIGN §9.3、DECISIONS L26/L29）：两个平台都是
`Command.LaunchDetached` 直接跑、不开任何窗口、输出收进 `itami.log`。三条不许动的：

- ⚠️ **`BuildShell` 里 `CreateNoWindow = redirect`，两个值都是实测定的，别改成常量**：
  App 那条路（重定向）不设它，Windows 会给子进程新建控制台窗口、黑窗回来；CLI 那条路
  （不重定向）设了它**子进程输出会整个消失**（实测退出码收得到、一个字都没有）。
- ⚠️ **知情代价**：`shutdown /h`「休眠未启用」是唯一一条既不吐字节、退出码还是 0 的
  失败分支，日志里只剩 `exited with 0`。**诊断手段是终端里跑 `itami commands --execute`**
  ——有真控制台就看得见。这也是 `commands` 子命令承重的原因，别砍它。
- ⚠️ **macOS**：Apple 事件授权记在 ItamiTimer 头上而它不在自动化列表里，
  `osascript ... System Events` 那几条第一次会弹授权框。

**分钟序列的顺序是定死的**：① 提示条到期收起 ② AW 查询 + 判定 + 三声通知
③ Alarms 清单 ④ 闹钟判断 + 执行命令/响铃 ⑤ 骨牌核对星期（DESIGN §9.2、DECISIONS L13）。

**两边都别把编译通过当验证通过**——这个项目正是在「照文档推断 → 编译通过 → 功能
安静失效」上栽过好几次（H12 的滚轮 `Delta/120`、L1 的命令引号）。

## 构建 / 运行

```bash
dotnet build ItamiTimer.slnx
dotnet test ItamiTimer.slnx
```

**本地调试验证就在项目目录内跑 Debug 或 Release 的编译产物**（`bin/Debug`、
`bin/Release`，或直接 IDE 里跑）——**不要**执行 `dotnet publish` 把东西发到项目外部
（比如 `%LOCALAPPDATA%\Programs\ItamiTimer`）去测试。`rules.json` 已经通过 csproj
的 `Content Include`（`CopyToOutputDirectory`）跟着编译产物走，`bin/` 下的产物本身就能
独立跑起来。

**对外发布只有一条路**：`dist/` 目录里的成品——`./pack-macos.sh` 产出的 `.dmg`，
`.\pack-windows.ps1` 产出的 `ItamiTimer-<版本>-win-x64.exe`。这两个脚本**只负责产出
这一份最终安装包**，不再兼任"发一份能跑的本地测试版"这个角色。3.7.0 起
`pack-macos.sh` **不再往 `~/Applications` 装任何东西**（用户 2026-09-03 要求删掉这个
行为）：bundle 在临时目录里组装、跟临时目录一起扔掉，只留 `dist/` 那个 `.dmg`。
`--dmg` 仍然收但被忽略，因为它已经是唯一的产物。

Windows 安装包装机不需要预装 .NET——`installer/ItamiTimer.iss` 会检测、提示、下载官方
运行时安装器。依赖 Inno Setup 6 的 `ISCC.exe`（`winget install --id JRSoftware.InnoSetup
-e`，只是构建工具）。两个脚本的版本号都读同一处 `Directory.Build.props`。详见
DESIGN.md §14。

调试出口（headless，不开窗口）：`--dial-specimens <目录>` / `--export-icon <路径>` /
`--export-iconset <目录>`。

## 布局与惯例

```
src/ItamiTimer.Core/   判定与重放（纯函数为主）   ← 逻辑改动优先落这里，可测
src/ItamiTimer.Cli/    itami 命令行，三个子命令，跟界面共用 Core 的镜像和判定
                       start（验**引擎**：真实数据干跑，不验会话——没有休息、没有
                       键鼠空闲、没有休息起点投影）、backfill（干跑累计时长，只读）、
                       commands（选/试 executeCommand，**唯一入口**，也是 `shutdown /h`
                       那类静默失败唯一的诊断手段，见 L29）
src/ItamiTimer.App/    Avalonia 界面              ← 平台差异每处收口在单个文件
tests/                 xUnit，测试名是中文句子，直接陈述行为
```

- git：远端 `github.com/Achillesy/CSharp_ItamiTimer.git`，默认分支 **`master`**。
  仓库现为 private；对外发布时用户会另建仓库拷贝内容。
- 源码注释、提交信息、开发文档用中文；日志、异常、界面文字用英文。
- 运行时数据在 `%LOCALAPPDATA%\ItamiTimer\`（settings.json / rules.json / itami.log）。
  Release 也写日志——界面全程沉默，日志是唯一能事后查的地方。
- 姊妹项目 ActivityWatchJournal（Python）是**纯参考**：搬结论不搬代码、不跨进程调用。
