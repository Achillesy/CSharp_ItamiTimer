# ItamiTimer（一袋米要扛几楼）—— 系统设计

> **本文是唯一的设计文档**：当前实现的系统设计 + 待办需求 + 已知 Bug。
> 原 `ISSUE.md`（需求清单）和 `ISSUE_FIX.md`（实施记录）已于 2026-08-02 并入本文。
>
> 配套：[`DECISIONS.md`](./DECISIONS.md) 护栏清单（被推翻的方案、知情接受的代价、
> 「不要翻案」）；[`README.md`](./README.md) 英文对外介绍；[`CLAUDE.md`](./CLAUDE.md) 工作规则。

---

# 第一部分 · 当前设计

## 1. 定位：番茄工作法 + 客观审计

番茄工作法的骨架：选一个任务 → 承诺一段专注 → 挣得一段休息 → 停下。它的两个弱点是
计时器量的是**流逝时间**而非**专注时间**，且合规全靠自觉。

ItamiTimer 保留骨架，把自觉换成审计：**只有窗口活动命中所选小目标、且人在座的时间
才计入**。审计数据来自本机 ActivityWatch（AW）的事件历史，程序自己不做行为监测。

偷懒的后果不是警告、不是作废，而是**任务被拖长**——表盘上灰色的截止弧往前滑。

## 2. 核心原则

0. **AW 是底座，不是零件。** 判定输入只来自 AW 本地 REST API，不碰系统原生窗口 API。
1. **任务一旦提交就已确定。** 起点固定、时长锁定。程序**永远不代替用户提交任务**。
2. **计时完全基于 AW。** 程序自己不累加时间。
3. **窗口是外在表现，进程即任务。** 最小化不影响任何东西；**退出程序 = 放弃任务**。
4. **状态是推导出来的，不是攒出来的。** 状态 = 纯函数(任务记录, AW 历史, now)。

> ⚠️ 原则 4 在**判定引擎重写后有所让步**：`JudgmentBuffer` 是一块会被增量写入的秒级
> 数组，不再是每轮从头重放。它靠「每个计时点重写最近 4 分钟」来自我纠正，而不是靠
> 全量重算。这是知情的取舍（见 §4），但也正是 §15 那个休息起点 Bug 的土壤。

## 3. 判定模型

**没有退化模式**（2026-07-31 删除）：程序永远是约束模式，界面不随 AW 在不在而变形。
AW 连不上时由判定模型自己兜底（记为 `AwOffline`，默认算专注）。

六个状态码，直接就是 buffer 里存的字节：

| 码 | 名 | 含义 | 计入专注 | 盘面 |
|---|---|---|---|---|
| 0 | `Init` | 已分配，AW 数据尚未到达 | ✘ | 不画 |
| 1 | `Gray` | 预计任务时间（尚未走到） | ✘ | 灰弧 |
| 2 | `Focused` | 命中所选小目标 | ✔ | 绿 |
| 3 | `AwOffline` | AW 脱机，**默认算专注** | ✔ | 绿 |
| 4 | `Afk` | AW 的 afk 说人不在 | ✘ | 空白（离开不怪你） |
| 5 | `OffTask` | 其余（含规则里没有的应用） | ✘ | 红 |

分类顺序（`GroupRules.Classify`，已简化——`Neutral`、`ignore` 名单、自身豁免全部删除）：

```
1. 命中【当前选中的那一个】小目标？ → OnTask
2. 其余                            → OffTask（fail-closed）
```

`Afk` 优先于一切：afk 说不在，无论窗口是什么都判 `Afk`（否则锁屏时长照涨）。

**小目标是单选**（Radio，2026-07-31 由多选 CheckBox 改）。Start 之后锁定不可改——
连带删掉了「中途补勾、追溯生效」那一整套语义（`TaskRecord.GroupChanges` 已删）。

## 4. 判定引擎：`JudgmentBuffer`

取代了原来的 `Replay` 全量重放（`Replay.cs` 仍在仓库里，但已不被调用）。

