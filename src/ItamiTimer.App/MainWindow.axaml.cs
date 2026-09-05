using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using System.Linq;
using System.Reflection;
using ItamiTimer.Core;
using ItamiTimer;

namespace ItamiTimer.App;

/// <summary>
/// Main window.
///
/// **This layer only renders the <see cref="MinuteCell"/> list and collects the user's
/// submission** — judgment and accounting live entirely in Core, the tick lives in
/// <see cref="TaskSession"/>.
///
/// Layout: **the "Start" button is the dividing line itself** — above it is the dial and
/// the dominoes, for the eyes; below it are the value controls and the goal list, for the
/// hand. Nothing below the divider ever explains itself (§8.6); the reasons for a failure
/// only go to the log (§8.1a).
///
/// Visible states:
/// <code>
/// Idle                  An empty dial is the invitation for the next round
/// In progress           The window stays put (user, 2026-07-27); the coloured cells grow by one per minute
/// No input for 60s      One beep, to wake you up before ActivityWatch marks you afk (C3)
/// Focus completed       One beep, into rest. No report — the dial speaks for itself (B4)
/// Resting                A blue wedge; the minute hand sweeps it away when it's over
/// Rest over             One beep, back to the empty dial, waiting for the user (B2: never auto-starts the next round)
/// </code>
///
/// ⚠️ This table has **no "pin / pop up / minimize to tray" anywhere** — the whole
/// auto-pinning scheme was abandoned on 2026-07-28 (C1: "pop up" and "never steal focus"
/// contradict each other by definition). The pin icon in the top-right corner is a manual
/// toggle; it isn't the same mechanism as the one that got cut.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>The second hand needs sub-second continuous repaint (§8.2.6). **Only runs while the window is visible** — stops when minimized to the tray, a free power saving.</summary>
    private readonly DispatcherTimer _frame = new() { Interval = TimeSpan.FromMilliseconds(33) };

    private GroupRules? _rules;
    private readonly List<RadioButton> _goalRadios = [];
    private readonly List<TextBlock> _duringLabels = [];
    private readonly Settings _settings = Settings.Load();

    /// <summary>每个小目标的累计专注时间 + 算到了哪一刻（§11.2）。启动读入，<b>只在任务启动那一刻被回填改写一次</b>。</summary>
    private readonly During _during = During.Load();

    private TaskSession? _session;

    // Alarm: the whole model lives in AlarmClock (testable pure logic); this is just the
    // scroll-wheel wiring. Left/right click was removed (2026-07-30, user: "so it doesn't
    // box in future features") — the wheel is the only input.
    private readonly AlarmClock _alarm = new();
    // Suppress spurious triggers during adjustment. Uses a "quiet-until deadline" rather
    // than a bool: the wheel produces a continuous stream of discrete events, and a
    // bool + Task.Delay reset would race itself (an earlier reset cancelling a later
    // adjustment window).
    private DateTime _alarmQuietUntil = DateTime.MinValue;

    // How many times the alarm rings when it fires (user, 2026-08-03). A single system
    // sound is one or two seconds long — short enough to lose to a passing thought, and
    // the alarm is the one sound with no second chance: the three notification sounds are
    // tied to a state that's still on screen afterward, while the alarm leaves nothing
    // behind (the yellow hand doesn't move, nothing pops up — §8.1a: the program never
    // interrupts).
    //
    // ⚠️ This is **not** DECISIONS E5's "daily repeating alarm": still one firing, still
    // one-shot, MarkFired above still runs before the first ring. Only the ringing itself
    // got longer.
    private const int AlarmRings = 4;

    // The three notification sounds ring twice (user, 2026-08-03) — the same reasoning as
    // above, weaker: each of them lands on something that stays on screen afterward (a
    // finished dial, a blue wedge, cells that stopped growing), so a missed beep can still
    // be recovered by looking. Two is enough to say "that was for you", four would turn
    // every completed pomodoro into an event.
    //
    // Fewer than the alarm on purpose, and the gap is what enforces it: these fire off the
    // whole-minute tick, and Idle can fire on two consecutive minutes — so a sequence has
    // to comfortably finish inside a minute even with the longest system sound picked
    // (Ring05 is 12.8s: twice is 26s, four times would overrun into the next nudge).
    private const int NotifyRings = 2;

    // Alarms 清单（DESIGN §17）：响铃遍数跟三声通知同一个理由——响完之后系统通知留在
    // 通知中心里，属于"留了痕迹回头能看"的一类，不需要闹钟那种"唯一没有第二次机会"的
    // 待遇，所以是 2 遍不是 4 遍。跟 NotifyRings 数值恰好相同但理由各自独立，故意不共用
    // 同一个常量。
    private const int AlarmsListRings = 2;

    /// <summary>
    /// 提示条最多列几条（3.7.0；3.8.0 起随尺寸档变）。**算出来的硬上限，不是保守取值**
    /// ——提示条跟骨牌叠在同一个 <c>Auto</c> 高的 Grid 格子里，多一行就把那一行撑高、
    /// 把下面的卡片顶下去，而那个负边距是按骨牌高度死算的（DECISIONS K16）。
    ///
    /// 标准档 2 条、紧凑档 1 条，跟 <c>TextBlock.MaxLines</c> 取同一个值（`ShowAlarmBanner`
    /// 把这几条用换行拼成一段，两者不一致就会截在半路）。预算见
    /// <see cref="WindowLayout.Compact"/> 的注释。
    /// </summary>
    private static int BannerMaxLines => WindowLayout.Current.BannerMaxLines;

    /// <summary>
    /// Alarms 清单的去重水位线：(after, now] 区间内到点的条目才算"新到点"（见
    /// <see cref="AlarmsList.Due"/>）。**纯内存，不持久化，初始化成启动那一刻而不是
    /// null**——已经定了"不补响、只看未来"，程序关闭期间错过的条目重新打开后直接跳过，
    /// 不需要跨会话记住"上次数到哪一分钟"，这跟闹钟要跨会话持久的 `_fired` 不是同一类
    /// 东西（DESIGN §17）。
    /// </summary>
    private DateTime _alarmsProcessedThrough;

    /// <summary>
    /// Alarms 清单提示条要显示到几点（2026-08-06）：程序自己在骨牌那块区域画一条提示，
    /// **刚好显示一分钟**（到点那一刻起到下一分钟整点为止），到点自动消失，不需要
    /// 用户手动关。这条**保证屏幕上一定看得见**——跟 <see cref="Notify"/> 弹的系统
    /// 通知并存，不是二选一：系统通知那条一度因为用错 AppId 被误判成"走不通"整个
    /// 放弃过，后来发现只是那个字符串没注册、横幅还被"请勿打扰"吞掉，通知其实躺在
    /// 通知中心里（见 <see cref="Notify"/> 的注释、DECISIONS J13）。提示条不依赖这些
    /// Windows 侧的不确定性，所以留着。
    /// </summary>
    private DateTime? _alarmBannerHideAt;

    public MainWindow()
    {
        Log.Info($"Started. Log: {Log.Path_}");

        InitializeComponent();
        ApplyLayout();
        ApplyTheme();

        // 这里原来给 Window.Icon 赋一张实时重绘的进度环（RingIcon）。2026-08-10 整块删掉：
        // 它能显示的地方一个不剩了——任务栏从来就没认过它（Avalonia 只发小尺寸 HICON，
        // D10），标题栏在 2.0.1 改成无边框时消失（659f06a）。不设 Window.Icon，两个平台
        // 都退回可执行文件/.app 自带的静态番茄图标，正是 D10 定下的那个状态（DECISIONS D11）。
        ApplySliderRange();
        LoadRules();
        RefreshStartButton();

        // Version, read from the single <Version> in Directory.Build.props at build time --
        // moved here from SettingsWindow (user, 2026-08-07) so the running build is visible
        // without opening Settings (DESIGN §14).
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        F<TextBlock>("VersionLabel").Text = version is null ? "" : $"v{version.ToString(3)}";

        // 齿轮和主题两个图标的 Content 由 ApplyChrome 统一画（跟喇叭、图钉一样跟着
        // 调色板走），这里只挂事件。
        F<Button>("SettingsBtn").Click += OnSettings;
        F<Button>("ThemeBtn").Click += (_, _) =>
        {
            _settings.DarkTheme = !_settings.DarkTheme;
            ApplyTheme();
            ApplyChrome();
            _settings.Save();
        };
        var dial = F<DialControl>("Dial");
        dial.PointerWheelChanged += OnAlarmWheel;
        _alarm.Restore(_settings.AlarmFireAt, DateTime.Now);
        dial.AlarmMinutes = _alarm.Position;
        // 水位线定在启动这一刻：程序关闭期间错过的条目直接跳过，不倒回去补（DESIGN §17）。
        _alarmsProcessedThrough = DateTime.Now;
        CheckAlarmsList(DateTime.Now);   // 立刻画一次红圈，不等第一次整分钟的心跳
        // Turning the tick off cuts off the one that's mid-play; turning it on doesn't touch
        // anything (the next second boundary starts it). Deliberately **not** in ApplyChrome
        // — see the note there.
        F<Button>("MuteBtn").Click += (_, _) =>
        {
            _settings.TickEnabled = !_settings.TickEnabled;
            if (!_settings.TickEnabled) Tick.Stop();
            ApplyChrome();
            _settings.Save();
        };
        F<Button>("PinBtn").Click += (_, _) => { _settings.Pinned = !_settings.Pinned; ApplyChrome(); _settings.Save(); };
        ApplyChrome();

        // 无边框之后窗口没有标题栏可拖了（DECISIONS K，改成整个主窗口无边框/透明，
        // 不是另开一扇挂件）。拖动生效范围**收窄到表盘**（用户 2026-08-08 明确要求：
        // "仅仅点在表盘区域拖动才有效"）——挂在 Dial 控件上而不是整个 Window。跟表盘
        // 上唯一的另一个手势 PointerWheelChanged（闹钟滚轮）不冲突，那是滚轮事件，
        // 这是按下事件，互不相关；钟面点击本来就留白（DECISIONS E3）。
        //
        // ⚠️ **实际命中范围是圆的，不是 Dial 的矩形 Bounds**（2026-08-08 用户实测，
        // 推翻了当初写这段代码时的假设，DECISIONS K10）：Avalonia 对自绘控件的命中
        // 测试是**按实际画出来的绘制操作逐个判定**的，不是按控件的矩形边界——所以
        // 表盘四个角（矩形范围内、但一笔都没画过的透明区域）拖不动，而圆盘外沿再往外
        // 一点点能拖，因为那儿画着表盘投在"墙上"的阴影（DialControl 的第一层）。
        // 这也正是 Panel/Border 想让整个矩形可点时必须显式写 Background="Transparent"
        // 的原因：那个背景本身就是一个绘制操作，没有它就没有可命中的东西。
        // 用户看过之后的结论是圆形范围**更好**，维持现状，不去 override HitTest 强行
        // 改成方的。
        dial.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

            // 先问红圈（2026-08-27，DESIGN §17.1）：这一下精确落在 Alarms 清单的小红圈
            // 上就当"瞄一眼下一条"处理，不再往下走拖窗口。跟拖拽共用同一次按下事件，
            // 非此即彼，不是叠加手势——精确点中红圈时窗口不会跟着这次按下走，松手前
            // 移动鼠标也不会拖动（跟旁边几个 Button 的"松开才算"不是一回事，这里在
            // 按下那一刻就已经分岔）。`OnAlarmsDotClicked` 返回 false（没有下一条可看，
            // 理论上不该发生，见其注释）时照旧退回拖窗口，行为不多不少跟没这个功能时
            // 完全一样。
            if (dial.HitTestAlarmsDot(e.GetPosition(dial)) && OnAlarmsDotClicked())
            {
                e.Handled = true;
                return;
            }

            BeginMoveDrag(e);
        };

        // 表盘右键菜单（用户 2026-08-08）：无边框之后没有标题栏、没有系统菜单，任务栏
        // 图标的右键菜单成了唯一能关窗口的地方——补一个跟它对齐的入口，就在拖动窗口
        // 的同一块区域上。只有"关闭窗口"一项，不做成一整套窗口菜单。
        //
        // 跟左键拖动挂在同一个控件上互不影响：ContextMenu 走的是右键，上面那个
        // PointerPressed 明确只认左键。
        //
        // **文字是英文**："界面文字英文"是这个项目一条既有硬约束（CLAUDE.md）；用户
        // 提过"要根据系统文字，如果不行，就统一为英文"，跟系统语言走需要引入一整套
        // 本地化资源机制（目前一行都没有），为一个菜单项不划算，按用户给的退路走英文。
        // 图标同样是矢量画的（ChromeIcons.Close），仓库不放位图（DECISIONS D5）。
        // 图标的墨色跟着主题走（v3.0.0）：右键菜单自己的背景是 Fluent 给的，变体一翻
        // 它也跟着翻，写死的深墨在暗色菜单上就看不见了。菜单只构造一次，所以把这一项
        // 记下来，换主题时由 ApplyChrome 重画它的图标。
        _closeItem = new MenuItem { Header = "Close window", Icon = ChromeIcons.Close(_palette) };
        var closeItem = _closeItem;
        // 走 Close() 而不是直接退进程：任务没结束时照样会弹"要放弃吗"的确认
        // （OnClosing，§9），跟点标题栏 ×、Alt+F4 完全同一条路径。
        closeItem.Click += (_, _) => Close();
        dial.ContextMenu = new ContextMenu { ItemsSource = new[] { closeItem } };

        RestoreWindowPosition();

        _frame.Tick += OnFrame;
        _frame.Start();
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            // 窗口关掉之后再让它跑一拍，Position/Screens 就是在一个已经没了的窗口上取值
            _settle.Stop();
            SaveAlarmOnExit();
        };
    }

    // ---------------------------------------------------------------- 窗口位置（DECISIONS K22/K23）

    /// <summary>
    /// 拖动停下来之后才做「拉回屏幕内 + 记住位置」这两件事的判定器。**故意不在
    /// <see cref="OnPositionChanged"/> 里当场做**，见 <see cref="ClampIntoScreen"/>
    /// 的注释：拖动过程中就往回拉会让窗口永远跨不过两块屏幕的交界。
    /// </summary>
    private readonly DispatcherTimer _settle = new() { Interval = TimeSpan.FromMilliseconds(250) };

    /// <summary>重入闸：<see cref="ClampIntoScreen"/> 自己写 <c>Position</c> 会再次触发 <see cref="OnPositionChanged"/>。</summary>
    private bool _clamping;

    /// <summary>
    /// 恢复上次的位置。没存过（首次运行）就什么都不做，让 axaml 里的
    /// <c>WindowStartupLocation="CenterScreen"</c> 照常居中。
    ///
    /// 有存过就切成 <c>Manual</c> 并在 <c>Opened</c> 里落位——**必须等到 Opened**：
    /// 原生窗口还没建出来时 <see cref="TopLevel.Screens"/> 拿不到可靠的屏幕信息，
    /// 而恢复完立刻要用它做一次 <see cref="ClampIntoScreen"/>（上次那块屏可能已经
    /// 拔掉了，见 <see cref="Settings.WindowX"/> 的注释）。
    /// </summary>
    private void RestoreWindowPosition()
    {
        PositionChanged += OnPositionChanged;
        _settle.Tick += OnPositionSettled;

        if (_settings.WindowX is not { } x || _settings.WindowY is not { } y) return;

        WindowStartupLocation = WindowStartupLocation.Manual;
        Opened += (_, _) =>
        {
            Position = new PixelPoint(x, y);
            ClampIntoScreen();
        };
    }

    /// <summary>拖动中每动一下都会来，只负责把「停下来」的计时器往后推。真正干活的是 <see cref="OnPositionSettled"/>。</summary>
    private void OnPositionChanged(object? sender, PixelPointEventArgs e)
    {
        if (_clamping) return;
        _settle.Stop();
        _settle.Start();
    }

    /// <summary>位置稳定 250ms（= 松手了）：拉回屏幕内，然后记住它。</summary>
    private void OnPositionSettled(object? sender, EventArgs e)
    {
        _settle.Stop();
        ClampIntoScreen();
        _settings.WindowX = Position.X;
        _settings.WindowY = Position.Y;
        _settings.Save();
    }

    /// <summary>
    /// 把窗口整个拉回屏幕可用区域内（用户 2026-08-08：向上拖出屏幕会被系统弹回来，
    /// 希望左/右/下也一样）。Windows 只在上边缘替我们做了这件事，另外三边得自己来。
    ///
    /// **拉回的目标是「窗口目前主要待在哪块屏」的工作区**（<c>ScreenFromWindow</c>，
    /// 工作区 = 扣掉任务栏之后的部分），不是所有屏幕拼起来的大矩形——多屏排布可能
    /// 不是一个完整矩形，拿外接矩形去判断会把两块屏之间的空洞也算成合法位置。
    ///
    /// ⚠️ **为什么必须等拖动停下来再拉，不能一边拖一边拉**：一边拖一边拉的话，窗口
    /// 永远会被摁在当前这块屏的边界内，就永远到不了「一半以上落在另一块屏上」那个
    /// 状态，而 <c>ScreenFromWindow</c> 正是按这个来判断该归哪块屏的——结果就是
    /// **窗口再也拖不到第二块显示器上去**。用户是双屏，这条不是理论风险。所以拖动
    /// 过程中随便它跨屏、出界，松手之后（<see cref="_settle"/> 计时器）再归位。
    ///
    /// 坐标单位要小心：<c>Position</c> 和 <c>Screen.WorkingArea</c> 是**物理像素**，
    /// 而 <c>FrameSize</c>/<c>ClientSize</c> 是**与 DPI 无关的逻辑单位**，两者之间差
    /// 一个 <c>Screen.Scaling</c>，不换算的话在缩放不是 100% 的屏幕上会算错窗口多大。
    /// </summary>
    private void ClampIntoScreen()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null) return;

        var area = screen.WorkingArea;
        var size = PixelSize.FromSize(FrameSize ?? ClientSize, screen.Scaling);

        // 窗口比工作区还大时，Math.Max 保证下界不会反超上界（Math.Clamp 那样会直接抛）——
        // 这种情况下贴着左上角，宁可右边/下边露出去，也不要把标题那头推出屏幕。
        var x = Math.Clamp(Position.X, area.X, Math.Max(area.X, area.Right - size.Width));
        var y = Math.Clamp(Position.Y, area.Y, Math.Max(area.Y, area.Bottom - size.Height));
        if (x == Position.X && y == Position.Y) return;

        _clamping = true;
        try { Position = new PixelPoint(x, y); }
        finally { _clamping = false; }
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// The focus-length slider's range depends on the build configuration.
    ///
    /// **The axaml carries the real Release range** (10–50, step 5, default 25); Debug
    /// overrides it here to 3–10, step 1. The direction is deliberate: forgetting to change
    /// this only ever ships the **real** range.
    ///
    /// The other direction (axaml carries the test range, Release overrides it) has already
    /// caused an incident — the 2026-07-31 change hard-coded 3–10 unconditionally,
    /// `Value="25"` got silently clamped to 10, and nobody noticed Release had changed too.
    ///
    /// Core sets no bound (`TaskRecord` accepts any duration), and `RestMinutes = ⌈f/5⌉`
    /// must hold for **every integer** — otherwise what gets verified in Debug isn't
    /// Release's actual behaviour, and the test range would be pointless.
    /// </summary>
    /// <summary>
    /// 把这一次启动的尺寸档套上去（DESIGN §8.10，3.8.0）。六个数字全部来自
    /// <see cref="WindowLayout"/>，XAML 里一个都不写死——同一个量只有一处定义。
    ///
    /// ⚠️ **必须排在 <see cref="RestoreWindowPosition"/> 之前**：那边在 <c>Opened</c>
    /// 里会调 <see cref="ClampIntoScreen"/>，而它读的是窗口当时的 <c>FrameSize</c>。
    /// 尺寸后设就会拿旧尺寸去夹，紧凑档从标准档的位置启动时会被推错地方。
    ///
    /// ⚠️ **只在启动时跑这一次**（用户 2026-09-05 定的语义）：运行中改 `layout` 文件
    /// 不生效，下次启动才算。这顺带免掉了「运行中换档要重新夹屏、要重算负边距、
    /// 提示条正显示着怎么办」的一整类边界情况。
    /// </summary>
    private void ApplyLayout()
    {
        var m = WindowLayout.Current;
        Width = m.WindowWidth;
        F<DialControl>("Dial").Height = m.DialHeight;
        F<DominoRow>("Dominoes").Height = m.DominoHeight;
        F<Grid>("DominoRowCell").Margin = m.DominoMargin;

        // 深色打底那份和蓝色错位那份，内容完全一样，两边都要设（DECISIONS J11）。
        foreach (var name in new[] { "AlarmBannerText", "AlarmBannerTextBlue" })
        {
            var tb = F<TextBlock>(name);
            tb.MaxLines = m.BannerMaxLines;
            tb.MaxWidth = m.BannerMaxWidth;
        }
    }

    private void ApplySliderRange()
    {
#if DEBUG
        var s = F<Slider>("Minutes");
        s.Minimum = 3;
        s.Maximum = 10;
        s.TickFrequency = 1;
        s.Value = 3;
#endif
    }

    private T F<T>(string name) where T : Control => this.FindControl<T>(name)!;

    /// <summary>
    /// 当前主题的调色板。**唯一真相源是 <see cref="Settings.DarkTheme"/>**，这个字段
    /// 只是 <see cref="ApplyTheme"/> 每次算完存下来给自绘那几处用的副本。
    /// </summary>
    private DialPalette _palette = DialPalette.Light;

    /// <summary>表盘右键菜单里唯一那一项。留着引用只为换主题时重画它的图标（见 <see cref="ApplyChrome"/>）。</summary>
    private MenuItem? _closeItem;

    /// <summary>
    /// 把主题铺开：日面素白、夜面深灰，同一块表的两副面孔（DESIGN §8.8）。
    ///
    /// **可以反复调用**（v3.0.0 起）——原来它只在构造函数里跑过一次、读的还是
    /// `ActualThemeVariant`（也就是系统主题），启动之后再没人碰过它。现在它由用户设置
    /// 驱动，点一下按钮就重跑一遍。
    ///
    /// 三件事，缺一不可：
    /// ① `Application.RequestedThemeVariant` —— Fluent 自带控件（单选、滑块、下拉、
    ///    开关、默认文字前景）**全靠这一句**跟着翻，Settings 窗口和 Give Up 确认框
    ///    一起白拿；App.axaml 里那两张 ThemeDictionaries 也是按这个变体选的，
    ///    `{DynamicResource}` 引到的中性色随之整批重刷。
    /// ② 自绘的表盘和骨牌 —— 它们不吃样式，只认 `Palette` 这个 StyledProperty
    ///    （`AffectsRender`，赋值即重绘）。
    /// ③ 右上角那四个图标 —— 它们的墨色和光晕现在也从调色板里取（ChromeIcons），
    ///    而图标是**构造出来塞进 Content 的对象**，不重建就不会变，所以必须
    ///    <see cref="ApplyChrome"/> 再跑一遍。
    ///
    /// ⚠️ 顺手刷 `row.Fallen` 是原来就有的，留着：反复调用它是幂等的（同一天算出来
    /// 同一个数），跟分钟节拍第 ⑤ 步每分钟核对一次（DECISIONS D12）不冲突。
    /// </summary>
    private void ApplyTheme()
    {
        _palette = _settings.DarkTheme ? DialPalette.Dark : DialPalette.Light;

        if (Application.Current is { } app)
            app.RequestedThemeVariant = _settings.DarkTheme ? ThemeVariant.Dark : ThemeVariant.Light;

        ApplyDialPalette();
        var row = F<DominoRow>("Dominoes");
        row.Palette = _palette;
        row.Fallen = DominoRow.FallenForToday(DateTime.Now);
    }

    /// <summary>
    /// 表盘用哪一套色：用户设定的底色，跑偏时把**钟面、刻度、指针**换成另一档
    /// （DESIGN §8.9）。
    ///
    /// ⚠️ **反色只作用于这一处**。骨牌、卡片、右上角四个图标、Settings 窗口、确认框全部
    /// 认底色，`Application.RequestedThemeVariant` 也不跟着动——那是主题，这是提示，
    /// 两件事。
    /// </summary>
    private void ApplyDialPalette()
    {
        var other = _settings.DarkTheme ? DialPalette.Light : DialPalette.Dark;
        F<DialControl>("Dial").Palette = _inverted ? _palette.WithFaceFrom(other) : _palette;
    }

    private int _tickedSecond = -1;

    /// <summary>
    /// One frame every 33ms — but **everything below happens once a second**: the 33ms
    /// cadence exists only to catch the second boundary within 33ms, not to do work 30
    /// times a second.
    ///
    /// ⚠️ **表盘重画也在秒边界上，不是每帧**（2026-08-29）：`DrawHands` 读的是
    /// `DateTime.Now`，而**秒针是一秒一跳的**（2026-07-28 用户定：扫秒针的钟不会响），
    /// 格子一分钟一变，闪烁一秒一翻，闹钟黄针靠 `AffectsRender` 自己触发——**秒与秒
    /// 之间表盘上没有任何东西在变**。重画放在"秒变化后的第一帧"，跟原来每帧都画**落在
    /// 同一帧上**，延迟一模一样（≤33ms），但重画次数是原来的 1/30。矢量表盘带渐变、
    /// 投影、六十个格子，这是全天候的开销。
    ///
    /// **It still ticks when the window is minimized** (user, 2026-07-28): the tick is the
    /// clock itself running, independent of whether you're watching it. Only the repaint
    /// skips when it's not visible — that's purely a power saving, it doesn't affect sound.
    ///
    /// The tick rides this existing timer rather than starting a new one: it's naturally
    /// aligned to the wall clock and never drifts, and 33ms of jitter is inaudible on a 35ms
    /// "tock". The alternative — a whole 1-second buffer handed to `SND_LOOP` — costs zero
    /// CPU, but the audio clock slowly drifts apart from the system clock, and within an
    /// hour the second hand and the sound would no longer line up.
    /// </summary>
    private void OnFrame(object? sender, EventArgs e)
    {
        var sec = DateTime.Now.Second;
        if (sec == _tickedSecond) return;
        _tickedSecond = sec;

        if (WindowState != WindowState.Minimized && IsVisible) F<DialControl>("Dial").InvalidateVisual();

        // **滴答就是"你走神了，回来"这句提醒**（2026-08-29 用户推翻 C7/C8，DESIGN §10）：
        // 跟钟面的闪烁**共用同一个判据**——镜像里上一秒是 `OffTask`，就既闪又响。
        //
        // ⚠️ 这推翻了 2026-08-13 定下的"安静是挣来的"（C7）：那一版里滴答是"时钟本来就
        // 在走"的中性信号，默认响，只有上一整分钟真的专注过才换来这一分钟安静，AFK 和
        // AW 掉线照响。现在滴答有了明确的含义，于是**没有跑偏就没有理由响**：
        // 专注、离开、AW 没数据、休息、空闲——全部安静。
        //
        // 连带效果（知情）：**没有任务在跑时程序完全不响**。以前空闲时它像个普通时钟
        // 一直嘀嗒，现在只有"专注期间跑偏"这一种情况出声。想要一个一直走的钟，
        // Settings 第一张卡的 **Force Ticking** 就是干这个的——它无条件响，不看任何判据，
        // 那才是"force"的字面意思（C6）。
        //
        // `_drifting` 由每秒那次镜像刷新维护（见 UpdateInversion），跟闪烁读的是同一格，
        // 所以两个信号天然同步，不可能一个响一个不闪。
        var ticking = _settings.ForceTicking || (_settings.TickEnabled && _drifting);
        if (ticking) Tick.Play(sec, _settings.TickVolume);

        // 每一秒：把 AW 内存镜像推到此刻（DESIGN §7.5）。**这是常驻期唯一还在碰 AW
        // 的地方**——判定、诊断、以后的反色全部从镜像读。挂在同一个 `_frame` 上，
        // **不新开定时器**（DECISIONS L8：这个程序里只有一个钟）；跟下面的 OnMinute
        // 各走各的，互不等待。
        RefreshMirror(DateTime.Now);

        // 整分钟：所有"以分为单位"的功能都在 OnMinute 里按固定顺序走一遍。
        if (sec == 0) OnMinute(DateTime.Now);
    }

    /// <summary>上一次镜像刷新还没回来的闸门。AW 卡住时（默认超时 10 秒）每秒一次会摞起来。</summary>
    private bool _mirrorBusy;

    /// <summary>
    /// 每秒一次的镜像刷新（DESIGN §7.5）。
    ///
    /// `async void`：跟 <see cref="OnMinute"/> 同一个理由——它是事件处理器的延长线，没有谁
    /// 能 await 它。里面不抛异常：AW 那一路的失败在
    /// <see cref="TaskSession.RefreshMirrorAsync"/> 里已经收敛成"那些秒记为无记录"。
    ///
    /// 没有任务在跑就什么都不做——镜像跟任务同生共死（DECISIONS O2），**空闲时一次 AW
    /// 查询都没有**。
    /// </summary>
    private async void RefreshMirror(DateTime now)
    {
        if (_session is not { Finished: false } session) return;
        if (_mirrorBusy) return;

        _mirrorBusy = true;
        try
        {
            await session.RefreshMirrorAsync(now);
            UpdateInversion(session, now);
        }
        finally { _mirrorBusy = false; }
    }

    /// <summary>钟面此刻是不是反着的（DESIGN §8.9）。**推导值，绝不落盘**。</summary>
    private bool _inverted;

    /// <summary>上一秒是不是跑偏（只用来决定日志打不打，不决定画什么）。</summary>
    private bool _drifting;

    /// <summary>
    /// 跑偏就让钟面**一秒一翻**（DESIGN §8.9）。镜像铺好之后，判据只剩一句话：
    /// **上一秒是不是 <see cref="JudgmentCode.OffTask"/>**。
    ///
    /// ⚠️ **是「翻」不是「设成反色」**：跑偏期间每一秒都把钟面倒一次个儿——黑、白、黑、白
    /// ——一直闪到你回来。这是用户 2026-08-29 明确要的效果，而且是他撤掉 3.1.0 那一版的
    /// 全部理由："5 秒一次的反转视觉影响太小，**需要一个频繁闪烁的东西才能吸引注意力**"。
    /// 稳定反着的钟面看两分钟就变成壁纸了，抓不住眼睛；**闪**才抓得住。
    /// 一秒一翻 = 完整周期 2 秒 = 0.5 Hz，远在光敏性癫痫的 3 Hz 风险区间之外。
    ///
    /// 不跑偏（<see cref="JudgmentCode.Focused"/> / <see cref="JudgmentCode.Afk"/> /
    /// <see cref="JudgmentCode.AwOffline"/>）就立刻回到用户自己设的那一档，不留半个相位。
    ///
    /// 读 <c>now-1</c> 而不是 <c>now</c>：当前这一秒还在被这一拍写，上一秒才是稳定的。
    /// 镜像的预测已经把 AW 那 3~10 秒的滞后填上了（§7.5 规则 3）。
    ///
    /// ⚠️ **判据必须是 `== OffTask`，不能写成"不是 Focused"**（DECISIONS N4）：
    /// <see cref="JudgmentCode.AwOffline"/> 按约定算专注，写成"不是 Focused"的话
    /// `aw-watcher-window` 一死钟面就会永远闪下去，而账本这段时间全判绿——屏幕跟账本
    /// 对着说反话。
    /// </summary>
    private void UpdateInversion(TaskSession session, DateTime now)
    {
        var second = session.MirrorAt(new DateTimeOffset(now).AddSeconds(-1));
        var offTask = !session.InRest && second.Code == JudgmentCode.OffTask;

        // 日志只在**进入/离开跑偏**那一下记，不是每秒一行——闪烁本身每秒都在发生，
        // 照着记就是每分钟 60 行（§8.1a 要的是"原因不能消失"，不是"把日志淹掉"）。
        if (offTask != _drifting)
        {
            _drifting = offTask;
            Log.Info(offTask
                ? $"Off task ({second.App} \"{second.Title}\"); dial face blinking"
                : "Back on task; dial face restored");
        }

        // 跑偏 → 倒一次个儿；不跑偏 → 回底色。
        var next = offTask && !_inverted;
        if (next == _inverted) return;
        _inverted = next;
        ApplyDialPalette();
    }

    /// <summary>上一分钟的处理还没跑完的闸门（见 <see cref="OnMinute"/>）。</summary>
    private bool _minuteBusy;


    /// <summary>
    /// **这个程序里所有以分钟为单位的事，全在这里、按这个顺序、一件做完再做下一件**
    /// （用户 2026-08-08 定的结构）。
    ///
    /// 为什么要收成一段直线代码：在这之前，分钟级的事分散在**两个各走各的定时器**上——
    /// 闹钟和 Alarms 清单挂在 33ms 的 <see cref="_frame"/>（分钟边界后 ≤33ms 触发），
    /// AW 查询和判定挂在 <see cref="TaskSession"/> 自己的 1 秒定时器上，而那个定时器锚在
    /// **任务开始那一刻**，所以它在边界后 0~1000ms 的任意位置触发。两者之间没有任何协调，
    /// 结果就是：闹钟已经把关机/重启命令发出去了，同一分钟里 AW 查询随后才到——
    /// 等于一边拆机器一边还在敲 aw-server（用户 2026-08-08 报：两次重启后 AW 报警）。
    /// 这不是哪一行写了并行，是两个时钟没有合并，所以光把闹钟从每秒改成每分钟解决不了。
    ///
    /// 现在 <see cref="_frame"/> 是唯一的钟：秒针和滴答仍然吃它的秒级节拍，其余全部
    /// 收到这里。<see cref="TaskSession"/> 不再持有定时器，由这里在分钟边界调它一次。
    ///
    /// ⚠️ **顺序是有讲究的，别随手调**（2026-08-08 定稿，DECISIONS L13）：
    /// ① 提示条到期收起 ② AW 查询+判定+三声通知 ③ Alarms 清单 ④ 闹钟（判断+执行/响铃）
    /// ⑤ 骨牌核对星期（2026-08-13 用户补充，见 DECISIONS D12，取代 D7 的"只在启动/Start 时查"）
    ///
    /// 三条理由，每条都对应一个真踩过或差点踩到的坑（都是针对①~④；⑤ 是后来单独加的，
    /// 见它自己那行注释）：
    ///
    /// 1. **闹钟整体排最后**，判断和动作不再分开。命令多半是关机/重启，让它成为这一分钟
    ///    的最后一件事，前面该做的都已经做完。原来的写法是"第 ① 步判断并执行命令"，
    ///    理由是"关机会把本进程杀掉，后面几步根本不会执行"——**那个前提只在 macOS 成立**：
    ///    Windows 的 `shutdown /s /t 0` 提交请求就返回，`await` 立刻回来，后面的 AW 查询
    ///    照跑，正好落在系统正在关机的时候，也就是 2.0.7 本来要修的那个 bug（"一边拆机器
    ///    一边还在敲 aw-server"）在 Windows 上等于没修。放到最后，这个修复不再依赖
    ///    "进程会被杀掉"这个平台特性。代价：关机比原来晚一点（最坏是这一分钟 AW 查询的
    ///    10 秒超时），对预约关机可以忽略。
    /// 2. **闹钟响铃因此天然排在 Alarms 清单之后**，正合 DESIGN §17 的要求：Windows 的
    ///    winmm 是单通道，谁后响谁把前一个截断；闹钟响完什么都不留，清单响完还留着一分钟
    ///    的提示条，所以让闹钟赢这一下代价更小。
    /// 3. **提示条收起排在第 ① 步**，即"先清旧的、再画新的"（新提示条在第 ③ 步画）。
    ///    反过来也能工作，但只是因为 `hideAt = now + 1分钟` 恰好躲过判断——哪天有人把它
    ///    改成 59 秒，收起那步就会把刚画上的提示条当场擦掉。这个顺序结构上不可能有那个 bug。
    ///
    /// ⚠️ **整段只有一层 try，不给中间几步单独包**（用户 2026-08-08 明确要求）：那样会
    /// 把一次真正的故障粉饰成已处理，反而让人略过日志里那行 Error（用户原话："加了 log
    /// 反而让我忽略了"）。**而且闹钟排到最后之后，不加 try 反而更安全**：前面几步抛异常时
    /// `MarkFired` 根本没执行，闹钟没被消费掉，下一分钟 `ShouldFire` 仍为真、会重试——
    /// 预约的关机不会被静悄悄吃掉。（闹钟排第 ① 步的那一版才有这个问题。）
    /// </summary>
    private async void OnMinute(DateTime now)
    {
        // AW 慢的时候下一个分钟边界照样会到；宁可整分钟跳过也不要两份并排跑。
        //
        // 曾经这里还带一套"取消上一分钟"的机制（L14），因为命令是 await 到子进程退出的，
        // 一条不退出的命令能把 `_minuteBusy` 永久卡住。2.2.0 之后 App 不再 await 命令
        // （只是起个 shell 就返回，L19），这里唯一还会等的是 AW 查询——它自带 10 秒超时，
        // 卡不了几分钟。那套 `CancellationTokenSource` 因此整个删掉了。
        if (_minuteBusy) { Log.Warn("Previous minute's work is still running; skipping this minute"); return; }

        _minuteBusy = true;
        try
        {
            // ---- 1) 上一分钟的提示条到期就收起。**排在第 ③ 步画新提示条之前**：
            //         先清旧的、再画新的，第 ③ 步刚画上的那条结构上不可能被这里擦掉。
            //         到点时刻本身是整分钟，+1 分钟仍是整分钟，所以按分钟判断跟原来
            //         每秒判断的效果完全一样。
            if (_alarmBannerHideAt is { } hideAt && now >= hideAt)
            {
                F<Grid>("AlarmBanner").IsVisible = false;
                _alarmBannerHideAt = null;
            }

            // ---- 2) AW 查询 + 判定 + 三声通知（专注达成/休息结束/键鼠空闲在里面触发）
            if (_session is { } session) await session.TickMinuteAsync(now);

            // ---- 3) Alarms 清单：提示条 + 系统通知 + 响铃
            CheckAlarmsList(now);

            // ---- 4) 闹钟：判断 + 执行/响铃**在同一处**，且是整分钟的最后一件事。
            //         Execute 和响铃互斥（DECISIONS E8/E9），所以这里是二选一。
            //
            //         **两个平台走同一条路**（2026-08-09 统一，DESIGN §9.3，DECISIONS L28）：
            //         直接把命令跑起来、起完就返回，输出由后台任务收进日志。
            //         Windows 一度为了 `shutdown /h`（L17）另走"弹一个控制台窗口"的路，
            //         后来实测发现那个病只有那一条命令的一个分支才有——其余失败在 Windows
            //         上照样老实走管道，跟 macOS 一样。为一个罕见分支让每次到点都闪一个
            //         黑窗、还多养一条平台专有代码，不划算。
            //
            //         **起完就返回，绝不 await**：命令挂死也卡不到分钟循环。
            //         `LaunchDetached` 自己现读 rules.json，所以刚 `--select` 换过的 #0
            //         立刻生效，不用重启程序。
            if (now >= _alarmQuietUntil && _alarm.ShouldFire(now))
            {
                _alarm.MarkFired();   // 一次性：响过即撤，不是每日重复（DECISIONS E5）
                if (_settings.CommandEnabled) Command.LaunchDetached(_rules);
                else Sound.Repeat(_settings.CommandSound, AlarmRings);
            }

            // ---- 5) 骨牌：每分钟核对一次星期，取代 D7 原来"只在启动和点 Start 时查"
            //         的做法（2026-08-13 用户要求，DECISIONS D12）。**排在闹钟之后但没有
            //         破坏"闹钟最后一件事"这个前提**：这一步只读 `now` 写一个 UI 属性，
            //         不碰网络、不 await，跟闹钟真正忌讳的"关机时还在敲 aw-server"不是
            //         同一类风险——就算上一步刚起了关机命令，这一句也能在进程被杀掉之前
            //         同步跑完。
            F<DominoRow>("Dominoes").Fallen = DominoRow.FallenForToday(now);
        }
        catch (Exception e)
        {
            // 这一分钟的某件事炸了不能把时钟带走：秒针、滴答、下一分钟都得照常。
            Log.Error("The minute's work threw", e);
        }
        finally { _minuteBusy = false; }
    }

    /// <summary>
    /// Alarms 清单的每分钟检查（DESIGN §9.1）：读文件、找到点的条目、叠一条提示条在骨牌
    /// 上（无条件，不受任何开关控制）、出声（受 <see cref="Settings.AlarmsListEnabled"/>
    /// 控制）、刷新表盘小红圈。
    /// </summary>
    private void CheckAlarmsList(DateTime now)
    {
        var entries = ReadAlarmsList();
        var due = AlarmsList.Due(entries, _alarmsProcessedThrough, now);
        _alarmsProcessedThrough = now;

        if (due.Count > 0)
        {
            // **这行历史是唯一的反馈渠道**（3.7.0，用户 2026-09-03 定）：写错的 cron 一律
            // 安静跳过、不报警，所以"提醒没响"要靠翻这里查不到记录来反推。因此**带上命中
            // 的那条表达式原文**——多条规则时不带它就只知道响过，不知道是哪一行响的。
            // 一条一行，绝不合并。
            // ⚠️ 前缀必须是 `Alarms list fired`，**不能只写 `Alarm fired`**：手拨闹钟那条
            // 路已经在用 `Alarm fired; running executeCommand: ...`（见上面第 4 步），
            // 两条撞在一起 grep 出来分不开，而这行日志正是排查"提醒怎么没响"的唯一手段。
            foreach (var entry in due)
                Log.Info($"Alarms list fired {entry.At:HH:mm} [{entry.Expression}] {entry.Text}");
            ShowAlarmBanner(due, now, TimeSpan.FromMinutes(1));   // 无条件：屏幕上一定看得见
            // 无条件，而且**一条事件一个通知，永远不合并**（用户 2026-09-03 点名）：提示条
            // 那边受两行的硬上限约束、多出来的只剩一个 `+N`，通知中心这份才是不丢的那份。
            foreach (var entry in due) Notify.Show(entry.Text);
            if (_settings.AlarmsListEnabled) Sound.Repeat(_settings.AlarmsListSound, AlarmsListRings);
        }

        // 红圈：下一条的角度 + 那一分钟是不是不止一条（不止一条画双圈，DESIGN §9.1）
        var next = AlarmsList.NextDue(entries, now);
        var dial = F<DialControl>("Dial");
        dial.AlarmsDotMinutes = AlarmsList.DotPosition(next.Count > 0 ? next[0] : null, now);
        dial.AlarmsDotMultiple = next.Count > 1;
    }

    /// <summary>
    /// 叠在骨牌上的提示条（2026-08-06，见 <see cref="_alarmBannerHideAt"/> 的注释）：
    /// 程序自己画，保证屏幕上一定看得见，跟 <see cref="Notify"/> 的系统通知并存。
    /// 显示 <paramref name="visibleFor"/> 那么久，到点在 <see cref="OnFrame"/> 里跟秒针
    /// 共用的心跳一起收起，不需要用户手动点掉。
    ///
    /// **到点真触发**（<see cref="CheckAlarmsList"/>）传 1 分钟——挂在整分钟节拍上，
    /// 下一分钟整点收起最自然。**点红圈手动瞄一眼**（<see cref="OnAlarmsDotClicked"/>）
    /// 传几秒——那只是扫一眼"下一条是什么"，没必要占那么久。两条路复用同一块显示
    /// 区域和同一套双层文字画法（DECISIONS J11），只是停留时长不同。
    /// </summary>
    private void ShowAlarmBanner(IReadOnlyList<AlarmEntry> due, DateTime now, TimeSpan visibleFor)
    {
        // **一条一行，不是 `A / B / C` 挤成一行**（3.7.0）：同一分钟多条在 crontab 下变得
        // 寻常，挤一行读起来很容易漏掉后面那几条——这正是用户提这一轮的起因。
        //
        // ⚠️ **最多两条**，硬上限，理由在 MainWindow.axaml 那段注释里（提示条跟骨牌叠在
        // 同一个 Auto 高的格子里，第三行会把整个窗口撑高）。多出来的缀在**时间行**末尾
        // 写成 `+N`，不占新的一行。屏幕上省掉的那几条**在系统通知和 itami.log 里一条不少**。
        var time = due[0].At.ToString("HH:mm");
        if (due.Count > BannerMaxLines) time += $"  +{due.Count - BannerMaxLines}";
        var text = string.Join("\n", due.Take(BannerMaxLines).Select(e => e.Text));

        // 深色底层 + 蓝色错位叠层，内容完全一样，两层都要设（DESIGN §9.1）
        F<TextBlock>("AlarmBannerTime").Text = time;
        F<TextBlock>("AlarmBannerText").Text = text;
        F<TextBlock>("AlarmBannerTimeBlue").Text = time;
        F<TextBlock>("AlarmBannerTextBlue").Text = text;

        F<Grid>("AlarmBanner").IsVisible = true;
        _alarmBannerHideAt = now.Add(visibleFor);
    }

    /// <summary>
    /// 点表盘上 Alarms 清单的小红圈（2026-08-27，DESIGN §17.1）：瞄一眼"下一条是什么"，
    /// 跟红圈本来就代表的那一条是同一条数据（<see cref="AlarmsList.Next"/>）。
    ///
    /// **纯读，不触发任何真实效果**——不出声、不弹系统通知、**不推进
    /// <see cref="_alarmsProcessedThrough"/> 水位线**。跟 <see cref="CheckAlarmsList"/>
    /// 那条到点真正触发的路径完全隔离，点这一下绝不会让后面真正到点时的提醒被冲掉或
    /// 提前消费——这是这个功能唯一不能妥协的一条。
    ///
    /// 没有下一条（<c>alarms.cron</c> 是空的，或者压根没红圈可点）就什么都不做，
    /// 静默返回；红圈都没画出来时 <see cref="DialControl.HitTestAlarmsDot"/> 已经先
    /// 挡掉了这一条分支，这里理论上不会走到，多一层判断只是防御性的。
    ///
    /// 拿的是 <see cref="AlarmsList.NextDue"/>（那一分钟上的**全部**条目）而不是单独一条：
    /// 红圈画成双圈就是在说"这一刻不止一件事"，点开只给一条等于说了一半。停留时长跟着
    /// 条数走（见 <see cref="PeekSeconds"/>）。
    /// </summary>
    private bool OnAlarmsDotClicked()
    {
        var next = AlarmsList.NextDue(ReadAlarmsList(), DateTime.Now);
        if (next.Count == 0) return false;
        ShowAlarmBanner(next, DateTime.Now, PeekSeconds(next.Count));
        return true;
    }

    /// <summary>
    /// 瞄一眼停留多久：一条 3 秒，每多一条 +1 秒，封顶 6 秒。跟到点真触发那次的 1 分钟
    /// 分开——那是"这件事现在该做了"，这只是扫一眼。
    /// </summary>
    private static TimeSpan PeekSeconds(int count) => TimeSpan.FromSeconds(Math.Min(3 + (count - 1), 6));

    /// <summary>
    /// 读 <c>alarms.cron</c>（3.7.0 起是一份标准 crontab，不再是 Markdown 清单）。程序
    /// 只读不回写不清理不生成——文件不存在（还没建过）就当空清单，任何读取/解析失败都
    /// 安静收场，一个格式错误的清单文件绝不能把程序搞挂。
    ///
    /// <c>File.ReadAllText(path)</c> **跟 <c>rules.json</c>/<c>settings.json</c> 是同一条
    /// 路**：默认按 UTF-8 解码、自动探测并跳过开头的 BOM，所以注释和提醒文字写中文跟
    /// <c>rules.json</c> 里的中文组名是同一个机制。**刻意用宽容解码而不是严格解码**——
    /// 万一文件被存成了 GBK，宽容解码只是中文变乱码、五个 ASCII 字段照常解析、规则照常
    /// 触发；严格解码会在非法字节上抛异常，被下面这个 catch 吞掉之后整份文件当空清单，
    /// 那是"一个字符存错、所有提醒集体消失"，比乱码坏得多。
    ///
    /// **每分钟整个重读重解析一遍，没有缓存、没有 FileSystemWatcher**（DESIGN §9.2）：
    /// 文件几 KB、几十行，解析是微秒级，换来的是"改完最多一分钟生效、不用重启"。
    /// </summary>
    private static IReadOnlyList<CronEntry> ReadAlarmsList()
    {
        try
        {
            var path = Path.Combine(AppData.Dir, "alarms.cron");
            return File.Exists(path) ? AlarmsList.Parse(File.ReadAllText(path)) : [];
        }
        catch (Exception e)
        {
            Log.Error("Failed to read alarms.cron; treating as empty for this check", e);
            return [];
        }
    }

    /// <summary>
    /// Refreshes the two top-right toggle icons and the window's pinned state to match
    /// what's in Settings.
    ///
    /// **The speaker only controls the tick** (user, 2026-07-28): the tick is the clock's
    /// own function, unrelated to the three notification sounds that nudge you back to
    /// work, so it isn't a "mute everything" switch. Those three sounds each have their own
    /// toggle in the settings window.
    ///
    /// The icons are **drawn as vectors** (<see cref="ChromeIcons"/>), not font glyphs.
    /// These two used to be Segoe Fluent Icons' `E767` / `E74F` / `E840` / `E718`, and that
    /// font only ships preinstalled on Windows — two tofu boxes on macOS. State is still
    /// expressed through the shape itself plus opacity, never text (there isn't a single
    /// word above the divider):
    ///
    /// | | Off | On |
    /// |---|---|---|
    /// | Speaker | A diagonal slash | Two sound waves |
    /// | Pin | Hollow (outline only) | Filled |
    ///
    /// The pin's off state **deliberately avoids an "X"**: at full opacity an X reads as
    /// "pinning is disabled", which is the opposite of "currently pinned". A slashed
    /// speaker has no such ambiguity — everyone reads a slashed speaker as muted.
    /// </summary>
    private void ApplyChrome()
    {
        var mute = F<Button>("MuteBtn");
        var pin = F<Button>("PinBtn");

        // #5: Force on -> hide the ticking icon, the user can't manually turn it off
        mute.IsVisible = !_settings.ForceTicking;
        mute.Content = ChromeIcons.Speaker(_settings.TickEnabled, _palette);
        mute.Classes.Set("on", _settings.TickEnabled);
        pin.Content = ChromeIcons.Pin(_settings.Pinned, _palette);
        pin.Classes.Set("on", _settings.Pinned);

        // 齿轮和主题图标也在这儿重建（v3.0.0）：四个图标的墨色/光晕都跟着调色板走，
        // 换主题时必须整排重新画一遍。齿轮没有开关态，主题图标**画的是当前状态**
        // （夜间显示月亮），所以它也不挂 `on` 这个 class——那一档是给"开/关"用的，
        // 这里两个态一样重要，用 Opacity 分主次会读成"主题被关掉了"。
        F<Button>("SettingsBtn").Content = ChromeIcons.Gear(_palette);
        F<Button>("ThemeBtn").Content = ChromeIcons.Theme(_settings.DarkTheme, _palette);

        // 右键菜单里那个关闭图标。构造函数里 ApplyChrome 先跑、菜单后建，那一趟这里
        // 还是 null（图标在建的时候就已经用当前调色板画好了），只有换主题那趟需要它。
        if (_closeItem is not null) _closeItem.Icon = ChromeIcons.Close(_palette);

        // ⚠️ Cutting off the tick that's already playing does **not** belong here (moved out
        // 2026-08-03, DECISIONS E13). ApplyChrome runs on four paths — construction, the
        // speaker, **the pin**, and closing Settings — and on Windows `Tick.Stop()` is
        // `PlaySound(null)`, which stops whatever this process has on winmm's single
        // channel, no matter who started it. Sitting here, it meant clicking the pin
        // silenced the alarm ring that happened to be playing. Stopping the tick is the
        // speaker's business, so it lives in the speaker's handler.
        WindowPin.Set(this, _settings.Pinned);
    }

    private void LoadRules()
    {
        try
        {
            _rules = GroupRules.Load(AppData.RulesPath());

            // Controls are built **once** and never rebuilt afterward (§15.2). rules.json
            // is only read once at startup, and the goal list never changes during a single
            // run — rebuilding the layout every time a task ends is both unnecessary and
            // exactly where that bug came from.
            var rows = new List<DockPanel>();
            foreach (var name in _rules.SelectableGroups)
            {
                // 这一块整个坐在 Start 按钮以下那张半透明卡片上（DECISIONS K，
                // 2026-08-08 改稿），不再需要 HaloTextBlock 那套双层描边——卡片本身
                // 已经给足对比度了，Content 恢复成普通字符串即可。
                var radio = new RadioButton
                {
                    Content = name,
                    GroupName = "Goals",
                    [DockPanel.DockProperty] = Avalonia.Controls.Dock.Left,
                };
                radio.IsCheckedChanged += OnGoalToggled;
                _goalRadios.Add(radio);

                var label = new TextBlock
                {
                    FontSize = 14,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Opacity = 0.6,
                    [DockPanel.DockProperty] = Avalonia.Controls.Dock.Right,
                };
                _duringLabels.Add(label);

                rows.Add(new DockPanel
                {
                    LastChildFill = true,
                    Children = { radio, label, new TextBlock() },
                });
            }
            F<ItemsControl>("Goals").ItemsSource = rows;   // Also set only this once

            // First run: seed a during.json of all zeros for the current set of goals (§11.2)
            _during.EnsureSeeded(_rules.SelectableGroups);
            RefreshGoalItems();
            // Restore the previously selected goal, or pick the first one if it's gone
            var saved = _goalRadios.FirstOrDefault(r =>
                r.Content?.ToString() == _settings.SelectedGroup);
            if (saved is not null) saved.IsChecked = true;
            else if (_goalRadios.Count > 0) _goalRadios[0].IsChecked = true;
            Log.Info($"rules.json loaded. Goals: {string.Join(", ", _rules.SelectableGroups)}");
        }
        catch (Exception e)
        {
            _rules = null;
            Log.Error("Cannot read rules.json", e);
        }
    }

    private void OnGoalToggled(object? sender, RoutedEventArgs e)
    {
        var picked = Picked();
        if (picked is not null)
        {
            _settings.SelectedGroup = picked;
            _settings.Save();
        }
        RefreshStartButton();
    }

    private string? Picked()
        => _goalRadios.FirstOrDefault(r => r.IsChecked == true)?.Content?.ToString();

    /// <summary>
    /// Refreshes the accumulated-time numbers. **Only touches Text, never the control
    /// tree** (§15.2).
    ///
    /// This used to build a fresh batch of `DockPanel`s every time, stuff the same batch of
    /// `RadioButton` / `TextBlock` instances into them, and swap the whole `ItemsSource`.
    /// Avalonia doesn't allow a control to have two visual parents, so it threw
    /// `already has a visual parent DockPanel` — and because that was thrown inside
    /// `EndSession`, the `RefreshStartButton()` call after it got skipped entirely, and
    /// **the button stayed stuck on "Give up" forever**.
    ///
    /// "Remove from the old parent before re-adding" was tried at the time, but only for
    /// the RadioButton, missing the TextBlock — which is why it looked like the fix "didn't
    /// take". But that was patching a hole that shouldn't have existed in the first place:
    /// **treating live controls as an `ItemsSource` was the actual mistake**. The list never
    /// changes during a single run (`rules.json` is only read once at startup), so building
    /// it once is all that's needed.
    /// </summary>
    private void RefreshGoalItems()
    {
        for (var i = 0; i < _goalRadios.Count && i < _duringLabels.Count; i++)
        {
            var name = (string)_goalRadios[i].Content!;

            // 已落盘的 checkpoint + 本轮实时（§11.2）。**纯显示求和，一个字都不写**：
            // during.json 只在任务启动那一刻被回填改写一次。
            //
            // 本轮那一项带着 H2 的 fail-open 水分（AwOffline 计入专注），而下次启动的回填
            // 是 fail-closed 的，所以**同一段时间在下次启动后可能变小**（DECISIONS I2）。
            // 另外它本来就**会往回退**：每拍重画最近 4 分钟（§4.2），因为 AW 的 afk 判定
            // 要 180 秒才追认——走开一会儿，已经数过的一分钟会被撤销。两种回退都是知情
            // 接受的（DECISIONS D9）：表盘的格子和承诺弧本来就这么退，为了单调而记一个
            // 「本轮见过的最高值」是一个会撒谎的 UI 状态（原则 4：状态是推导出来的，不是
            // 累积出来的）。
            var live = _session is { } s && s.Task.Group == name ? s.FocusedSeconds() : 0;
            _duringLabels[i].Text = ((_during[name] + live) / 3600.0).ToString("F2");
        }
    }

    private void RefreshStartButton()
    {
        var btn = F<Button>("StartBtn");
        if (_session is { Finished: false })
        {
            btn.Content = _session.InRest ? "New round" : "Give up";
            // Only "Give up" is coloured red: it voids the whole round. "New round" starts
            // the next task after focus was already achieved, so it isn't dangerous and
            // stays green (see the Button.start.danger style in MainWindow.axaml).
            btn.Classes.Set("danger", !_session.InRest);
            btn.IsEnabled = true;
            return;
        }
        btn.Content = "Start";
        btn.Classes.Set("danger", false);
        btn.IsEnabled = _rules is not null && Picked() is not null;
        btn.InvalidateVisual();
    }

    // ---------------------------------------------------------------- Task lifecycle

    private void OnStart(object? sender, RoutedEventArgs e)
    {
        // Clicking it mid-task = give up; clicking it during rest = start a new round
        // (§8.4.6: no separate "skip rest" button is needed — starting a new task already
        // is skipping the rest)
        if (_session is { Finished: false })
        {
            if (_session.InRest) EndSession();
            else { _ = AskAbandonAsync(); return; }
        }

        // Radio buttons, single-select, locked once Start is pressed
        var picked = Picked();
        if (_rules is null || picked is null) return;

        // §14.1 (changed 2026-07-27): **truncate** to the current whole minute, don't round
        // up. Starting at 23:13:10 -> counted from 23:13:00. The cost is up to 59 seconds
        // before the click are counted for free; the benefit is starting immediately on
        // click instead of waiting.
        var task = new TaskRecord
        {
            StartedAt = TimeGrid.FloorToMinute(DateTimeOffset.Now),
            FocusMinutes = (int)F<Slider>("Minutes").Value,
            Group = picked,
        };

        // Re-check the day of week when Start is clicked, and update the domino count.
        // This way, clicking across midnight reflects the new date; the check is cheap
        // enough that it's not noticeable.
        F<DominoRow>("Dominoes").Fallen = DominoRow.FallenForToday(DateTime.Now);

        _session = new TaskSession(task, _rules!, _settings.AwBaseUrl);
        _session.Updated += OnSessionUpdated;
        _session.Interrupted += OnInterrupted;
        foreach (var r in _goalRadios) r.IsEnabled = false;   // Lock the selection once Start is pressed

        // 启动这一刻是 during.json 唯一的写入点（§11.2）：补齐上次统计到现在这一段，推进
        // checkpoint。故意不 await —— 界面不等它，先显示旧值。
        _ = BackfillAsync(picked, task.StartedAt);

        // The dial must show something the instant the button is pressed: the whole grey
        // arc goes up immediately, without waiting for the first ActivityWatch response.
        // Same for the rest wedge — it's a projected value now, already computed the
        // moment TaskSession is constructed (start + focus length).
        var dial = F<DialControl>("Dial");
        dial.StartedAt = task.StartedAt;
        dial.Cells = _session.Cells;   // The commitment arc is just the Gray span in the buffer, already there at construction time (§4.5)
        dial.RestFrom = _session.RestFrom;
        dial.RestMinutes = task.RestMinutes;
        dial.InvalidateVisual();

        RefreshStartButton();
        // §8.3 originally required "minimize to tray the instant a task starts"; the user
        // changed this to **stay put** on 2026-07-27. Related: returning to on-task only
        // un-pins now, it no longer minimizes (see the watchdog in OnFrame).
    }

    private void OnSessionUpdated()
    {
        if (_session is not { } s) return;

        var dial = F<DialControl>("Dial");
        dial.StartedAt = s.Task.StartedAt;
        dial.Cells = s.Cells;
        dial.RestFrom = s.RestFrom;
        dial.RestMinutes = s.Task.RestMinutes;
        dial.InvalidateVisual();

        // 这里原来还有一段按整分钟节流的任务栏图标重绘（`_lastIconMinute` + `RingIcon.Make`），
        // 2026-08-10 随图标本身一起删了（DECISIONS D11）。那个节流当初是为了修原生位图泄漏
        // 加的（1.0.3，a471f3c）——泄漏的源头 `RenderTargetBitmap` 现在整条路都没了，
        // 所以节流也没有存在的理由，不是"顺手删了个优化"。
        //
        // The goal list's running total follows the same tick — no timer of its own, because
        // the number it shows only ever changes on the whole-minute tick anyway (during rest
        // this fires once a second, but nothing behind it moves, so it's a no-op repaint).
        RefreshGoalItems();
    }

    /// <summary>
    /// Three events each get one system beep, and each can be individually turned off in
    /// Settings (user, 2026-07-28).
    ///
    /// The whole "pinned but never steals focus" scheme has been cleaned out entirely —
    /// the user's own words: "Stop fussing over window pinning. The logic is a mess and
    /// keeps breaking." And it really did keep breaking: first the completion moment
    /// didn't pin at all (never-steal-focus plus never-pin means no pop-up happens), then
    /// after the fix it could never be un-pinned again. Sound has none of these states.
    ///
    /// Drifting off task no longer gets any notification, **only a log line**. The cell on
    /// the dial is red, the grey arc has slid forward a bit — you look, you guess, same
    /// philosophy as giving no report. Idle input is different: it's warning about a
    /// stretch of time **about to be voided by ActivityWatch marking you afk**, and once
    /// it's too late there's no getting it back (§8.3.5).
    /// </summary>
    private void OnInterrupted(TaskSession.Interrupt why)
    {
        if (_session is null) return;

        switch (why)
        {
            case TaskSession.Interrupt.FocusDone:
                if (_settings.FocusDoneEnabled) Sound.Repeat(_settings.FocusDoneSound, NotifyRings);
                RefreshStartButton();
                break;

            case TaskSession.Interrupt.RestDone:
                if (_settings.RestDoneEnabled) Sound.Repeat(_settings.RestDoneSound, NotifyRings);
                EndSession();
                break;

            case TaskSession.Interrupt.Idle:
                if (_settings.IdleEnabled) Sound.Repeat(_settings.IdleSound, NotifyRings);
                break;
        }
    }

    /// <summary>
    /// 从 AW 的历史里补齐 <c>[上次统计到的时刻, 本次任务开始)</c> 这一段，然后把 checkpoint
    /// 推到本次开始时刻（§11.2 / DECISIONS I1）。<b>这是 during.json 唯一的写入点。</b>
    ///
    /// 这一段覆盖的正好是：<b>上一场任务全程（当时一秒都没落盘）+ 中间的空隙</b>。不会重复
    /// 计费，因为上一场的秒数根本没进过账本。
    ///
    /// 三件事按这个顺序，缺一不可：
    /// <list type="number">
    ///   <item><b>不阻塞启动。</b>fire-and-forget，任务界面立刻就绪，界面上先显示旧值，
    ///         每分钟的实时项照常累加。回填完成时数字会跳一次——首次可能跳很大，之后每次
    ///         就是上一场任务那点量。用户 2026-08-06 明确接受。</item>
    ///   <item><b>CPU 不落在 UI 线程上。</b><see cref="Backfill.CountAsync"/> 内部全程
    ///         <c>ConfigureAwait(false)</c>，画格子在线程池上跑；这里的 <c>await</c> 捕获
    ///         的是 UI 上下文，所以续体里碰 <c>_during</c> 和刷界面都是安全的。</item>
    ///   <item><b>失败就不推进 checkpoint。</b>下次启动自然重试同一个窗口——推进 checkpoint
    ///         这个动作本身就是成功的唯一证明，不需要任何重试或恢复逻辑。</item>
    /// </list>
    /// </summary>
    private async Task BackfillAsync(string goal, DateTimeOffset upTo)
    {
        if (_rules is null) return;

        try
        {
            using var aw = new AwClient(_settings.AwBaseUrl, Backfill.ClientTimeoutSeconds);

            var from = _during.RecordedThrough(goal);
            if (from is null)
            {
                // 首次：走完整段 AW 历史。起点不需要准确，只需要足够早——真正的裁剪是 AW
                // 自己干的，`created` 只是给分块循环定个下界，免得从 1970 年开始空扫
                // （DECISIONS I3）。
                from = await aw.FindBucketCreatedAsync(AwClient.WindowBucketType);
                Log.Info(from is null
                    ? $"Backfill \"{goal}\": first run, no bucket creation time available; falling back to one year"
                    : $"Backfill \"{goal}\": first run, walking history from {from:yyyy-MM-dd HH:mm}");
                from ??= upTo.AddYears(-1);
            }

            if (from >= upTo)
            {
                _during.Advance(goal, 0, upTo);   // 同一分钟内连开两次：没有新地面，只对齐 checkpoint
                return;
            }

            // 只在这一片真数到东西时才写日志：按天切片，走完整段历史是几百片，全记下来会
            // 把 1MB 的滚动日志冲掉——而空片本来就没什么可看的。
            var lastLogged = 0L;
            var seconds = await Backfill.CountAsync(
                aw, from.Value, upTo, _rules, goal,
                (through, running) =>
                {
                    if (running == lastLogged) return;
                    lastLogged = running;
                    Log.Info($"Backfill \"{goal}\": through {through:yyyy-MM-dd HH:mm}, {running / 3600.0:F2}h so far");
                });

            _during.Advance(goal, seconds, upTo);
            Log.Info($"Backfill \"{goal}\": +{seconds / 3600.0:F2}h over " +
                     $"{(upTo - from.Value).TotalHours:F1}h of history; total {_during[goal] / 3600.0:F2}h");
            RefreshGoalItems();
        }
        catch (AwUnavailableException ex)
        {
            // ⚠️ **这里绝不能 fail-open**。运行期连不上 AW 会把那一分钟判成 AwOffline 并计入
            // 专注（H2），因为那时机器摆明开着、人摆明在。回填的处境正相反——程序当时没在
            // 跑，AW 没数据最大的可能是机器关着。checkpoint 原地不动，下次启动重试。
            Log.Warn($"Backfill \"{goal}\" skipped, ActivityWatch unreachable; checkpoint left where it was: {ex.Message}");
        }
        catch (Exception ex)
        {
            Log.Error($"Backfill \"{goal}\" failed; checkpoint left where it was", ex);
        }
    }

    /// <summary>Task ended: back to the empty dial. <b>不写盘</b>——本轮的秒数会在下次任务启动时由回填重新数出来（§11.2）。</summary>
    private void EndSession()
    {
        var old = _session;
        _session = null;
        try { old?.Dispose(); } catch { /* couldn't close it, doesn't matter — state is already cleared */ }

        foreach (var r in _goalRadios) r.IsEnabled = true;
        // Restore the previously selected radio, making sure Picked() isn't null
        var saved = _goalRadios.FirstOrDefault(r =>
            r.Content?.ToString() == _settings.SelectedGroup);
        if (saved is not null) saved.IsChecked = true;
        else if (_goalRadios.Count > 0) _goalRadios[0].IsChecked = true;

        // 钟面立刻回到底色（DESIGN §8.9）：下一秒那次刷新本来也会收回去，但"任务都结束了
        // 钟面还反着一秒"没有道理。
        _inverted = false;
        _drifting = false;
        ApplyDialPalette();

        var dial = F<DialControl>("Dial");
        dial.Cells = [];
        dial.StartedAt = null;
        dial.RestFrom = null;
        dial.InvalidateVisual();

        RefreshGoalItems();
        RefreshStartButton();
    }

    // ---------------------------------------------------------------- Quitting = abandoning (§9)

    private bool _closeApproved;

    /// <summary>
    /// §9: **closing the window mid-focus = abandoning the task**, so it has to ask once.
    /// The title-bar ×, the taskbar's right-click "Close window", and Alt+F4 all land here.
    ///
    /// No task, or already resting (focus already achieved) → **quit right away, no
    /// question asked**.
    ///
    /// Closing is a synchronous event, so it can't await a dialog inline — cancel the
    /// close first, ask asynchronously, then close for real.
    ///
    /// **这里不再记账**（§11.2 重写）。1.0.x 时三条终结路径都得记得调 `BankRemainder()`，
    /// 漏一条就丢一段时间，崩溃更是直接丢——那套机制整个删掉了：本轮的秒数会在下次任务
    /// 启动时由回填从 AW 重新数出来，跟程序是怎么退出的完全无关。
    /// </summary>
    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeApproved) return;

        if (_session is { Finished: false, InRest: true }) return;   // Resting: focus already achieved, nothing to ask
        if (_session is not { Finished: false }) return;

        e.Cancel = true;

        if (await Confirm.AskAsync(this, "The task isn't finished. Quit anyway?"))
        {
            _session?.Abandon();
            _closeApproved = true;
            Close();
        }
    }

    /// <summary>
    /// Clicking "Give up" in the UI → **the same confirmation dialog** as closing the
    /// window, just different wording, and it **only abandons this round, it doesn't quit
    /// the program** (user, 2026-07-27).
    ///
    /// No report is shown — the UI **never** shows a report (user, 2026-07-27), not even
    /// on completion.
    /// </summary>
    private bool _abandoning;

    private async Task AskAbandonAsync()
    {
        if (_abandoning) return;
        _abandoning = true;
        try
        {
            if (!await Confirm.AskAsync(this, "The task isn't finished. Give up?")) return;
            var session = _session;          // Hold a steady reference in case it gets cleared mid-await
            session?.Abandon();
            EndSession();
        }
        finally { _abandoning = false; }
    }

    // ================================================================
    //  Alarm button interaction
    // ================================================================
    /// <summary>
    /// The alarm is **only ever written to disk at the moment of exit**, and only one
    /// value: the ring time (user, 2026-07-30). The yellow hand's position is a derived
    /// value — its time point mod 12 hours — so whether it changed or fired doesn't need
    /// recording separately. The Shutdown flag isn't handled here — Settings.Load always
    /// resets it to off at startup.
    /// </summary>
    private void SaveAlarmOnExit()
    {
        _settings.AlarmFireAt = _alarm.FireAt;
        _settings.Save();
    }

    /// <summary>Streak count and timestamp of the last scroll. See <see cref="OnAlarmWheel"/>.</summary>
    private DateTime _lastWheelAt = DateTime.MinValue;
    private int _wheelStreak;

    /// <summary>Maximum gap for scroll events to count as one streak. Beyond this it restarts from 1 notch.</summary>
    private const int WheelStreakGapMs = 300;

    /// <summary>
    /// Scroll wheel sets the alarm: scrolling forward (away from you) moves the hand
    /// counter-clockwise, scrolling back moves it clockwise. A slow scroll is 1 minute per
    /// notch; **a continuous fast scroll accelerates**.
    ///
    /// ⚠️ **The acceleration is based on scrolling rhythm, not the magnitude of a single
    /// event** (changed 2026-08-02). It used to read
    /// <c>Math.Abs(e.Delta.Y) / 120</c> — 120 is the unit of **Win32's
    /// `WM_MOUSEWHEEL`**, whereas Avalonia's <c>Delta.Y</c> is **1.0 per notch**. So that
    /// division was always 0.008, got pulled back up to 1 by `Math.Max(1, …)`, and the tier
    /// switch always landed on the first tier: **the acceleration ladder recorded in E3
    /// never actually ran once** — every scroll always moved exactly 1 minute.
    ///
    /// And fast scrolling in Avalonia shows up as **denser events**, not a bigger Delta, so
    /// the correct approach is to count "how many consecutive notches" — a gap longer than
    /// <see cref="WheelStreakGapMs"/> milliseconds breaks the streak and restarts at
    /// 1 minute per notch. This way a slow scroll still fine-tunes minute by minute, while a
    /// fast scroll can jump across hours in one motion.
    /// </summary>
    private void OnAlarmWheel(object? sender, PointerWheelEventArgs e)
    {
        if (e.Delta.Y == 0) return;

        var now = DateTime.Now;
        _wheelStreak = (now - _lastWheelAt).TotalMilliseconds <= WheelStreakGapMs
            ? _wheelStreak + 1
            : 1;
        _lastWheelAt = now;

        // Over the course of a streak: 1 -> 3 -> 8 -> 15 -> 30 minutes per notch.
        // A full 12-hour sweep is 720 notches; at 30 minutes per notch that's roughly
        // twenty-odd flicks, while letting go instantly returns to 1 minute per notch so
        // fine adjustment is unaffected.
        var step = _wheelStreak switch
        {
            <= 2 => 1,
            <= 5 => 3,
            <= 10 => 8,
            <= 20 => 15,
            _ => 30,
        };

        // High-precision trackpads may report multiple notches at once; multiply that in too
        var notches = Math.Max(1, (int)Math.Round(Math.Abs(e.Delta.Y)));
        var direction = e.Delta.Y > 0 ? -1 : +1;

        Bump(direction * notches * step * AlarmClock.SlotMinutes);
        _alarmQuietUntil = now.AddSeconds(2);
        e.Handled = true;
    }

    /// <summary>Moves the hand and refreshes the yellow hand plus the tooltip. The tooltip reads <see cref="AlarmClock.FireAt"/> directly — what's shown and what will actually fire are the same value.</summary>
    private void Bump(double minutes)
    {
        _alarm.Bump(minutes, DateTime.Now);
        var dial = F<DialControl>("Dial");
        dial.AlarmMinutes = _alarm.Position;
        if (_alarm.FireAt is { } at)
        {
            ToolTip.SetTip(dial, at.ToString("HH:mm"));
            ToolTip.SetIsOpen(dial, true);
        }
    }

    /// <summary>Gear icon: opens Settings. Two sounds, change one, save one (see <see cref="SettingsWindow"/>).</summary>
    private async void OnSettings(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            await new SettingsWindow(_settings, _rules).ShowDialog(this);
            // #2.6: activate the alarm when Execute is turned on
            if (_settings.CommandEnabled)
                _alarm.Activate(DateTime.Now);
            ApplyChrome();  // #5: Force Ticking may have changed, refresh the speaker icon
        }
        catch (Exception ex) { Log.Error("Failed to open the settings window", ex); }
    }
}
