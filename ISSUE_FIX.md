# ISSUE 修复方案

> 基于 ISSUE.md 的 11 条问题，逐条讨论后形成的完整设计方案。
> 讨论日期：2026-07-31。

---

## 1. 音频重叠

**问题**：Ticking 播放时，其它声音被中断。

**方案**：Tick 加 `SND_NOSTOP`（Windows），通道被占用时跳过本次 tick（35ms 的滴答少一两声听不出来）。
Alarm / 通知声音优先，直接播。macOS 上 `AudioServicesPlaySystemSound` 天然支持多声叠加，
Tick 和 Alarm 用不同 `SystemSoundID`。

**流程**（15:40 闹钟为例）：
```
15:40:00 → Tick.Play with SND_NOSTOP → 通道空闲 → 正常播 tick
         → Alarm 触发 → Sound.Play → 中断 tick（已响 1-2ms，听不出来）
15:40:01 → Tick.Play with SND_NOSTOP → 闹钟还在播 → 跳过
...闹钟播完...
15:40:05 → Tick.Play with SND_NOSTOP → 通道空闲 → 正常播
```

## 2. 闹钟设计修改

**2.1** — Settings UI 移除 Alarm on/off 开关，只保留 Command 卡片的 Execute toggle + Sound 下拉。
闹钟本身由黄针控制，不需要独立开关。

**2.2** — 同 #1 的 bug。等 #1 修复后自动解决。

**2.3** — Shutdown（现 Command Execute）每次启动重置为 off。**已实现**（E8：`Settings.Load` 强制复位）。

**2.4** — 滚轮改变黄针位置 = 设定闹钟时间（原 Alarm 开）。删除 `AlarmEnabled` 布尔变量。

**2.5** — 退出时只持久化黄针位置（`FireAt`），时间点是推导值。**已实现**（E7：`AlarmClock.Position` 是计算属性）。

**2.6** — 启动时加载 `FireAt` 显示黄针，但不激活闹钟（不计算 `NextRing`）。
用户滚动滚轮或开启 Execute 时才首次计算闹钟时间。

## 3. 为什么画图不用 icon/emoji

- 钟面是动态渲染的（指针位置、木桶短板高度、彩色圆弧纯度渐变、灰色截止弧螺旋、蓝色休息扇形）
- 骨牌理论上可用 emoji（🎲），但跨平台渲染不一致（Windows Segoe UI Emoji vs macOS Apple Color Emoji）
- Segoe Fluent Icons 在 macOS 是豆腐块（已全部换成手绘矢量，D5）
- 程序大小固定 → 手绘矢量无缩放模糊、无需多套素材
- D5 规定仓库不放位图，所有图形运行时绘制或合成

## 4. Settings 窗口尺寸

Settings 窗口宽度 ≤ 主窗口宽度（380px）。如果高度不够则宽度略小。
当前 Settings = 460px、MainWindow = 380px，需调整。

## 5. Force Ticking 开关

**改动**：
- Tick Volume 滑块保留，功能完全不变
- 滑块上方新增 `Force Ticking` Toggle（格式和其它 Setting 卡片一致）
- Force on → 主界面 Ticking 图标隐藏，滴答不可手动关闭
- Start 后直到休息结束期间不播滴答（不管 Force 状态）
- Force Ticking Toggle **状态持久化**
- 用户先手动开 Ticking → Start → 进 Settings 开 Force → Force 优先（覆盖按钮状态）
- C4 规则不变：喇叭只管滴答，通知声音开关在 Settings 里独立控制

## 6. 启动不随 AW 退化

**推翻 B3**（模式启动锁死）。永远进约束模式，AW 断连由判定模型的状态 3（AW 脱机 = 默认专注）处理。

**删除**：`AppMode` 枚举、`SyntheticSpan`、`CheckAwAsync`、`TomatoIcon`、`TodayTomatoes`、
整个 Pomodoro 退化路径。

用户确认接受「杀 aw-server 免费刷时长」的作弊路径——和旧 Pomodoro 模式全绿效果等价。

## 7. 判定模型重写

### 状态码

| 值 | 含义 | 圆弧颜色 |
|----|------|----------|
| 0 | 初始化（分配空间，数据未到达） | 不画 |
| 1 | 灰色（预计任务时间） | 灰色圆弧 |
| 2 | 专注时间 | 绿色（按纯度渐变） |
| 3 | AW 脱机（默认算专注） | 同 2 |
| 4 | AFK 离线 | 空白（不画，和 D3 一致） |
| 5 | 非专注 | 红色圆弧 |

### Buffer

