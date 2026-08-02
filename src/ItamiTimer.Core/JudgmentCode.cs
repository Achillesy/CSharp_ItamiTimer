namespace ItamiTimer.Core;

/// <summary>
/// 秒级判定码（DESIGN.md §3）。buffer 里存的就是它，1 字节一个。
///
/// ⚠️ <b>值的大小是「覆盖优先级」，不是「对用户有利的程度」</b>：值越小，画得越晚、越权威。
///
/// 按有利程度排，<see cref="Afk"/>（空白、不怪你）本该排在 <see cref="OffTask"/>
/// （红、全怪你）之上；但 afk 必须<b>最后画</b>才能盖住窗口判定——人不在的时候窗口是
/// 什么无所谓——所以它的值必须更小。谁按「有利程度」去调换这两个，afk 优先级会
/// <b>悄悄失效且不报错</b>（DECISIONS H1）。
///
/// 判「这一秒算不算专注」请一律写成 <c>&gt;= Focused</c>，不要列举码值：
/// <b>计入专注 ⟺ 码 ≥ Focused(4)</b>。将来加码也不会漏掉某一处。
/// </summary>
public enum JudgmentCode : byte
{
    /// <summary>还没画过。不计入、不绘制。</summary>
    Init = 0,

    /// <summary>预计还要花的时间（承诺弧）。每拍重算，见 <see cref="JudgmentBuffer.RefreshGray"/>。</summary>
    Gray = 1,

    /// <summary>AW 的 afk 说人不在。不计入，但也不怪你（§0.4.1）。</summary>
    Afk = 2,

    /// <summary>有窗口事件，不命中所选小目标。fail-closed。</summary>
    OffTask = 3,

    /// <summary>有窗口事件，命中所选小目标。</summary>
    Focused = 4,

    /// <summary>
    /// 这一秒 AW 没有任何记录——整拍连不上，或者连上了但这一秒没事件。
    /// <b>计入专注</b>：拿不出数据是 AW 自己的毛病，不该让用户替它挨罚（§3.1，知情的 fail-open）。
    /// </summary>
    AwOffline = 5,
}
