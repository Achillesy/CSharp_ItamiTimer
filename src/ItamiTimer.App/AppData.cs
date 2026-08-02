using System.Text.Encodings.Web;
using System.Text.Json;

namespace ItamiTimer.App;

/// <summary>
/// 程序自己那点东西放哪儿。
///
/// 单独拎出来是因为 <see cref="Log"/> 在 Release 下整个是空操作（用户 2026-07-28），
/// 而 <see cref="Settings"/> 仍然要往同一个目录写 settings.json —— 让配置去依赖
/// 一个已经不干活的日志类，是等着以后踩的坑。
/// </summary>
public static class AppData
{
    /// <summary>
    /// settings.json 和（仅 Debug 的）日志放哪儿。
    ///
    /// <code>
    /// Windows   %LOCALAPPDATA%\ItamiTimer
    /// macOS     ~/Library/Application Support/ItamiTimer
    /// </code>
    ///
    /// **macOS 上不能直接用 <c>SpecialFolder.LocalApplicationData</c>**：.NET 在
    /// 类 Unix 系统上把它映射到 XDG 的 <c>~/.local/share</c>，那是个从 Finder 里
    /// 根本看不见的隐藏目录。用户要去改自己那份 rules.json 时得先会按 ⇧⌘. ——
    /// 这个程序的配置本来就指望用户自己去编辑（§8.1 那条链子只读不写），
    /// 藏起来等于把那条路堵死。
    /// </summary>
    public static string Dir { get; } = OperatingSystem.IsMacOS()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                       "Library", "Application Support", "ItamiTimer")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                       "ItamiTimer");

    /// <summary>
    /// 规则文件按三级找，**绝不只看当前工作目录**。
    ///
    /// <code>
    /// 1. %LOCALAPPDATA%\ItamiTimer\rules.json   ← 用户自己的，跟 settings.json 放一起
    /// 2. &lt;exe 所在目录&gt;\rules.json             ← 随程序发布的默认
    /// 3. .\rules.json                            ← 开发时从仓库根目录跑
    /// </code>
    ///
    /// **第 1 级存在的理由**：重新发布会覆盖 exe 旁边那份，用户加的小目标就没了。
    /// 把他那份放在这里，发布多少次都碰不到。删掉它就自动退回默认规则，很温和。
    ///
    /// **不看工作目录优先**：桌面快捷方式的"起始位置"可以是任何东西，按工作目录
    /// 找的话程序会时灵时不灵。2026-07-28 建 Release 快捷方式时才发现这个坑 ——
    /// 此前所有测试都恰好从仓库根目录启动，工作目录一直正好是对的。
    ///
    /// **这一级链条只读不写**，程序不会自己去铺第 1 级那份文件（§8.1）。
    /// </summary>
    public static string RulesPath()
    {
        var mine = Path.Combine(Dir, "rules.json");
        if (File.Exists(mine)) return mine;

        var beside = Path.Combine(AppContext.BaseDirectory, "rules.json");
        return File.Exists(beside) ? beside : "rules.json";
    }

    /// <summary>
    /// **程序自己那两个文件**（settings.json / during.json）怎么写。
    ///
    /// 一份，不是两份——原来 `Settings` 和 `During` 各写了一套一模一样的（连注释都一样），
    /// 那正是「同一件事写两遍、改一处忘另一处」的种子（§15.4 的 `executeCommand` 就是
    /// 那么长出来的）。
    ///
    /// 不转义非 ASCII：这两个是**给人看**的文件，小目标名是中文，默认编码器会把
    /// 「学习经济学」写成 学习...，想手动清零都认不出是哪一行。
    /// （`Unsafe` 指的是不为 HTML 上下文转义；这两份文件只被自己读写，不进任何网页。）
    ///
    /// ⚠️ 这**不能**用来读写 `rules.json`——那是用户手写的，程序只读不写，
    /// 而且它的解析设置在 `GroupRules` 里（注释、结尾逗号、大小写不敏感）。
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
