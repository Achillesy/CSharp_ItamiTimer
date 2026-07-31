# ISSUE 修复方案及实施记录

> 2026-07-31 会话。基于 ISSUE.md 的 12 条问题，逐条讨论设计方案并实施。

---

## 已完成（已验证通过）

### #1 音频重叠

Tick 加 `SND_NOSTOP`（Windows），通道被占用时跳过本次滴答。Alarm/通知优先。

**文件**：`Tick.cs`（+2 行）——`SND_NOSTOP = 0x0010` + `PlaySoundMem` 加标志。

### #2 闹钟设计修改

- **2.1** Settings 删 Alarm on/off，卡 4 改为 Command（Execute Toggle + Sound 下拉）
- **2.2** 预听声音被 Ticking 中断 → 随 #1 修复
- **2.3** Shutdown 每次启动复位 → E8 已有
- **2.4** 滚轮拨黄针 = 设闹钟，删除 `AlarmEnabled` 变量
- **2.5** 退出只记黄针位置（`FireAt`），时间点推导 → E7 已有
- **2.6** 启动时黄针可见但不激活（`Restore` 始终 `_fired = true`），滚轮拨针或开 Execute 才 `Activate(now)`

**优化**：黄针粒度 5→1 分钟，滚轮加速（1-2 格 1× / 3-5 格 2× / 6-10 格 3× / 11+ 格 5×）

### #4 Settings 窗口尺寸

`SettingsWindow.axaml`：`Width` 460→380（等于主窗口）

### #5 Force Ticking

- Settings 卡片 5：`Force Ticking` Toggle（持久化）+ Tick Volume 滑块（保留）
- Force on → 主界面喇叭图标隐藏、滴答强制开
- Start 到休息结束期间静音（不管 Force 状态）
- Settings 关闭时 `ApplyChrome()` 刷新图标

### #6 删除 Pomodoro 退化路径

**删除文件**：`AppMode.cs`、`TomatoIcon.cs`、`IconExport.cs`、`PomodoroFallbackTests.cs`

**删除概念**：
- `IntervalKind.Neutral`、`GroupRules.SelfApps/Empty/_ignore/TodayTomatoes/Accumulate`
- `TaskRecord.Groups`(IReadOnlyList) → `Group`(string?)、`GroupChanges`
- `TaskSession.SyntheticSpan/_pomodoro/AppMode`
- `MainWindow.CheckAwAsync/ApplyMode/RefreshTomatoesAsync/AccumulateToRules`
- `Program.cs --export-icon/--export-iconset`
- 清理根目录 `bin/obj` 及 6 个 `net10.0-windows` 残留目录

**Radio 单选**：CheckBox → RadioButton 组，Start 后锁定不可改，选中项持久化到 `settings.json`（`selectedGroup`）

### #7 判定模型重写

**新增**：`JudgmentBuffer.cs`（7380 buffer，180 padding + 7200 绘制区，6 状态码，2h 滚动归档）、`Judgment.cs`（纯函数逐秒分类）

**引擎替换**：`TaskSession` 从 `Replay.Run` 切换到 `JudgmentBuffer` + `Judgment.ClassifySeconds`。AW 查询从全量 `[startedAt, now)` 改为固定 `[now-4min, now)` 窗口。渲染层（`DialControl`、`RingIcon`）零改动——仍消费 `MinuteCell`。

**CLI bench**：`itami bench --minutes N --pattern focused|mixed|slack`，合成数据干跑验证。

### #8 Shutdown → Command

`Shutdown.cs` → `Command.cs`：读 `rules.json` 的 `executeCommand.{windows|macos}`，`Process.Start` 执行。Execute 与提示音互斥：Execute on → 执行命令、音色变灰；Execute off → 播放提示音、可选音色。

### #9 累计时间替换番茄

- `Settings.DuringByGroup`（`Dictionary<string,double>`）——按 goal 持久化累计秒数
- UI：每个 radio 同行右对齐显示 `during/3600`（两位小数，无单位）
- 任务结束时 `_buffer.DuringSeconds` 落盘

### #11 休息时圆弧保留

专注完成后圆弧不消失（`Cells = cells` 而非 `_buffer.IsFocusComplete ? [] : cells`）。`DrawRestWedge`（扇形）在 `DrawRing`（圆弧）之前绘制——z-order 天然正确。

---

## 需求变更记录

| 原设计 | 变更后 |
|--------|--------|
| 黄针 5 分钟/格 | 1 分钟/格 + 滚轮加速 |
| Command 提示音跟随 Toggle | Execute on→执行命令 off→响铃，互斥 |
| 专注时长 10-50 分钟 | 3-10 分钟（测试便利） |
| multi-select goals | Radio 单选 |
| AlarmEnabled 独立开关 | 黄针即闹钟开关 |
| Shutdown 硬编码关机 | rules.json executeCommand |
| Pomodoro 退化模式 | 永远约束模式 |
| Replay 全量重放 | JudgmentBuffer 4 分钟窗口 |

---

## 已知 Bug（未修复）

