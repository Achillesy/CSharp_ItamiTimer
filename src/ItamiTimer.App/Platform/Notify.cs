using System.Diagnostics;
using System.Text;

namespace ItamiTimer.App;

/// <summary>
/// Alarms 清单到点时弹的系统通知（DESIGN §9.1）。**不是闹钟的 Execute**（<see cref="Command"/>）
/// ——这里没有用户可配的命令，永远是"弹一条带这段文字的系统通知"，无条件执行，不受
/// 任何开关控制，跟骨牌区域的提示条一起出现（<c>MainWindow.ShowAlarmBanner</c>），
/// 不是二选一。
///
/// 两个平台都靠**起一个短命的子进程**做到，不加任何新的包依赖、不碰 TFM（CLAUDE.md：
/// App 保持 `net10.0`，`-windows` 别加回去）——真正的 WinRT/UWP toast 绑定需要
/// `-windows` 系带 TFM 或者签名打包的应用身份，这个项目两样都没有，所以退而求其次让
/// `powershell.exe` 去调 WinRT API。
///
/// ⚠️ **2026-08-06 一波三折**：最初用 `CreateToastNotifier("Windows PowerShell")`
/// 这个裸字符串当 AppId，真机验证发现根本弹不出来、Windows 通知设置里也找不到这个
/// 应用，一度整个放弃改走 <c>MainWindow</c> 里的自绘提示条（DECISIONS J9）。后来
/// 用户翻通知中心，发现**当天早些时候用另一个 AppId 测试的那条其实是送达的**
/// ——只是"请勿打扰"开着，横幅被吞了，但通知本身安静地躺在通知中心里。真正的问题
/// 从来不是"这条路走不通"，是**裸字符串 `"Windows PowerShell"` 不是一个真实注册过的
/// AppId**；PowerShell 控制台宿主真正的 AppId 是下面这个 GUID + 安装路径拼出来的
/// 字符串（社区里管这类东西叫 AppUserModelID，Windows 装 PowerShell 时会自动登记），
/// 用这个才对得上号。<see cref="Show"/> 因此**跟自绘提示条并存**，不是互斥的两条路——
/// 提示条保证屏幕上一定看得见，系统通知额外给一份"关掉程序也能事后翻看"的记录，
/// 但会不会弹横幅、弹不弹得出来，终究要看 Windows 自己的通知设置和"请勿打扰"状态，
/// 这个不确定性本来就在，不是 bug。
/// </summary>
public static class Notify
{
    private const string Title = "ItamiTimer";

    /// <summary>
    /// PowerShell 控制台宿主的真实 AppId——不是随便一个字符串都能让 Windows 认账。
    /// 这串 GUID 是 PowerShell 装机时在系统里登记的固定值，不是这个项目分配的。
    /// </summary>
    private const string PowerShellAppId = @"{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\WindowsPowerShell\v1.0\powershell.exe";

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
    /// 走 WinRT 的 <c>ToastNotificationManager</c>，从纯 PowerShell 里直接调，用
    /// <see cref="PowerShellAppId"/> 而不是裸字符串——这是这条路径真正能弹出来的关键。
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
            $"[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('{PowerShellAppId}').Show($toast)\n";

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
