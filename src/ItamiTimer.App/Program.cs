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
            if (e.ExceptionObject is Exception ex) Log.Error("未捕获的异常，程序即将退出", ex);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("后台任务里未观察到的异常", e.Exception);
            e.SetObserved();
        };

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Log.Error("启动失败", ex);
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .LogToTrace();
}
