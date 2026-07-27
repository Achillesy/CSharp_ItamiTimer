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
    /// <summary><c>%LOCALAPPDATA%\ItamiTimer</c>。settings.json 和（仅 Debug 的）日志都在这里。</summary>
    public static string Dir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ItamiTimer");

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
}