```
int[7380]

  [0..180)          [180..7380)
  ← 3 min padding → ← 7200 秒绘制区 →
                      初始: 1（灰色）× (FocusMinutes + 3) × 60
                      其余: 0（初始化）
```

### 数据流

```
任务开始 09:01:25
  ↓ 整分钟截断（A6）
09:01:00

第一次查询 09:02:00
  取 [08:58:00, 09:02:00) 共 240s → 打标 → 覆盖 buffer[0..240)
  [0..180) = padding（任务前数据，不绘制）
  [180..240) = 任务第 1 分钟

每分钟查询
  取 [now - 4min, now) 240s → 打标 → 覆盖对应偏移
  前一分钟的旧值被覆写（AW 数据 3 分钟内不稳定——接受）

Focus 完成
  停止查询 AW
  Buffer 保持，只画圆弧
  休息扇形叠在圆弧下层（蓝色，D4）

任务结束（Ignore / 关程序 / 休息结束）
  during 落盘（从 buffer 统计状态 2+3 秒数）
  最坏损失: 3 分钟未 finalize 数据
  Buffer 清空
```

### 2 小时滚动归档

```
elapsed > 7200s:

  [0..3600)      [3600..7380)
  ← 归档 →       ← 保留 →

  归档: [0..3600) → 统计状态 2+3 秒数 → during 落盘
  左移: [3600..7380) → [0..3780)
  新空间: [3780..7380)

  左移后:
    [0..180)    ← 旧 [3600..3780)，含未 finalize 数据（容忍）
    [180..3780) ← 旧 [3780..7380)
    [3780..7380) ← 新写入空间

  StartedAt' = StartedAt + 3600s（整小时）
  FocusSeconds' = FocusSeconds - archivedFocusedSeconds
```

### 完成判定

```
accumulatedFocusedSeconds >= FocusSeconds（FocusSeconds = FocusMinutes × 60）
整分钟边界判断，不会在某分钟内完成
during 以秒为单位落盘
```

### 分类逻辑

```
匹配唯一选中 group → 状态 2（专注）
不匹配              → 状态 5（非专注）
afk 事件            → 状态 4（AFK 离线，优先级最高）
AW 连不上           → 状态 3（AW 脱机）

无 Neutral、无自身豁免、无 ignore
```

## 8. Shutdown → Command

**改动**：
- `ShutdownEnabled` → `ExecuteEnabled`（Settings 模型）
- `rules.json` 新增 `executeCommand` 字段：`{ "windows": "...", "macos": "..." }`
- 程序自动 `RuntimeInformation.IsOSPlatform` 选对应命令
- `Process.Start` 执行
- Execute Toggle **不持久化**（启动复位，延续 E8）
- Settings 卡片标题：**Command**，包含 Toggle `Execute` + Sound 下拉

**默认 rules.json**：
```json
{
  "groups": [
    {
      "name": "番茄钟",
      "apps": [".*"]
    }
  ],
  "executeCommand": {
    "windows": "shutdown /s /t 0",
    "macos": "osascript -e 'tell app \"System Events\" to shut down'"
  }
}
```

## 9. 累计时间替换番茄绘制

**改动**：
- 删除 `TomatoIcon`、`TodayTomatoes`
- 任务列表每项后面显示 `during / 3600`，保留两位小数，右对齐，无单位
- `during` 以**秒**为单位落盘（B1 放宽：允许 during 持久化）
- 所有 goal 的 during 全部显示（不只是当前选中的）
- 数据源：归档时从 buffer 统计的专注秒数（每 2 小时 + 任务结束时更新）

## 10. AW 查询精简

**改动后**（做完 #6 + #7 + #9）：

| 查询 | 结果 |
|------|------|
| `ProbeAsync` / bucket 验证 | **删除**（#6） |
| `TodayTomatoes`（全日查询） | **删除**（#9） |
| 启动时 AW 探测 | **删除**（#6） |
| 每 tick 全量重查 | 改为每分钟一次 4 分钟窗口查询（大幅精简） |
| 结束后 AW 查询 | **删除**（#9） |

## 11. 可变长专注空间

**方案**：
- 7380 buffer 不关程序不清除，圆弧一直可见
- 休息扇形先画（底层），圆弧画在上层，超过 60 分钟覆盖扇形——正确显示
- 休息结束 → 清空 buffer + 扇形，准备下一次 Start
- 新 Start → 清空 buffer
- Radio 按钮：Start 后全部灰色，不可改选

---

## Settings UI 布局（5 张卡片）

