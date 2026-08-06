using System.Diagnostics;
using System.Text;

namespace ItamiTimer.App;

/// <summary>
/// Alarms 清单到点时弹的系统通知（DESIGN §17）。**不是闹钟的 Execute**（<see cref="Command"/>）
/// ——这里没有用户可配的命令，永远是"弹一条带这段文字的系统通知"，无条件执行，不受
/// 任何开关控制。
///
/// 两个平台都靠**起一个短命的子进程**做到，不加任何新的包依赖、不碰 TFM（CLAUDE.md：
/// App 保持 `net10.0`，`-windows` 别加回去）——真正的 WinRT/UWP toast 绑定需要
/// `-windows` 系带 TFM 或者签名打包的应用身份，这个项目两样都没有，所以退而求其次让
/// `powershell.exe` 去调 WinRT API。**2026-08-06 在本机验证过确实弹得出来**：以
/// "Windows PowerShell"这个身份/图标显示，不是 ItamiTimer 自己的——这是不打包应用绕不
/// 开的已知代价，接受。
/// </summary>
public static class Notify
{
    private const string Title = "ItamiTimer";

    /// <summary>弹一条通知。失败一律安静收场、只写日志——提示本身绝不能把程序搞挂（跟 Sound.Play 同一条原则）。</summary>
    public static void Show(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            if (OperatingSystem.IsWindows()) ShowWindows(text);
            else if (OperatingSystem.IsMacOS()) ShowMac(text);
        }
        catch (Exception e)
        {
            Log.Error($"Failed to show notification: {text}", e);
        }
    }

    // ---------------------------------------------------------------- Windows

    /// <summary>
    /// 走 WinRT 的 <c>ToastNotificationManager</c>，从纯 PowerShell 里直接调，不需要
    /// AppUserModelID 注册（那是让图标/身份显示成 ItamiTimer 自己的必要条件，这里不追求）。
    ///
    /// 脚本整体走 <c>-EncodedCommand</c>（Base64 的 UTF-16LE）传进去，而不是拼进命令行
    /// 参数——这样完全绕开 cmd/PowerShell 的引号转义地狱，脚本里唯一需要手工转义的只有
    /// 嵌进去的文字本身：先做 XML 转义（进的是 toast 的 XML），再做 PowerShell 单引号
    /// 转义（外层拿单引号包住这段 XML 字符串）。
    /// </summary>
    private static void ShowWindows(string text)
    {
        var t = PsSingleQuoted(XmlEscape(Title));
        var m = PsSingleQuoted(XmlEscape(text));
        var script =
            "[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null\n" +
            "[Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime] | Out-Null\n" +
            "$doc = New-Object Windows.Data.Xml.Dom.XmlDocument\n" +
            $"$doc.LoadXml('<toast><visual><binding template=\"ToastGeneric\"><text>{t}</text><text>{m}</text></binding></visual></toast>')\n" +
            "$toast = New-Object Windows.UI.Notifications.ToastNotification $doc\n" +
            "[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier(\"Windows PowerShell\").Show($toast)\n";

        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        Run("powershell.exe", ["-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-EncodedCommand", encoded]);
    }

    private static string XmlEscape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    private static string PsSingleQuoted(string s) => s.Replace("'", "''");

    // ---------------------------------------------------------------- macOS

    /// <summary>
    /// <c>osascript -e 'display notification ...'</c>——系统自带，不需要额外权限授予
    /// （不同于从签名 .app 里调 <c>UNUserNotificationCenter</c> 那条路）。
    /// </summary>
    private static void ShowMac(string text)
    {
        var t = AppleScriptQuoted(Title);
        var m = AppleScriptQuoted(text);
        Run("osascript", ["-e", $"display notification \"{m}\" with title \"{t}\""]);
    }

    private static string AppleScriptQuoted(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    // ---------------------------------------------------------------- 共用

    /// <summary>
    /// 用 <c>ArgumentList</c> 而不是拼一整条 <c>Arguments</c> 字符串——每个元素原样传给
    /// 子进程的 argv，不需要再操心一层 shell 转义（跟 <see cref="Command"/> 不一样，那边
    /// 命令本身就是用户写在 rules.json 里的一整条 shell 命令，没法回避 shell）。
    /// </summary>
    private static void Run(string exe, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        var p = Process.Start(psi);
        if (p is null) { Log.Warn($"Notify: {exe} did not start"); return; }

        // 后台收集退出码和 stderr，不阻塞——通知弹没弹好不该拖住 UI 线程，出错了日志里能查。
        _ = Task.Run(async () =>
        {
            try
            {
                var stderr = await p.StandardError.ReadToEndAsync();
                await p.WaitForExitAsync();
                if (p.ExitCode == 0) Log.Info($"Notify: {exe} exited 0");
                else Log.Warn($"Notify: {exe} exited with {p.ExitCode}: {stderr.Trim()}");
            }
            catch (Exception e) { Log.Error("Notify: failed to collect process output", e); }
        });
    }
}