### 1. Give Up 后按钮不恢复 "Start"

**现象**：点击 Give Up 确认放弃 → 表盘清空 → 按钮仍显示 "Give up" 且灰色不可点。点击 Radio 后按钮恢复正常。

**日志错误**：
```
The control RadioButton already has a visual parent DockPanel
while trying to add it as a child of DockPanel.
```

**根因分析**：`RefreshGoalItems()` 每次将同一个 RadioButton 对象加入新创建的 DockPanel，但 RadioButton 已有旧父控件。Avalonia 不允许控件同时有两个视觉父。异常导致 `EndSession` 后续代码（包括 `RefreshStartButton()`）被跳过，按钮状态未更新。

**尝试过的修复**（未生效）：
1. `EndSession` 中先设 `_session = null` 再 `Dispose()`——防 `Dispose` 异常导致 null 赋值不执行
2. `AskAbandonAsync` 加防重入锁 `_abandoning`
3. `RefreshStartButton` 加 `btn.InvalidateVisual()`
4. `RefreshGoalItems` 中加入前先从旧父移除：`oldP.Children.Remove(radio)`

**建议方向**：RadioButton 不应在 `RefreshGoalItems` 中反复重建父容器。改为：RadioButton 和 TextBlock 在 `LoadRules` 中一次性创建并放入 DockPanel，`RefreshGoalItems` 只更新 TextBlock 的 `Text` 属性，不重建 ItemsSource。

### 2. duringByGroup 未持久化到 settings.json

可能因上述异常导致 `EndSession` 中途退出，`_settings.Save()` 未被执行。修好 Bug 1 后应自动解决。

### 3. 未完成项

- **#12** 单实例限制：方案已设计（Mutex + Windows FindWindow/SetForegroundWindow），用户要求放到最后或不做。

---

## 涉及文件清单

| 文件 | 变更类型 |
|------|----------|
| `src/ItamiTimer.App/Platform/Tick.cs` | 改：+SND_NOSTOP |
| `src/ItamiTimer.App/Platform/Shutdown.cs` | 重写：→Command.cs |
| `src/ItamiTimer.App/AlarmClock.cs` | 改：SlotMinutes=1, Restore 不激活, +Activate |
| `src/ItamiTimer.App/Settings.cs` | 改：CommandEnabled/Sound, ForceTicking, SelectedGroup, DuringByGroup |
| `src/ItamiTimer.App/SettingsWindow.axaml` | 改：卡片 4/5 布局, Width=380 |
| `src/ItamiTimer.App/SettingsWindow.axaml.cs` | 改：ExecuteOn/ForceOn 接线 |
| `src/ItamiTimer.App/MainWindow.axaml` | 改：Slider 范围, ItemsControl 样式 |
| `src/ItamiTimer.App/MainWindow.axaml.cs` | 改：RadioButton/CheckBox, AppMode 删除, Force Ticking, during 显示, Give up |
| `src/ItamiTimer.App/TaskSession.cs` | 重写：Replay→JudgmentBuffer 引擎 |
| `src/ItamiTimer.App/Program.cs` | 改：删除 icon 导出 |
| `src/ItamiTimer.App/AppMode.cs` | **删** |
| `src/ItamiTimer.App/Drawing/TomatoIcon.cs` | **删** |
| `src/ItamiTimer.App/Drawing/IconExport.cs` | **删** |
| `src/ItamiTimer.Core/IntervalKind.cs` | 改：删 Neutral |
| `src/ItamiTimer.Core/GroupRules.cs` | 重写：删 SelfApps/Empty/ignore/TodayTomatoes/Accumulate |
| `src/ItamiTimer.Core/TaskRecord.cs` | 改：Groups→Group(string?), 删 GroupChanges |
| `src/ItamiTimer.Core/Replay.cs` | 改：删 Neutral 引用（死代码，不再被调用） |
| `src/ItamiTimer.Core/TaskState.cs` | 改：注释 |
| `src/ItamiTimer.Core/MinuteCell.cs` | 改：注释 |
| `src/ItamiTimer.Core/JudgmentBuffer.cs` | **新增**：7380 buffer + ToMinuteCells + FocusCompletedAt |
| `src/ItamiTimer.Core/Judgment.cs` | **新增**：逐秒分类 |
| `src/ItamiTimer.Cli/Program.cs` | 改：bench 命令, 单 group |
| `src/ItamiTimer.Cli/Renderer.cs` | 改：BufferSummary, 删 Neutral 引用 |
| `tests/.../PomodoroFallbackTests.cs` | **删** |
| `tests/.../AlarmClockTests.cs` | 改：Restore 不激活 |
| `tests/.../GroupRulesTests.cs` | 改：删 Neutral/SelfApps 测试 |
| `tests/.../ReplayTests.cs` | 改：单 group |
| `tests/.../BoundaryTests.cs` | 改：删 TodayTomatoes 测试, 单 group |
| `tests/.../TaskRecordTests.cs` | 改：单 group |
| `tests/.../其余测试文件` | 改：Groups→Group |
| `ISSUE_FIX.md` | **新增** |
