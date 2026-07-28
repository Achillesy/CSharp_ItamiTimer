# 一袋米要扛几楼（ItamiTimer）

桌面端**带强制约束的专注任务计时器**。Windows 和 macOS 都能跑（同一份代码，见「跨平台」）。

勾选本轮允许的小目标，提交任务；此后**只有窗口标题/应用命中规则、且人在座**的时间才算专注。切到别处那段时间不计入，任务因此被拖长——表盘上那一分钟的格子变红，灰色的截止弧往前滑走。

名字本身就是「疼痛感」的意思。**痛感全部来自表盘**：不弹窗、不出声、不给账单、不报数字，自己看，自己猜。

它的计时**完全建立在 ActivityWatch 的事件历史之上**：程序不持有"还剩多少秒"这种累加值，任何时刻的状态 = 纯函数(任务记录, AW 事件历史, now)。要知道进度就拿开始时刻向 AW 查整段区间、重放一遍。所以轮询间隔不影响精度、临时连不上 AW 不损坏任何东西、漏掉一个计时点也不会少记一分钟。

## 界面

一个**你愿意开在桌面上的钟**，和一个**盯着你学习的督工**，装在同一个窗口里。

- **表盘**：一分钟一格，绿 → 琥珀 → 红表示那一分钟的纯度；灰弧从写入头画到预计结束时刻，偷懒多久它就往前滑多久。全部矢量绘制，不含任何位图资源——番茄图标、木质边框、指针投影、exe 的文件图标，都是代码画出来的。
- **七块骨牌**：倒下几块就是星期几。倾角是按凸多边形接触求解算出来的，不是拍脑袋。
- **右上角**：喇叭（滴答声开关）、图钉（窗口置顶）。这两个属于「钟」。图形也是矢量画的，不依赖任何图标字体。
- **齿轮**：三条通知音各自可关 + 滴答音量。这三条属于「督工」。
- **「开始」按钮就是那条分割线**：它以上是给眼睛的，以下是给手的。任务进行中它变成红色的「Give up」——界面上唯一一处用红色，因为它作废整轮。

**界面文字是英文**（`Start` / `Give up` / `Settings`……），只有窗口标题保留中文原名。这不是多语言版，就是一个英文版，目的是让更多人能用。你自己的小目标名字来自 `rules.json`，写什么语言都行。

滴答声是**运行时合成**的（白噪声 × 指数衰减 + 阻尼正弦），因为 `C:\Windows\Media` 里一个滴答都没有——macOS 那 14 个系统音更没有，全是提示音。「滴」和「答」音色不同、按秒交替——真钟的擒纵机构两边不对称。

## 核心思路

- **状态是推导出来的，不是攒出来的。** 见上。这一条是整个设计的地基（`DESIGN.md` 原则 4）。
- **判定模型**：一段时间计入 ⟺ 命中已勾选的小目标 **且** 人在座（AW 的 afk 数据）。`Absent` 优先级高于一切——锁屏时 `LockApp.exe` 在中性名单里而 afk 说不在，必须判 `Absent`，否则就是"锁屏一小时专注时长照涨"。
- **勾选集合用并集，追溯生效。** 中途补勾一个小目标，整段历史都按新的并集重算。「经济学学腻了，补勾一个 Blender，剩下的时间不算偷懒」——这是刻意的，不是漏洞。
- **任务不落盘，退出程序 = 放弃任务。** 没有 `current-task.json`，没有历史文件。任务最长 50 分钟，崩溃最多丢这一轮。历史统计是姊妹项目 ActivityWatchJournal 的活。
- **绝不自动开始下一轮。** 休息结束就是任务终结，停在那里等你。
- **AW 是底座，不是零件。** 所有判定输入只来自它的本地 REST API（`http://127.0.0.1:5600`），不调用任何系统原生窗口枚举/监听 API。

完整决策记录、任务生命周期、重放算法、表盘渲染规格、时间模型见 **[`DESIGN.md`](./DESIGN.md)**。

## 依赖

