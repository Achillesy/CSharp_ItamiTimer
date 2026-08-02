using Avalonia;
using Avalonia.Headless;

namespace ItamiTimer.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // 界面对用户是沉默的，所以**崩溃的原因更要留下来** —— 否则程序凭空消失，
        // 谁也说不出发生了什么。
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) Log.Error("Unhandled exception; the program is about to exit", ex);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("Unobserved exception in a background task", e.Exception);
            e.SetObserved();
        };

        try
        {
            // 调试出口：离屏渲染几张表盘样张然后退出。
            if (args is ["--dial-specimens", var outDir, ..])
            {
                HeadlessBuilder().SetupWithoutStarting();
                DialSpecimens.Render(outDir);
                return;
            }

            // 同上，把番茄图标导成 .ico 装进 exe 的资源。图标仍然是代码画的，
            // 仓库里不放位图 —— 这条纪律从表盘一路贯到这里。
            if (args is ["--export-icon", var icoPath, ..])
            {
                HeadlessBuilder().SetupWithoutStarting();
                IconExport.Write(icoPath);
                return;
            }

            // 同上，macOS 那一侧：铺一个 .iconset 目录出来，交给 iconutil 压成 .icns
            // 装进 .app（见 pack-macos.sh）。同一份美术，两种容器。
            if (args is ["--export-iconset", var iconsetDir, ..])
            {
                HeadlessBuilder().SetupWithoutStarting();
                IconExport.WriteIconset(iconsetDir);
                return;
            }

            // 单实例限制（DESIGN §16.4）：只挡正常启动这条路径。调试出口是一次性跑完
            // 就退出的命令行工具，跟"已经在跑的那个窗口"不冲突，不用挡。
            if (!SingleInstance.TryAcquire())
            {
                Log.Info("Another instance is already running; activated it and exiting.");
                return;
            }

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Log.Error("Startup failed", ex);
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .LogToTrace();

    /// <summary>
    /// 三个调试出口专用的 AppBuilder。**正常启动那条路径不走这里**，它仍然是
    /// <see cref="BuildAvaloniaApp"/> + `UsePlatformDetect`。
    ///
    /// **为什么不能共用**：那三条路径只往 <c>RenderTargetBitmap</c> 上画，压根不需要
    /// 窗口平台；而 `UsePlatformDetect` 会去初始化原生窗口系统。在有图形会话的机器上
    /// 看不出区别，**一旦没有图形会话就崩**：
    ///
    /// <code>
    /// Avalonia.Native was not able to start the RenderTimer   (-6661)
    /// </code>
    ///
    /// 这意味着**构建步骤依赖图形会话** —— `pack-macos.sh` 里那句 `--export-iconset`
    /// 正是构建的一环，放到 CI 上就会栽。构建不该关心有没有人登录着桌面。
    ///
    /// <c>UseHeadlessDrawing = false</c> 是关键的一半：headless 平台默认连绘制都是空的，
    /// 关掉它再挂上 <c>UseSkia</c>，画出来的才是**真实像素**，跟正常启动时是同一个
    /// Skia 后端。
    ///
    /// 验证方式（2026-07-28）：<c>--export-icon</c> 和 <c>--export-iconset</c> 那 11 个产物
    /// 改前改后 **SHA-256 逐字节一致**，说明渲染结果没有任何变化。
    /// 8 张表盘样张**不能用校验和比**——它们画着当前时刻的指针，同一个构建连跑两次
    /// 哈希就不同（实测过），那部分是看图确认的。
    /// </summary>
    private static AppBuilder HeadlessBuilder() => AppBuilder.Configure<App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .UseSkia()
        .LogToTrace();
}
