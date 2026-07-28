using Avalonia;

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
            // 调试出口（不是产品功能）：离屏渲染几张表盘样张然后退出，不开窗口。
            // 表盘在 App 层，Core 的测试碰不到它，有些几何错误只有看图才发现得了。
            if (args is ["--dial-specimens", var outDir, ..])
            {
                BuildAvaloniaApp().SetupWithoutStarting();
                DialSpecimens.Render(outDir);
                return;
            }

            // 同上，把番茄图标导成 .ico 装进 exe 的资源。图标仍然是代码画的，
            // 仓库里不放位图 —— 这条纪律从表盘一路贯到这里。
            if (args is ["--export-icon", var icoPath, ..])
            {
                BuildAvaloniaApp().SetupWithoutStarting();
                IconExport.Write(icoPath);
                return;
            }

            // 同上，macOS 那一侧：铺一个 .iconset 目录出来，交给 iconutil 压成 .icns
            // 装进 .app（见 pack-macos.sh）。同一份美术，两种容器。
            if (args is ["--export-iconset", var iconsetDir, ..])
            {
                BuildAvaloniaApp().SetupWithoutStarting();
                IconExport.WriteIconset(iconsetDir);
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
}