- **[ActivityWatch](https://activitywatch.net/) 必须在本机运行**——唯一的硬依赖，连不上就直接说无法工作，不做任何猜测和降级。两个监听器**都必需**：
  - `aw-watcher-window`（`currentwindow`）——判断在不在做正事
  - `aw-watcher-afk`（`afkstatus`）——判断人在不在。**不是可选项**：AW 的窗口监听器即使人走开也会心跳续期当前窗口那条事件，不看 afk 的话「把目标应用停在前台然后起身走开」是完全隐形的作弊路径
- .NET 10 SDK

## 构建与运行

```bash
dotnet build ItamiTimer.slnx
```

```bash
dotnet test ItamiTimer.slnx
```

发布**两步**（**必须指定 RID**，否则 Skia/HarfBuzz 各平台的原生库和调试符号全进来，实测会涨到 560MB）：

```bash
dotnet publish src/ItamiTimer.App -c Release -r win-x64 --self-contained false -o "$LOCALAPPDATA/Programs/ItamiTimer"
```

```bash
find "$LOCALAPPDATA/Programs/ItamiTimer" -name '*.pdb' -delete
```

**第二步不能省。** `dotnet publish` 只会把 NuGet 原生包里的东西照搬出来，**不会剔除调试符号**，所以它的原样输出是 **127 MB / 39 个文件**；删掉 `.pdb` 才是 **27 MB / 35 个文件**。那 100 MB 就两个文件：`libSkiaSharp.pdb` 80.1 MB + `libHarfBuzzSharp.pdb` 19.9 MB。

macOS 打包成 `.app` 装进 `~/Applications/`（发布 + 画图标 + 组 bundle + ad-hoc 签名一步到位）：

```bash
./pack-macos.sh
```

**macOS 上必须走这个脚本，不能直接跑发布出来的二进制。** 除了图标和 Dock，bundle 还决定了 AW 上报的应用名——自身豁免靠它（见下）。

**Debug 和 Release 都写日志。** 界面对用户是全程沉默的（分割线以下一个提示字都没有），日志是唯一能事后看出"它到底怎么了"的地方，所以正式版也留着。开销可以忽略：一分钟一行，一轮任务最多五十行，超过 1MB 自动滚动、只留一份旧的。

## 项目结构

```
src/ItamiTimer.Core/   net10.0   类库      判定与重放，无 UI 无平台调用
src/ItamiTimer.Cli/    net10.0   itami     命令行原子层，用真实 AW 数据干跑
src/ItamiTimer.App/    net10.0   界面      Avalonia 12
tests/ItamiTimer.Core.Tests/     xUnit     80 个测试
```

**`Core` 必须保持不含 UI**——往里塞 UI 包会直接编不过。重放是纯函数，所以测"专注 25 分钟走完"不用真等 25 分钟，喂合成事件就能穷举边界。

其它文件：

- `rules.json` — 小目标规则。运行时按三级找：用户数据目录（你自己的）→ 程序旁边（随程序发布的默认）→ 当前工作目录
- `pack-macos.sh` — macOS 打包脚本
- `wild-enchanting-planet.md` — 已被取代的 v1 设计，仅作历史记录

运行时数据（`settings.json`、你自己那份 `rules.json`、`itami.log`）：

| | |
|---|---|
| Windows | `%LOCALAPPDATA%\ItamiTimer\` |
| macOS | `~/Library/Application Support/ItamiTimer/` |

**程序对任务状态只读不写。**

## 跨平台

判定层从设计上就与平台无关——所有输入只来自 AW 的 REST API，不碰任何原生窗口 API。这条在 2026-07-28 移植 macOS 时得到实测印证：**Core、Cli 和 80 个测试一行没改就全过了**，全部改动集中在界面层，且每一处平台差异都收口在单个文件里（放音、键鼠空闲、置顶、数据目录、右上角图标）。

移植中撞到、也最容易重犯的两条：

- **`App.axaml` 的 `Name="ItamiTimer"` 是正确性的一部分。** macOS 的 aw-watcher-window 上报的应用名就是 Avalonia 的 `Application.Name`；不设它时默认叫 `Avalonia Application`，于是程序认不出自己，看一眼进度就被判成违规——正是那个"提醒 → 用户看提醒 → 又违规"的死循环。
- **`ignore` 名单两个平台的条目写在同一份 `rules.json` 里。** AW 报的 app 名两边完全不同（`explorer.exe` ↔ `Finder`、`LockApp.exe` ↔ `loginwindow`），而一条在另一平台上永远匹配不上的正则是无害的。

## 调试出口

三个不属于产品功能的命令行开关，正常启动路径一个字节都没碰：

```bash
ItamiTimer --dial-specimens <目录>       # 把表盘在几个关键状态下离屏渲染成 PNG
ItamiTimer --export-icon <路径.ico>      # 从 TomatoIcon 导出 exe 的文件图标（Windows）
ItamiTimer --export-iconset <目录>       # 同上，导成 .iconset 交给 iconutil（macOS）
```

表盘在 App 层，Core 的测试碰不到它——半径、角度、叠放次序这类几何错误只有看图才发现得了。

## 许可

Apache 2.0，见 [`LICENSE`](./LICENSE)。
