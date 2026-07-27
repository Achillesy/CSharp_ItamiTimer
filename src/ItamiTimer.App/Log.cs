using System.Diagnostics;
using System.Text;

namespace ItamiTimer.App;

/// <summary>
/// 诊断日志。
///
/// **界面对用户是沉默的**（分割线以下一个提示字都没有，用户 2026-07-27 定），
/// 但沉默不等于把原因丢掉 —— 出错的**理由必须留下来**，否则程序坏了就成了黑箱：
/// 用户只看到按钮灰着，而谁也说不出为什么。
///
/// ⚠️ **这是本程序唯一会写盘的东西**，跟 DESIGN.md §8.1「完全不写盘」并不冲突：
/// 那一条禁的是**任务状态**落盘（不要 current-task.json、不要累加值、退出即放弃），
/// 目的是让状态永远由 AW 历史推导。日志是另一类东西 —— 它只往后追加、从不被读回来
/// 参与任何判定，删掉它不影响程序的任何行为。
///
/// 写不进去就算了：**日志本身绝不能把程序搞挂**。
/// </summary>
public static class Log
{
    private const long MaxBytes = 1 * 1024 * 1024;
    private static readonly Lock Gate = new();

    public static string Directory => AppData.Dir;

    public static string Path_ => System.IO.Path.Combine(Directory, "itami.log");

    /// <summary>
    /// <b>Release 下一个字都不写</b>（用户 2026-07-28：「log 文件只有 Debug 才写，
    /// Release 版本不必了」）。
    ///
    /// 用 <c>[Conditional("DEBUG")]</c> 而不是方法里判一个开关：这样 Release 编译时
    /// <b>整个调用连同实参一起消失</b> —— 那行每分钟一次的日志有一大串字符串插值，
    /// 运行时判断的话字符串照拼不误，只是拼完扔掉。
    ///
    /// ⚠️ 代价说清楚：界面对用户是<b>沉默</b>的（分割线以下一个提示字都没有），
    /// 日志原本是<b>唯一</b>能让人事后看出"它到底怎么了"的地方（§8.1a）。Release 下
    /// 出了错 —— AW 连不上、rules.json 写坏、抛异常 —— 屏幕上只有一个灰按钮，
    /// 没有任何线索，用户和我都无从查起。
    ///
    /// 想留一条退路的话：把 <see cref="Error"/> 上的 <c>[Conditional]</c> 去掉即可。
    /// 正常一轮任务照样零写入（没有 ERROR 就没有行），出事时又有据可查。
    /// </summary>
    [Conditional("DEBUG")]
    public static void Info(string message) => Write("INFO ", message);

    [Conditional("DEBUG")]
    public static void Warn(string message) => Write("WARN ", message);

    /// <summary>记一条错误。**异常的完整信息一定要进去** —— 界面上那句话是不会有的。</summary>
    [Conditional("DEBUG")]
    public static void Error(string what, Exception e)
        => Write("ERROR", $"{what}: {e.GetType().Name}: {e.Message}"
                          + (e.InnerException is { } inner ? $"  <- {inner.GetType().Name}: {inner.Message}" : ""));

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(Directory);
                Roll();
                File.AppendAllText(Path_,
                    $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}  {level}  {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch
        {
            // 日志写不进去就算了。绝不能因为记不了日志而把程序搞挂 ——
            // 那会把一个小毛病升级成崩溃。
        }
    }

    /// <summary>超过 1MB 就滚一次，只留一份旧的。长期开着也不会把盘吃光。</summary>
    private static void Roll()
    {
        var f = new FileInfo(Path_);
        if (!f.Exists || f.Length < MaxBytes) return;
        var old = Path_ + ".old";
        if (File.Exists(old)) File.Delete(old);
        File.Move(Path_, old);
    }
}