**存储空间**：`byte[7380]` = 180 秒 padding + 7200 秒绘制区（120 分钟）。
`buffer[i]` 对应绝对时刻 `WallClock + i 秒`，`buffer[180]` = 任务起点。
padding 存在的理由：AW 查询窗口要往前取 4 分钟，起点前 3 分钟必须有地方落。

**初始化**：`[0, focusSeconds + 180)` 填 `Gray`（预计任务时间），其余 `Init`。

**每个整分钟计时点**：
```
查 AW [now-4min, now) → Judgment.ClassifySeconds 逐秒打标
→ buffer.Write(offset, 结果)        ← 重写最近 4 分钟
→ buffer.TryArchive()               ← 跑满 2 小时才滚动
→ ToMinuteCells() 投影成每分钟一格 → 渲染层零改动
```

**为什么固定 4 分钟窗口**：AW 的 afk 默认 180 秒才出结论并**回填**，取 4 分钟必然覆盖。
代价如实记：如果用户把 afk 超时调长到 5 分钟，会误算 1 分钟的离开时间——已接受。

**2 小时滚动归档**：`elapsed ≥ 7200` 时把 `[0,3600)` 的专注秒数累进 `DuringSeconds`，
buffer 左移 3600，尾部清零。任务因此没有时间上限。

**投影**：
- `ToMinuteCells()` —— 60 秒一格，只吐**完整**的分钟。CLI 和表盘消费同一个列表。
- `IsFocusComplete` —— `DuringSeconds + CountFocused() ≥ FocusTargetSeconds`。
- `FocusCompletedAt()` —— 逐格累加，返回跨过阈值那一格的**结束边界**（`Start+1min`）。
  ⚠️ 它每次都从头重新推导，这正是 §15.1 那个 Bug 的机制。

## 5. 规则文件 `rules.json`

三级查找（`AppData.RulesPath`，绝不只看工作目录）：用户数据目录 → exe 旁 → 工作目录。
允许注释和结尾逗号。正则 `search` 语义。

```json
{
  "groups": {
    "学习经济学": { "rules": [ { "title": "(?i)经济学|曼昆" }, { "app": "EconReader\\.exe" } ] },
    "上季度目标": { "disabled": true, "rules": [ { "title": "Blender" } ] }
  },
  "executeCommand": {
    "windows": "shutdown /s /t 0",
    "macos":   "osascript -e 'tell application \"System Events\" to shut down'"
  }
}
```

- 组内规则是**或**；单条规则里 `app` 与 `title` 是**与**。
- 三种形状缺一不可：只写 `app`（用这个工具就算）、只写 `title`（做这件事就算，
  什么工具都行）、都写（卡浏览器标签页）。
- 空规则 / 空组是**加载期错误**（匹配一切 = 关掉约束）。
- `disabled: true` 屏蔽旧目标而不删除。
- 两个平台的 app 名写在同一份文件里，永不匹配的正则无害。
- `executeCommand` 供闹钟的 Execute 用（§9），按当前 OS 取一条。

> ⚠️ **两条读取路径**：判定走类型模型 `RulesFile`（只认 `groups`），
> `Shutdown.cs` 用裸 `JsonDocument` 另读一遍 `executeCommand`。见 §15.4。

## 6. 任务生命周期

```
选一个小目标（Radio）、拖时长滑块、点 Start
  ↓  startedAt = 截断到当前整分钟（09:01:25 → 09:01:00）
  ↓  FocusMinutes 锁定；RestMinutes = ⌊focus/5⌋+1；立刻画整段灰弧，不查 AW
【专注】每个整分钟：查 AW 4 分钟窗口 → 写 buffer → 更新色块与截止弧
  ↓  IsFocusComplete → _focusDoneAt = FocusCompletedAt()
【休息】画蓝色扇形 [_focusDoneAt, +RestMinutes]；纯本地计时，零 AW 访问
  ↓  圆弧**不清除**（2026-07-31 #11 改）——扇形先画、圆弧后画，z-order 天然正确
  ↓  分针扫出扇形 → 响一声 → 会话终结、清圆弧与扇形
【空盘】停在这里等用户。空盘即邀请。
```

