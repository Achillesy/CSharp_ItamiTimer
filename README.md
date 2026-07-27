# 一袋米要扛几楼（ItamiTimer）

Windows 桌面端**带强制约束的专注任务计时器**。

勾选本轮允许的小目标，提交任务；此后**只有窗口标题/应用命中规则、且人在座**的时间才算专注。切到别处那段时间不计入，任务因此被拖长——表盘上那一分钟的格子变红，灰色的截止弧往前滑走。

名字本身就是「疼痛感」的意思。**痛感全部来自表盘**：不弹窗、不出声、不给账单、不报数字，自己看，自己猜。

它的计时**完全建立在 ActivityWatch 的事件历史之上**：程序不持有"还剩多少秒"这种累加值，任何时刻的状态 = 纯函数(任务记录, AW 事件历史, now)。要知道进度就拿开始时刻向 AW 查整段区间、重放一遍。所以轮询间隔不影响精度、临时连不上 AW 不损坏任何东西、漏掉一个计时点也不会少记一分钟。

## 界面

一个**你愿意开在桌面上的钟**，和一个**盯着你学习的督工**，装在同一个窗口里。

- **表盘**：一分钟一格，绿 → 琥珀 → 红表示那一分钟的纯度；灰弧从写入头画到预计结束时刻，偷懒多久它就往前滑多久。全部矢量绘制，不含任何位图资源——番茄图标、木质边框、指针投影、exe 的文件图标，都是代码画出来的。
- **七块骨牌**：倒下几块就是星期几。倾角是按凸多边形接触求解算出来的，不是拍脑袋。
- **右上角**：喇叭（滴答声开关）、图钉（窗口置顶）。这两个属于「钟」。
- **齿轮**：三条通知音各自可关 + 滴答音量。这三条属于「督工」。

滴答声是**运行时合成**的（白噪声 × 指数衰减 + 阻尼正弦），因为 `C:\Windows\Media` 里一个滴答都没有。「滴」和「答」音色不同、按秒交替——真钟的擒纵机构两边不对称。

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

发布（**必须指定 RID**，否则 Skia/HarfBuzz 各平台的原生库和调试符号全进来，会从 27MB 涨到 560MB）：

```bash
dotnet publish src/ItamiTimer.App -c Release -r win-x64 --self-contained false -o "$LOCALAPPDATA/Programs/ItamiTimer"
```

**日志只有 Debug 版写**（`[Conditional("DEBUG")]`）。Release 下出错是完全无声的——排查问题得先切回 Debug 跑一遍。

## 项目结构

```
src/ItamiTimer.Core/   net10.0          类库      判定与重放，无 UI 无 Win32
src/ItamiTimer.Cli/    net10.0          itami     命令行原子层，用真实 AW 数据干跑
src/ItamiTimer.App/    net10.0-windows  界面      Avalonia 12
tests/ItamiTimer.Core.Tests/            xUnit     80 个测试
```

**`Core` 必须保持 `net10.0`（无 `-windows`）**——这是纪律的执行机制本身：往 Core 里塞 UI 或 P/Invoke 会直接编不过。重放是纯函数，所以测"专注 25 分钟走完"不用真等 25 分钟，喂合成事件就能穷举边界。

其它文件：

- `rules.json` — 小目标规则。运行时按三级找：`%LOCALAPPDATA%\ItamiTimer\`（你自己的）→ exe 旁边（随程序发布的默认）→ 当前工作目录
- `wild-enchanting-planet.md` — 已被取代的 v1 设计，仅作历史记录

运行时数据在 `%LOCALAPPDATA%\ItamiTimer\`：`settings.json`、`rules.json`、以及仅 Debug 的 `itami.log`。**程序对任务状态只读不写**。

## 调试出口

两个不属于产品功能的命令行开关，正常启动路径一个字节都没碰：

```bash
ItamiTimer.exe --dial-specimens <目录>    # 把表盘在几个关键状态下离屏渲染成 PNG
ItamiTimer.exe --export-icon <路径.ico>   # 从 TomatoIcon 导出 exe 的文件图标
```

表盘在 App 层，Core 的测试碰不到它——半径、角度、叠放次序这类几何错误只有看图才发现得了。

## 许可

Apache 2.0，见 [`LICENSE`](./LICENSE)。