| # | 标题 | 控件 |
|---|------|------|
| 1 | **Focus Complete** | Toggle + Sound 下拉 |
| 2 | **Break Over** | Toggle + Sound 下拉 |
| 3 | **Idle Warning** | Toggle + Sound 下拉 |
| 4 | **Command** | Toggle `Execute` + Sound 下拉 |
| 5 | **Force Ticking** | Toggle `Force` on/off + Tick Volume 滑块 |

- Force Ticking Toggle：**持久化**
- Execute Toggle：**不持久化**（每次启动复位）
- Tick Volume 滑块：保留，功能完全不变

---

## 删除清单

| 删除项 | 原因 |
|--------|------|
| `AppMode` 枚举 | #6：不再退化 |
| `SyntheticSpan` | #6：不再合成番茄钟事件 |
| `CheckAwAsync` | #6：启动不探测 AW |
| `TomatoIcon` | #9：不再画番茄矢量图 |
| `TodayTomatoes` | #9：不再统计 🍅 数 |
| `AlarmEnabled` | #2：闹钟 = 黄针，不需要开关 |
| `ShutdownEnabled` | #8：改为 ExecuteEnabled |
| `ignore`（rules.json） | #7：去掉 Neutral 分类 |
| 自身豁免（`SelfApps`） | #7：秒级精度下几秒看表盘几乎无影响 |

---

## 依赖拓扑

```
#2.1 + #4     第 1 批（独立，并行）
#2.4 → #2.6   第 2 批（闹钟链）
#5 + #8       第 2 批（独立）
#6 → #7 → #9 + #11  第 3 批（核心重构链，严格串行）
#1 → #2.2     第 4 批（音频）
```

---

## 12. 单实例限制

**问题**：当前允许多个实例同时运行。两个实例各自查询 AW、各自计时，
闹钟可能响两次、Shutdown 可能触发两次——行为不可预测。

**方案**：启动时拿一个全局命名 Mutex，拿不到说明已有实例在跑，
激活已有实例的窗口后退出。

### 实现（跨平台，一行 P/Invoke 都不用）

```csharp
// Program.cs / App.axaml.cs 启动入口
const string MutexName = "Global\\ItamiTimer_SingleInstance_v1";

using var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
if (!createdNew)
{
    // 已有实例在跑 → 把它拉到前台
    ActivateExistingWindow();
    return 0;
}

// 正常启动
BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
```

### 激活已有实例

**Windows**：`FindWindow` + `SetForegroundWindow`

```csharp
[DllImport("user32.dll")]
static extern IntPtr FindWindow(string? className, string windowName);

[DllImport("user32.dll")]
static extern bool SetForegroundWindow(IntPtr hWnd);

[DllImport("user32.dll")]
static extern bool ShowWindow(IntPtr hWnd, int cmdShow);

static void ActivateExistingWindow()
{
    var hWnd = FindWindow(null, "ItamiTimer");
    if (hWnd == IntPtr.Zero) return;
    ShowWindow(hWnd, 9);        // SW_RESTORE（从最小化恢复）
    SetForegroundWindow(hWnd);  // 拉到前台
}
```

**macOS**：用 `NSRunningApplication` 或 `osascript`。但 Avalonia 没有内置的
macOS 窗口查找 API。简化处理——macOS 上检测到已有实例时，**直接退出并打印
一条日志**，不做窗口激活。macOS 有 Dock 指示器，用户自己点一下就行。

### 关键决策

- **Mutex 名字不随版本变**：同一个程序，不管哪个版本，同一时间只跑一个。
  用 `Global\` 前缀覆盖所有用户会话（Windows 服务场景用不到，但无害）。
- **macOS 不做窗口激活**：`FindWindow` 是 Windows-only，macOS 侧的
  `NSRunningApplication` 需要额外 P/Invoke 或引入 AppKit 绑定——代价大于收益。
- **Mutex 在进程退出时自动释放**：`using` 块保证即使崩溃也由 OS 回收。
- **不影响 CLI**：`itami bench` / `itami start` 不需要单实例限制——它们
  不和 GUI 共享状态（CLI 不写 settings.json、不碰闹钟）。

### 改动范围

| 文件 | 改动 |
|------|------|
| `App.axaml.cs` 或 `Program.cs` | 启动入口加 Mutex 检查 + Windows 窗口激活 |
| `Platform/` | 新增 `SingleInstance.cs` 收口平台差异 |

### 难度

**Tier 1**（简单，单文件，~40 行）。

---

## 验证优先级

1. **音频**：验证 `SND_NOSTOP` 在 Windows 上不中断其它声音，macOS 上双 `SystemSoundID` 叠加正常
2. **新计时方法**：在 CLI 下验证 7380 buffer、4 分钟窗口查询、打标、绘图逻辑