- **休息不被观察**：期间干什么都不重要，不进任何记录。
- 休息中点 Start（显示 "New round"）= 终结当前任务 + 开新一轮。
- 专注中点 Start（红色 "Give up"）或关窗口 → 确认框 → Abandoned。
- 崩溃 / 关机 / 进程被杀 = 放弃。程序区分不了「点了 X」和「被杀」。
- **时长滑块现在是 3~10 分钟**（原 10~50，为测试便利改小）。Core 不设范围，
  约束只在滑块上。

## 7. AW 接口约定

### 7.1 两个 bucket

| type | 用途 | 缺了会怎样 |
|---|---|---|
| `currentwindow` | 在不在做正事 | 无法判定 |
| `afkstatus` | 人在不在 | **「停在目标应用上走开」完全隐形**——窗口事件靠心跳续期自己长大，事件流不留空白。这是最省力的作弊路径 |

### 7.2 硬知识（全部实测得来，实现时必须遵守）

| # | 事实 | 应对 |
|---|---|---|
| T1 | AW 区间查询只按事件**自己的开始时间**过滤，跨起点的事件静默消失 | 查询窗口向前放宽再自己裁 |
| T3 | AW 窗口事件滞后 `now` 约 6~12 秒 | 末尾几秒恒为空——**这正是 §15.1 Bug 的触发条件** |
| T4 | 窗口标题每秒变时产生 `duration=0` 的事件 | 空隙 ≤5 秒才桥接（否则填平真窟窿） |
| T5 | afk 事件**回溯**写入，起点回填到最后一次输入 | 发现时损失已写死 → 唯一有效的提醒是赶在截止线之前自己读键鼠空闲（§10） |
| T6 | 人安静下来后 afk 桶末端有 ≤180 秒空洞 | 4 分钟重查窗口天然覆盖，会自我纠正 |
| — | 系统代理会吞 localhost 请求 | `HttpClientHandler { UseProxy = false }` |
| — | `AddSeconds(double)` 四舍五入到毫秒，AW 是微秒精度 | `AwEvent.End` 用 `AddTicks` |
| — | AW 时间戳是 UTC | **在边界上归一**：`AwClient` 解析时直接 `ToLocalTime()` |

### 7.3 时间模型

- `startedAt` 截断到整分钟 → 每个色块恒为完整 60 秒。
- 查询节拍**锚在整分钟**（`FloorToMinute(now) > 已查过的分钟`），不锚在点击时刻。
- 计时点可以漏：buffer 按绝对偏移写入，晚一拍只是晚一拍。

## 8. 表盘渲染规格

几何归一化到 `rFace = 1.0`，12 点为 0°，顺时针，分钟 × 6°。

| 层 | 半径 | 说明 |
|---|---|---|
| 木质边框 | 1.02 | 渐变模拟受光（光源统一在左上） |
| 盘面 | 1.00 | 跟随主题：日面素白、夜面深灰 |
| 色环 lane 0 | [0.50, 0.68] | 超圈螺旋内缩：lane 1 [0.31,0.46]、lane 2 [0.14,0.26] |
| 数字 1~12 | 基线 0.70 | |
| 刻度 | 0.90~0.965 | 分钟细、五分粗 |
| 指针 | 时 0.50 / 分 0.72 / 秒 0.80 | 带锥度，投影偏移在指针之前画 |
| 闹钟黄针 | 0.62 | 比分针短、比时针略长 |

- **分针就是写入头**：第 i 格 = 钟面分钟 `[m₀+i, m₀+i+1)`。表盘同时是钟、进度条、
  账本，共用一套坐标。休息倒计时因此免费——分针扫出扇形即结束。
- **木桶短板**：`高度 = rIn + (rOut−rIn) × (0.5 + 0.5×纯度)`。颜色与高度编同一个量，
  一个给正常视觉，一个给所有人。下限 1/2，**绝不取 0**（零高度会和「人不在」撞车，
  而那是最不该混淆的一对：一个不怪你，一个全怪你）。
