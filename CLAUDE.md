# CLAUDE.md

本文件为在此仓库中工作的 Claude Code 提供指引。

## 项目是什么

ItamiTimer（一袋米要扛几楼）：桌面端带强制约束的专注计时器（Windows + macOS，
Avalonia 12 / .NET 10）。勾选允许的小目标后提交任务，程序拿开始时刻查 ActivityWatch
的事件历史重放整段——只有命中规则且人在座的时间计入。偷懒不弹窗不出声，只有表盘上
的红格和越滑越远的截止弧。没装 AW 时退化成纯番茄钟。

**功能已完整，真机验证过多轮。当前工作模式是改进和修 bug**，不是从设计推进实现。

## 三份文档，各司其职

| 文件 | 内容 | 改代码前 |
|---|---|---|
| [`DESIGN.md`](./DESIGN.md) | 当前系统设计（按主题组织）：判定模型、重放算法、时间模型、表盘规格、闹钟、跨平台 | **必读相关章节** |
| [`DECISIONS.md`](./DECISIONS.md) | 护栏清单：被推翻的方案、知情接受的代价、「不要翻案」 | **动手前先查**——你觉得「显然可以改进」的地方多半在这里有一条 |
| [`README.md`](./README.md) | 英文对外介绍 | 用户可见的行为变了要同步 |

**这个项目两天里推翻过自己很多次，全部有案可查。** 想改一条现有行为时：先查
DECISIONS.md 有没有这条；有，就先跟用户确认再动；没有，也要在改完后把新决策补进去。

## 硬性约束（违反即事故）

- `App.axaml` 的 `Name="ItamiTimer"`、csproj 的 `AssemblyName=ItamiTimer` **永不改**
  ——自身豁免的依据，改了不报错，只是安静把账算错（DECISIONS F1/F2）。
- Core 保持 `net10.0`、零 UI 引用；App 的 TFM 也是 `net10.0`，`-windows` 别加回去。
- 不落盘任务状态；不做缓存；不自动降级模式；不自动开始下一轮。
- `Neutral` 计入、`SelfApps` 硬编码——动之前看 `PomodoroFallbackTests`。
- 仓库不放位图和音频；界面文字英文（窗口标题中文是产品名）；`rules.json` 是用户数据
  不翻译。

## 构建 / 运行

```bash
dotnet build ItamiTimer.slnx
dotnet test ItamiTimer.slnx
```

发布（**必须限定 RID**，否则 560MB；pdb 由 csproj 的 `StripPdbFromPublish` 自动剔除）：

```bash
dotnet publish src/ItamiTimer.App -c Release -r win-x64 --self-contained false -o "$LOCALAPPDATA/Programs/ItamiTimer"
```

发布目标被正在运行的 ItamiTimer 锁住时会重试 10 次然后失败——先让用户关程序。
用户从桌面快捷方式启动 `%LOCALAPPDATA%\Programs\ItamiTimer\ItamiTimer.exe`，
要用户实机验证就得先发布。

macOS 打包必须走 `./pack-macos.sh`（bundle 承载图标 + DOTNET_ROOT）。

调试出口（headless，不开窗口）：`--dial-specimens <目录>` / `--export-icon <路径>` /
`--export-iconset <目录>`。

## 布局与惯例

```
src/ItamiTimer.Core/   判定与重放（纯函数为主）   ← 逻辑改动优先落这里，可测
src/ItamiTimer.Cli/    itami 命令行，真实数据干跑  ← 唯一给账单的地方
src/ItamiTimer.App/    Avalonia 界面              ← 平台差异每处收口在单个文件
tests/                 xUnit，测试名是中文句子，直接陈述行为
```

- git：远端 `github.com/Achillesy/CSharp_ItamiTimer.git`，默认分支 **`master`**。
  仓库现为 private；对外发布时用户会另建仓库拷贝内容。
- 源码注释、提交信息、开发文档用中文；日志、异常、界面文字用英文。
- 运行时数据在 `%LOCALAPPDATA%\ItamiTimer\`（settings.json / rules.json / itami.log）。
  Release 也写日志——界面全程沉默，日志是唯一能事后查的地方。
- 姊妹项目 ActivityWatchJournal（Python）是**纯参考**：搬结论不搬代码、不跨进程调用。
