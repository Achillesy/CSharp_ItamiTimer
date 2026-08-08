using ItamiTimer.App;
using ItamiTimer.Core;

namespace ItamiTimer.App.Tests;

/// <summary>
/// <see cref="Command.Execute"/> 必须把 rules.json 里那条命令**原样**交给 shell——
/// 尤其是命令自己带双引号的时候。
///
/// 这个测试是 2026-08-08 那个 bug 的回归防线（DECISIONS L1）：当时 macOS 分支把命令
/// 拼成一整条 <c>-c "{cmd}"</c> 的 <c>Arguments</c> 字符串，而 rules.json 里默认那几条
/// 全是 <c>osascript -e 'tell application "System Events" to ...'</c> —— 内层的双引号
/// 把外层引号提前闭合，.NET 再按 C 运行库规则切回 argv 时脚本就断了，sh 报
/// <c>unexpected EOF</c>、退出码 2。**闹钟那一侧一切正常**（触发了、也调用了
/// <c>Command.Execute</c>），只有命令自己安静地失败，日志不翻到 stderr 那行根本看不出来。
///
/// 所以这里断言的**不是"进程起来了"，而是命令的副作用真的发生了、且引号原封不动**——
/// "调用到了" 恰恰是当时唯一还正常的那一环，拿它当断言什么都测不出来。
///
/// 不测 <c>osascript</c> 之类的真实命令：那些要么有破坏性（重启/关机），要么依赖
/// macOS 的自动化授权。这里只要一条**带双引号、跨平台、无害、留下可验证痕迹**的命令。
/// </summary>
public class CommandQuotingTests
{
    /// <summary>命令里嵌的这段文字带一对双引号，正是当年被切碎的那个形状。</summary>
    private const string Quoted = "tell application \"System Events\" to restart";

    [Fact]
    public async Task ACommandContainingDoubleQuotesReachesTheShellIntact()
    {
        var marker = Path.Combine(Path.GetTempPath(), $"itami-cmd-{Guid.NewGuid():N}.txt");
        try
        {
            // 两个平台各写各的：Windows 走 cmd.exe 的 echo，Unix 走 sh 的 echo + 单引号。
            // 两条都把 Quoted 原样写进 marker 文件。
            var cmd = OperatingSystem.IsWindows()
                ? $"echo {Quoted}>\"{marker}\""
                : $"echo '{Quoted}' > \"{marker}\"";

            var os = OperatingSystem.IsWindows() ? "windows" : "macos";
            await Command.ExecuteAsync(GroupRules.Parse($$"""
                {
                  "groups": {},
                  "executeCommand": { "{{os}}": [{{System.Text.Json.JsonSerializer.Serialize(cmd)}}] }
                }
                """));

            // ExecuteAsync 已经 await 到子进程退出了，副作用这时必然落了盘。仍然留一个
            // 短轮询兜底：写盘和进程退出之间在某些文件系统上还隔着一次 flush。
            var text = await WaitForFile(marker, TimeSpan.FromSeconds(5));

            Assert.NotNull(text);
            // 关键断言：双引号必须**原样还在**。修复前这里拿到的是被 shell 拆碎的残句
            // （或者根本没有文件，因为 sh 直接语法错误退出了）。
            Assert.Contains(Quoted, text);
        }
        finally
        {
            try { File.Delete(marker); } catch { /* 残留的临时文件不该让测试失败 */ }
        }
    }

    /// <summary>轮询等文件出现并且写完（非空），超时返回 null。</summary>
    private static async Task<string?> WaitForFile(string path, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            try
            {
                if (File.Exists(path))
                {
                    var text = File.ReadAllText(path);
                    if (text.Trim().Length > 0) return text;
                }
            }
            catch (IOException) { /* 子进程还在写，下一轮再看 */ }
            await Task.Delay(100);
        }
        return null;
    }
}