- **承诺弧**：从写入头到预计结束，灰 22%；终点画径向截止线。
- **休息扇形**：从圆心切出的整块，蓝色径向渐变，外缘 0.70。**先画扇形再画圆弧**。
- **秒针一秒一跳**，不是连续扫——扫秒针的钟不会响，滴答来自步进擒纵，物理上互斥。
- **骨牌**：七块 = 星期几，倾角按凸多边形接触求解。启动时与点 Start 时各查一次日期。

## 9. 闹钟与 Command

表盘即输入设备：**在钟面上滚滚轮**调闹钟，没有独立按钮，左右键点击刻意留白。

- **模型**：黄针停在 720 个格子上（0~719 分钟，**1 分钟一格**）。滚轮前滚逆时针、
  后滚顺时针；快滚加速（1-2 格 1× / 3-5 格 2× / 6-10 格 3× / 11+ 格 5×）。
- **响铃时刻在拨针那一刻算死**（`NextRing`），三级判断**全部严格小于**：
  `now < 今天的 T` → 今天 T；`now < T+12h` → T+12；否则明天 T。
  **恰好重合意为 12 小时后**，不是立刻响。之后只单调比较，不做角度容差。
- **黄针位置是推导值**：时间点对 12 小时取余（`Position` 是计算属性）。
- **持久化**：只存 `alarmFireAt` 一个值，退出时写一次。**启动时只显示不激活**
  （`Restore` 恒 `_fired = true`），要用户滚轮拨针或开 Execute 才 `Activate`。
- 一次性：响过即撤。检查节拍每秒一次。
- **Execute 开关**（Settings 卡 4）：开 → 到点执行 `rules.json` 的 `executeCommand`，
  音色下拉变灰；关 → 到点响铃、可选音色。**二者互斥。**
  Execute 每次启动强制复位为关（关机绝不跨会话）。

## 10. 声音

**只用系统自带的音，不打包音频资源**。Windows 枚举 `C:\Windows\Media\*.wav` 走 winmm；
macOS 三级枚举 `*.aiff` 走 AudioToolbox。找不到、放不出一律安静收场——
**提示音绝不能把程序搞挂**。

- 滴答 `SND_NOSTOP`：通道占用时跳过本次滴答，让通知音优先，不再互相打断。
- 三声通知（专注达成 / 休息结束 / 键鼠空闲），各自可关、选中即试听。
- 键鼠空闲声只在 `[60, 180)` 秒窗口响：过了 180 秒 AW 已回填 afk，救不回来了。
- 滴答**运行时合成**：白噪声 × 指数衰减 + 阻尼正弦，「滴」「答」两个缓冲区按秒交替。
- **Force Ticking**（Settings 卡 5）：开 → 主界面喇叭图标隐藏、滴答强制开；
  但 Start 到休息结束期间一律静音。冲突时强制规则优先。

## 11. 设置与数据

