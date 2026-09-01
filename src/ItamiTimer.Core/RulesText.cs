namespace ItamiTimer.Core;

/// <summary>
/// 对 <c>rules.json</c> 做**最小改动的文本编辑**：把 <c>executeCommand.{os}</c> 里的某一条
/// 挪到第一位（`itami --list` 用）。
///
/// ⚠️ **故意不走 JSON 序列化往返**。`rules.json` 是**用户手写的文件**（DESIGN §11），
/// 解析器明确允许注释和尾逗号；用 <c>JsonSerializer</c> 读出来再写回去，会把用户的注释、
/// 缩进、键的顺序**全部抹掉**——文件还是合法 JSON，程序也照常运作，但用户下次打开
/// 会发现自己写的说明没了。这类"不报错、只是安静地毁掉用户数据"正是这个项目反复栽的
/// 那一类，所以这里只做**定位 + 搬运原文**：每一条命令的文本原样保留，字节级不变。
///
/// 这不是一条新的解析路径（§15.4 的护栏是"一个类型模型、一个解析器"）：判定用的仍然是
/// <see cref="GroupRules.Parse"/>，这里只负责搬字符，从不解释命令的含义。
/// </summary>
public static class RulesText
{
    /// <summary>
    /// 把 <c>executeCommand.{os}</c> 数组里下标 <paramref name="index"/> 的那条挪到最前面，
    /// 返回改写后的**整份文件文本**。
    ///
    /// 返回 <c>null</c> = **拒绝改写**（调用方应原样保留文件并告诉用户手工改），发生在：
    /// 找不到该 os 的数组、下标越界、或者**数组里有注释**。最后一条是刻意的：一旦元素之间
    /// 夹着注释，搬动元素就必然要决定注释跟谁走，怎么猜都可能猜错——宁可不动。
    /// </summary>
    public static string? MoveToFront(string json, string os, int index)
    {
        if (index == 0) return json;   // 已经在第一位，什么都不用做
        if (!TryFindArray(json, os, out var open, out var close)) return null;

        var span = json[(open + 1)..close];
        if (span.Contains("//") || span.Contains("/*")) return null;   // 有注释，不碰

        var items = SplitTopLevel(json, open + 1, close);
        if (items is null || index < 0 || index >= items.Count) return null;

        // 原文的排版：`[` 到第一条之间、条与条之间、最后一条到 `]` 之间的空白各留一份，
        // 重排后照原样拼回去——这样只有顺序变了，缩进风格一个字符都没动。
        var prefix = json[(open + 1)..items[0].Start];
        var suffix = json[items[^1].End..close];
        var separator = items.Count > 1
            ? json[FindAfterComma(json, items[0].End)..items[1].Start]
            : "";

        var order = new List<(int Start, int End)>(items);
        var moved = order[index];
        order.RemoveAt(index);
        order.Insert(0, moved);

        var body = string.Join("," + separator, order.Select(r => json[r.Start..r.End]));
        return json[..(open + 1)] + prefix + body + suffix + json[close..];
    }

    /// <summary>逗号之后的第一个字符位置——条与条之间那段空白的起点。</summary>
    private static int FindAfterComma(string json, int from)
    {
        var i = json.IndexOf(',', from);
        return i < 0 ? from : i + 1;
    }

    /// <summary>
    /// 定位 <c>executeCommand</c> 里 <c>{os}</c> 那个数组的 <c>[</c> 和 <c>]</c>。
    /// 键名比较**不区分大小写**——跟 <see cref="GroupRules"/> 的解析设置保持一致，
    /// §15.4 那次事故里正是两边一个区分大小写一个不区分，才让文件一半好用一半失效。
    /// </summary>
    private static bool TryFindArray(string json, string os, out int open, out int close)
    {
        open = close = -1;

        var root = json.IndexOf("\"executeCommand\"", StringComparison.OrdinalIgnoreCase);
        if (root < 0) return false;

        var key = json.IndexOf($"\"{os}\"", root, StringComparison.OrdinalIgnoreCase);
        if (key < 0) return false;

        var i = json.IndexOf('[', key);
        if (i < 0) return false;
        open = i;

        // 找配对的 `]`：字符串里的方括号不算数，转义字符也要跳过。
        var inString = false;
        for (var p = open + 1; p < json.Length; p++)
        {
            var c = json[p];
            if (inString)
            {
                if (c == '\\') { p++; continue; }
                if (c == '"') inString = false;
            }
            else if (c == '"') inString = true;
            else if (c == '[') return false;          // 嵌套数组，超出这里该处理的形状
            else if (c == ']') { close = p; return true; }
        }
        return false;
    }

    /// <summary>
    /// 把数组内容按顶层逗号切成若干条，返回每一条**去掉两端空白之后**的起止位置。
    /// 位置而不是字符串：搬运时要原样引用原文，不能经过任何再加工。
    /// </summary>
    private static List<(int Start, int End)>? SplitTopLevel(string json, int from, int to)
    {
        var items = new List<(int, int)>();
        var inString = false;
        var start = -1;
        var lastNonWs = -1;

        void Flush()
        {
            if (start >= 0 && lastNonWs >= start) items.Add((start, lastNonWs + 1));
            start = -1; lastNonWs = -1;
        }

        for (var p = from; p < to; p++)
        {
            var c = json[p];
            if (inString)
            {
                if (c == '\\') { lastNonWs = ++p; continue; }
                if (c == '"') inString = false;
                lastNonWs = p;
                continue;
            }
            if (c == '"') { inString = true; if (start < 0) start = p; lastNonWs = p; continue; }
            if (c == ',') { Flush(); continue; }
            if (char.IsWhiteSpace(c)) continue;
            if (start < 0) start = p;
            lastNonWs = p;
        }
        Flush();

        return inString ? null : items;   // 引号没闭合 = 文件本身有问题，不改写
    }
}
