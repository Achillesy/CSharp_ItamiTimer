# ItamiTimer

一款为 Windows 和 macOS 打造的、**带强制约束**的桌面专注计时器。

传统番茄钟全靠自觉；ItamiTimer 不信任你。它读取本机 [ActivityWatch](https://activitywatch.net/)
记录的真实窗口活动历史，只把你**确实**待在承诺目标里、且人在座的那些秒数算成专注。开个浏览器、
切到聊天软件，或者干脆走开——那些时间悄悄地不计入：表盘上的灰色截止弧会往前滑，你只能眼睁睁
看着它变长。

![ItamiTimer 主界面](screenshots/ItamiTimer.png)
![ItamiTimer 设置窗口](screenshots/Settings.png)

> ⚠️ 主界面截图停在 2.0.0 之前，还是带边框的老样子——现在长什么样见[窗口](#窗口)。

**所有的痛感都在表盘上**
```
痛みを感じろ，
痛みを考えろ，
痛みを受け取れ，
痛みを知れ，
痛みを知らぬ者（もの）に，
本当の平和はわからん，
俺は弥彦（やひこ）の痛みを忘れない，
ここより，
世界に痛みを，
神罗天征（しんらてんせい）。
```

## 番茄工作法，以及这里跟它的不同

经典手法（Cirillo，1980 年代）：选一个任务，定 25 分钟，响了就停，歇 5 分钟；每四轮再歇长一点。
它的两个已知弱点：计时器量的是**流逝时间**而非**专注时间**，合规全靠自觉。

ItamiTimer 保留骨架——一次承诺、一段专注、一段挣来的休息——但把自觉换成审计：

| | 经典番茄钟 | ItamiTimer |
|---|---|---|
| 计入什么 | 墙钟时间 | 只有命中所选目标、且人在座的那些秒 |
| 计时器状态 | 内存里的倒计时 | **没有倒计时这回事**。表盘是 ActivityWatch 历史的投影 |
| 偷懒 | 计时器照跑 | 秒数不计入；截止弧往前滑——任务被拖长 |
| 离开（AFK） | 计时器照跑 | 不计入，也不算你的错——画成空心虚线框，不是红色 |
| 惩罚 | 没有 | 时间本身。什么都不作废，什么都不响，你只是完成得更晚 |
| 休息 | 固定 5 分钟 | ⌈专注分钟数 ÷ 5⌉ 分钟，从检测到达成那一刻起算 |
| 每 4 轮一次长休息 | 有 | 没有——一个任务 = 一段专注 + 一段休息，然后程序**停下来等你** |
| 自动开始下一轮 | 常见 | **绝不会**。开始任务永远是你自己的动作 |
| 报表 | 各家不同 | 屏幕上永远没有。表盘上的色块就是全部故事 |

## 窗口

2.0.0 起窗口**没有边框、没有背景**——没有标题栏，没有外框，也没有那层灰底。表盘直接
落在你的壁纸上；只有 Start 按钮以下那部分留了一张浅色卡片垫底，让滑块和目标列表还看
得清。

没了标题栏，两件日常操作跟着变：

- **拖表盘移动窗口**。表盘上任意位置都行，下面那些控件照常响应自己的操作。（拖动范围
  跟着实际画出来的东西走，所以表盘外接矩形的四个角不算在内。）
- **在表盘上点右键关闭窗口**（任务栏 / Dock 那条路照旧）。任务没结束时照样先问一句
  ——退出等于放弃这一轮，这条从来没变过。

窗口会记住你上次把它放在哪。万一某个位置跑到屏幕外面去了（拖过了头，或者它原来待的那
块显示器已经拔掉了），松手之后它会自己回到可见区域。**在两块显示器之间拖不受影响**：
松手时它落在哪块屏上占得多，就归位到那一块。

## 环境要求

- 本机跑着 [ActivityWatch](https://activitywatch.net/)，**两个** watcher 都要有：
  `aw-watcher-window`（在做什么）和 `aw-watcher-afk`（人在不在）。afk watcher 不是可选项——
  窗口事件靠心跳持续增长，就算人已经走开也一样，没有 afk 数据的话，"停在目标应用上起身
  走开"这种事完全隐形。
- .NET 10 运行时（编译需要 SDK）。

## 编译与运行

```bash
dotnet build ItamiTimer.slnx
dotnet test ItamiTimer.slnx
```

Windows 下发布（**必须指定 RID**，不指定的话所有平台的 Skia/HarfBuzz 原生库都会被打进去，
膨胀到约 560MB）：

```bash
dotnet publish src/ItamiTimer.App -c Release -r win-x64 --self-contained false -o "$LOCALAPPDATA/Programs/ItamiTimer"
```

结果：约 27MB，35 个文件。发布时会自动剔除两个巨型原生 `.pdb`（csproj 里的一个 MSBuild
target 做的——`dotnet publish` 本身不会剔除）。

macOS——必须打成 `.app` bundle（图标、Dock、以及 Finder 启动时需要的 `DOTNET_ROOT`
环境变量）：

```bash
./pack-macos.sh --dmg
```

Windows——要给别人用而不是本机开发调试时，打一个安装包（`dist/ItamiTimer-<版本>-win-x64.exe`）。
装机时会检测 .NET Desktop Runtime，没有就提示下载安装，目标机器不需要预装 .NET。需要
Inno Setup 6（`winget install --id JRSoftware.InnoSetup -e`）作为构建工具：

```powershell
.\pack-windows.ps1
```

专注时长滑块在 Release 版是 **10–50 分钟**，Debug 版是 **3–10 分钟**——这样开发时不用真的
坐等 25 分钟才能看到达成那一刻。

## 项目结构

```
src/ItamiTimer.Core/    net10.0  核算引擎：规则、判定 buffer、投影计算。
                                 无 UI、无平台调用——由 csproj 强制约束。
src/ItamiTimer.Cli/     net10.0  `itami`——拿真实 ActivityWatch 数据干跑引擎。
                                 唯一会打印报表的地方。
src/ItamiTimer.App/     net10.0  Avalonia 界面：表盘、骨牌、声音、闹钟、Alarms 清单、设置。
tests/                  xUnit    纯函数测试：合成事件，不需要等待。
```

程序画的每一样东西——表盘、骨牌、窗口图标、exe 图标本身——全部是计算出来的矢量几何。仓库
里不含任何位图或音频资源；连滴答声都是运行时合成的（白噪声脉冲 + 阻尼正弦），提示音直接用
操作系统自带的。

运行时数据：

| | |
|---|---|
| Windows | `%LOCALAPPDATA%\ItamiTimer\` |
| macOS | `~/Library/Application Support/ItamiTimer/` |

| 文件 | 由谁写 | 内容 |
|---|---|---|
| `rules.json` | **你**，手写 | 你的目标，以及可选的 `executeCommand` |
| `settings.json` | 程序 | 声音选项、开关状态、闹钟时间、窗口位置 |
| `during.json` | 程序 | 每个目标累计的专注秒数 |
| `alarms.md` | **你**（或一个脚本），手写 | 预约打卡提醒，见下文 |
| `itami.log` | 程序 | 1MB 滚动；界面全程沉默，这是唯一能事后查清发生了什么的地方 |

任务状态**永不落盘**。关掉程序等于放弃当前这一轮——不过已经挣到的时间仍然会被计入
`during.json`。

### 预约打卡提醒（Alarms 清单）

跟表盘上那根手拨的一次性闹钟针不是同一回事：ItamiTimer 还能盯着一份纯文本的 Markdown
清单，处理"到点该做什么"这类标准约会——吃药、日历里定好的打卡时间，任何有固定时刻的事。
让一个脚本（或你自己）把内容写进数据目录下的 `alarms.md`：

```markdown
- [ ] 2026-08-06 14:00 吃药
- [ ] 2026-08-06 21:30 晚间打卡
- [x] 2026-08-05 09:00 已经做过——勾选就能单独消音一条
```

程序只读这份文件——从不回写、不删除过期的行，也不会自己把"每天下午两点"这种周期规则
展开成一串日期；这件事由生成这份文件的东西负责。每分钟检查一次，到点的条目**无条件**
弹一条系统通知，要不要同时出声是 Settings 里的一个开关。下一条提醒在 12 小时以内时，
表盘木框内侧会出现一个小红点。

## 干跑引擎

```bash
itami replay --since "2026-07-27 14:00" --until "2026-07-27 15:30" --minutes 25 --group Economics
```

这条命令用**跟界面完全相同的引擎、相同的一分钟节拍**去重放真实的 ActivityWatch 历史，打印出
色块和报表。这是检验你的规则到底有没有匹配上你实际行为最快的办法。

`itami start` 在终端里跑一轮真实计时；`itami bench` 完全不连 ActivityWatch，用合成事件跑引擎。

## 挑选和试跑闹钟要执行的命令

rules.json 里的 `executeCommand` 是一份常用命令收藏夹，**闹钟永远只跑第一条**。
`itami commands` 就是用来换第一条、以及在不等闹钟真到点的前提下试跑某一条的：

```bash
itami commands --list       # 只看（* 标出 #0），什么都不改
itami commands --select N   # 把第 N 条挪成 #0（改写 rules.json，留一份 .bak）
itami commands --select     # 同上，但先打清单再问你要哪个编号
itami commands --execute    # 跑 #0，y/N 确认后执行
```

它操作的是**程序真正在用的那一份 rules.json**（`AppData.RulesPath` 的三级查找链），
并且把这个路径打在第一行。选完对正在运行的 ItamiTimer **立刻生效**——闹钟到点那一刻
会重读 rules.json，不用重启。

**`--execute` 刻意不带编号**：想试别的就先 `--select` 把它选成 #0。这样"你试的那条"和
"闹钟真正会跑的那条"在设计上就是同一条，不存在"我刚才测的到底是哪条"这种事。它走的是
**跟闹钟到点完全同一份代码**（源文件被 link 进两个项目，不是抄一份），所以"在这儿试通了"
对界面才是有意义的结论。换序是文本级搬运，绝不做 JSON 往返序列化：你写的注释和缩进逐
字节保留；碰上它拿不准的数组形状会直接拒绝改写，而不是猜。

## 调试出口

三个渲染完就退出、不开窗口的命令行开关（对 CI 安全）：

```bash
ItamiTimer --dial-specimens <目录>    # 把表盘几个关键状态渲染成 PNG
ItamiTimer --export-icon <路径.ico>   # 把矢量图标导出成 exe 图标
ItamiTimer --export-iconset <目录>    # 同上，导出成 macOS iconutil 用的 .iconset
```

## 许可证

[PolyForm Noncommercial 1.0.0](https://polyformproject.org/licenses/noncommercial/1.0.0)——
见 [`LICENSE`](./LICENSE)。源码公开、任何非商业用途免费；商业用途需要取得版权所有者许可。