`%LOCALAPPDATA%\ItamiTimer\`（macOS `~/Library/Application Support/ItamiTimer`）：

| 文件 | 作者 | 内容 |
|---|---|---|
| `settings.json` | **程序**，随时整份重写 | 三声开关与音色、commandEnabled/Sound、forceTicking、tickEnabled/Volume、pinned、selectedGroup、duringByGroup、alarmFireAt、awBaseUrl |
| `rules.json` | **用户手写**，程序只读 | groups（判定规则）、executeCommand |
| `itami.log` | 程序 | 1MB 滚动。界面全程沉默，日志是唯一能事后查的地方 |

**任务状态不落盘**：没有 current-task.json，退出即放弃。设置和日志删掉不改变行为。

### 11.1 关于「把 settings.json 和 rules.json 合并」（2026-08-02 讨论，待定）

**先纠正一个前提**：用户提出合并的理由是「两个文件启动后都会被修改」——这个前提
**现在已经不成立**。2026-07-31 删掉了 `GroupRules.Accumulate`，累计时长改存
`settings.json` 的 `duringByGroup`。所以现在 `rules.json` 是**纯只读**的。

**真正的区别不是「谁会被改」，而是「谁是作者」**：

| | `rules.json` | `settings.json` |
|---|---|---|
| 作者 | 用户手写 | 程序 |
| 内容 | 正则、组名、命令行 | 开关、音色、累计值 |
| 注释与排版 | **有意义** | 无所谓 |
| 写坏的后果 | **约束本身失效**（判定规则是这个产品的本体） | 回到默认音色 |

**三个方案**：

| 方案 | 做法 | 代价 |
|---|---|---|
| **A（推荐）不合并** | 保持两个文件，把纪律写死：**程序绝不写用户手写的文件**。可把 `settings.json` 改名 `state.json` 让所有权一眼可见 | 仍是两个文件 |
| **C 合并 + 局部改写** | 一个文件，程序用 `JsonNode` 读整份、只改 `state` 子树再写回 | 一个文件；注释仍会丢（STJ 不能 round-trip 注释），但规则内容不经过类型模型往返 |
| **B 合并 + 整体重写** | 一个文件两个顶层节，程序序列化整份写回 | ❌ 用户手写的正则每次被程序重写；序列化一旦出错，**静默损坏的是判定规则本身** |

**推荐 A**。让程序去写用户手写的文件，等于把「注释会丢」变成常态，而且赌的是
序列化永远不出错——赌注是这个产品的核心（约束规则），赔率不划算。
如果目标只是「备份/同步少管一个文件」，C 可以接受；**B 不要做**。

## 12. 模块与项目布局

```
src/ItamiTimer.Core/   net10.0  判定与投影。无 UI、无平台调用（csproj 强制）
src/ItamiTimer.Cli/    net10.0  itami：start / replay / bench，真实或合成数据干跑
src/ItamiTimer.App/    net10.0  Avalonia 界面
  ├── Platform/        平台差异每处收口在单个文件，没有散落的 #if
  └── Drawing/         表盘、骨牌、图标——全部矢量计算
tests/                 xUnit
```

Core 的关键类型：`JudgmentBuffer`（秒级存储）、`Judgment`（逐秒分类，纯函数）、
`GroupRules`（规则编译与匹配）、`AwClient`（唯一碰网络的地方）、`TaskRecord`、
`MinuteCell`（渲染契约）、`TimeGrid`。

**两条改错不报错、只把账算错的身份标识**：`App.axaml` 的 `Name="ItamiTimer"`、
csproj 的 `AssemblyName=ItamiTimer`。macOS 上 AW 报的 `data.app` 就是前者。

## 13. 跨平台

判定层平台无关，工作量全在 App 层。平台差异收口于：`Sound` / `Tick` / `MacAudio` /
`InputIdle` / `WindowPin` / `AppData` / `Shutdown`。

- macOS **必须打成 `.app`**（`pack-macos.sh`）：bundle 用 `LSEnvironment` 写
  `DOTNET_ROOT`——双击启动的 GUI 不继承 shell 环境变量。
- 调试出口 `--dial-specimens` 走 **headless**，不初始化窗口平台（构建不该依赖
  有没有人登录着桌面）。
- 未验证风险：macOS 的 `localizedName` 随系统语言变（中文系统 `Finder`→`访达`）。

## 14. 构建与发布

```bash
dotnet build ItamiTimer.slnx
dotnet test  ItamiTimer.slnx
dotnet publish src/ItamiTimer.App -c Release -r win-x64 --self-contained false -o "$LOCALAPPDATA/Programs/ItamiTimer"
./pack-macos.sh
```

- **必须限定 RID**：否则所有平台的 Skia/HarfBuzz 原生库全进来（实测 560MB）。
- csproj 的 `StripPdbFromPublish` 自动删掉两个巨型 pdb：127MB → 27MB。**别删这个 target。**
- 发布目标被正在运行的程序锁住时会重试 10 次然后失败——先关程序。

---

# 第二部分 · 已知 Bug

## 15.1 ⚠️ 休息起点落在过去，短任务下休息被吃光（2026-08-02 发现，最高优先级）

**现象**：17:05 的计时点发现专注达成，但休息像是从 **17:04** 开始的。

**根因**（已核实代码，机制确凿）：

1. `FocusCompletedAt()` 每次都**从头重新推导**跨过阈值的那一格，返回该格的结束边界。
2. 每个计时点用 `[now-4min, now)` **重写最近 4 分钟**的 buffer。
3. AW 的窗口事件滞后 `now` 约 6~12 秒（T3）。所以在 17:04 那个计时点，
   `[17:03, 17:04)` 这一格的末尾几秒还是 `Init`（不计入），累计**差一点**没到目标；
4. 到 17:05 计时点重查时，那几秒被补成 `Focused` → 累计跨过阈值的位置**回退**到
   `[17:03, 17:04)` 这一格 → `FocusCompletedAt()` 返回 **17:04:00**，比发现时刻早一整分钟。

**后果被时长改小放大了**：`RestMinutes = ⌊focus/5⌋ + 1`，focus 3~4 分钟时休息只有
**1 分钟**。起点回退 1 分钟 → `now >= done + rest` 在发现的当拍就成立 →
**休息实际为 0**，扇形一闪而过甚至根本看不见。

旧设计里这一分钟回退本来是被 `+1` 补偿掉的（发现延迟上界 = 计时点间隔 60 秒，
补 1 分钟保证实歇不少于名义）。但那条不变量的前提是名义休息 ≥ 1 分钟**且**回退 ≤ 1 分钟
——当名义休息正好 = 1 分钟时，补偿被吃得一干二净。

**修复方向（三选一，待定）**：

| | 做法 | 评价 |
|---|---|---|
| A | `_focusDoneAt = Max(FocusCompletedAt(), now)` | 一行，保证休息不被追溯消费 |
| B | 休息一律从发现时刻起算（`_focusDoneAt = now`） | 最简；`+1` 补偿随之可去掉，但 focus<5 时 `⌊focus/5⌋=0`，得另定下限 |
| C | 分开两个概念：`FocusCompletedAt` 只用于画圆弧末端（历史事实），休息起点用发现时刻（用户权益，不可被追溯消费） | 语义最正 |

**建议**：按 C 的语义、用 A 的实现——一行代码，且保留圆弧末端的历史真实。
顺带复查 `RestMinutes` 公式在 3~10 分钟量程下是否还合理。

## 15.2 Give Up 后按钮不恢复 "Start"

**现象**：点 Give Up 确认 → 表盘清空 → 按钮仍显示 "Give up" 且灰色不可点；
点一下 Radio 才恢复。

**日志**：`The control RadioButton already has a visual parent DockPanel while trying
to add it as a child of DockPanel.`

**根因**：`RefreshGoalItems()` 每次把同一个 RadioButton 实例塞进**新建的** DockPanel，
而它已有旧的视觉父。Avalonia 不允许一个控件有两个视觉父 → 抛异常 → `EndSession`
后续代码（含 `RefreshStartButton()`）被整段跳过。

**试过但没生效**：先置 `_session = null` 再 `Dispose`、防重入锁、`InvalidateVisual()`、
加入前先从旧父 `Remove`。

**建议方向**：别再反复重建父容器。RadioButton 和 TextBlock 在 `LoadRules` 中**一次性**
创建并放进 DockPanel，`RefreshGoalItems` 只更新 TextBlock 的 `Text`，不重建 `ItemsSource`。

## 15.3 `duringByGroup` 未持久化到 settings.json

很可能是 15.2 那个异常导致 `EndSession` 中途退出，`_settings.Save()` 没执行。
**修好 15.2 后应自动解决**——需复验。

## 15.4 三处遗留的不一致（低优先级，顺手记下）

- `GoalGroup.AccumulatedSeconds` 还在类型模型里，但 `Accumulate` 删除后**没有任何代码
  读写它**，成了死字段，且与 `Settings.DuringByGroup` 语义重复。
- `executeCommand` 不在 `RulesFile` 类型模型里，`Shutdown.cs` 用裸 `JsonDocument`
  另读一遍——同一个文件两条读取路径。
- `MainWindow.axaml` 的 `Slider Value="25"` 超出了 `Minimum=3 Maximum=10`（会被
  静默钳到 10）。
- `Shutdown.cs` 按 ISSUE_FIX 的说法应改名 `Command.cs`，实际没改名。

---

# 第三部分 · 待办需求

> 按处理顺序排。**单实例限制排在最后**（用户 2026-08-02 指定）。

## 16.1 骨牌左侧亮面宽度随倒下数量递减（原 ISSUE #12）

**第一步先回答**：说明 `DominoRow` 现在是怎么绘制左侧亮面的（宽度从哪来、为什么固定）。

**需求**：不再让最左边骨牌的亮面宽度固定，而是**随着倒下的骨牌增多逐渐减小**。

## 16.2 为什么全是矢量绘制而不用图片 / emoji（原 ISSUE #3，待答）

用户的质疑成立且值得重新评估：程序尺寸固定、不会被拉伸变形，表盘和骨牌就那么几个
固定画面，为何不直接用图片资源？

**需要整理回答**：现有纪律（仓库不放位图）的由来——跨平台渲染一致、零外部美术依赖、
图标字体在 macOS 上是豆腐块、几何参数可计算可微调。以及代价——代码量、可读性。
**结论待定**：若确认改用预生成资源，是整体改还是只改静态部分（如骨牌）。

## 16.3 启动 / 退出时的 AW 查询能否移除（原 ISSUE #10）

目标是精简逻辑、减少误操作。**第一步**：逐处列出当前启动与退出路径上还剩哪些 AW 访问
（注意 2026-07-31 已删掉 `CheckAwAsync` / `RefreshTomatoesAsync`，可能已所剩无几），
再逐条讨论去留。

## 16.4 单实例限制（原 ISSUE #12 的另一条，**放到最后**）

方案已设计：`Mutex` + Windows `FindWindow`/`SetForegroundWindow`——第二个实例启动时
把已有窗口激活到前台然后自己退出。用户要求**放在所有需求之后**，也可能不做。

---

# 附录 · 2026-07-31 那一轮的实施记录（存档）

| 需求 | 落点 |
|---|---|
| #1 音频重叠 | `Tick.cs` 加 `SND_NOSTOP`，通道占用则跳过本次滴答 |
| #2 闹钟重构 | Settings 删 Alarm 开关改 Command 卡；黄针粒度 5→1 分钟 + 滚轮加速；`Restore` 不激活、新增 `Activate` |
| #4 Settings 尺寸 | `Width` 460→380（等于主窗口） |
| #5 Force Ticking | Settings 卡 5；Force on → 喇叭图标隐藏、滴答强制开；Start 到休息结束静音 |
| #6 删除退化路径 | 删 `AppMode` / `TomatoIcon` / `IconExport` / `PomodoroFallbackTests`；删 `Neutral` / `SelfApps` / `ignore` / `TodayTomatoes` / `Accumulate`；`Groups`→`Group`；多选→Radio 单选 |
| #7 判定模型重写 | 新增 `JudgmentBuffer` + `Judgment`；引擎从 `Replay.Run` 切到 buffer；AW 查询改 4 分钟固定窗口；新增 `itami bench` |
| #8 Shutdown→Command | 读 `rules.json` 的 `executeCommand.{os}`；Execute 与提示音互斥 |
| #9 累计时间替换番茄 | `Settings.DuringByGroup` 按 goal 存秒数；UI 右对齐显示 `during/3600`（两位小数，无单位） |
| #11 休息时圆弧保留 | 专注完成后圆弧不再清空；扇形先画、圆弧后画 |

**需求变更对照**：黄针 5→1 分钟/格；Command 提示音由跟随改互斥；专注时长 10-50→3-10 分钟；
多选→单选；`AlarmEnabled` 删除（黄针即开关）；Shutdown 硬编码→`executeCommand`；
Pomodoro 退化模式→永远约束模式；`Replay` 全量重放→`JudgmentBuffer` 4 分钟窗口。
