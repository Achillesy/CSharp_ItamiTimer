using Avalonia.Media.Imaging;

namespace ItamiTimer.App;

/// <summary>
/// 把番茄图标导成 Windows 的 .ico，装进 exe 的资源里（csproj 的 ApplicationIcon）。
///
/// **图标仍然是代码画出来的**，仓库里不放位图 —— 这条纪律从表盘一路贯到这里。
/// 这个文件的作用只是"把画好的东西按 ICO 的格式排一遍"，不含任何美术信息，
/// 美术信息全在 <see cref="TomatoIcon"/>。
///
/// 用法：<c>ItamiTimer.exe --export-icon &lt;输出路径.ico&gt;</c>，跟 --dial-specimens
/// 一样是**调试出口，不是产品功能**，正常启动路径一个字节都没碰。
///
/// ICO 里塞的是 PNG 而不是 BMP：Vista 之后的 Windows 认 PNG 负载，省掉手写
/// DIB 头和 AND 掩码那一堆东西，256×256 那一档也只有 PNG 才装得下。
/// </summary>
internal static class IconExport
{
    /// <summary>Windows 会从这几档里挑最合适的：任务栏 32、桌面大图标 48、超大 256。</summary>
    private static readonly int[] Sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

    public static void Write(string path)
    {
        var pngs = new List<byte[]>();
        foreach (var s in Sizes)
        {
            using var bmp = TomatoIcon.Render(s);
            using var ms = new MemoryStream();
#pragma warning disable CS0618 // Avalonia 12 标了过时，新重载要编码器选项对象；默认 PNG 就够
            bmp.Save(ms);
#pragma warning restore CS0618
            pngs.Add(ms.ToArray());
        }

        using var f = File.Create(path);
        using var w = new BinaryWriter(f);

        // ICONDIR
        w.Write((short)0);                 // 保留
        w.Write((short)1);                 // 1 = 图标（2 才是光标）
        w.Write((short)Sizes.Length);

        // ICONDIRENTRY ×N。数据从目录结束处开始往后排。
        var offset = 6 + Sizes.Length * 16;
        for (var i = 0; i < Sizes.Length; i++)
        {
            // 256 在这个字节字段里写 0 —— 一个字节存不下 256，规范就是这么定的
            w.Write((byte)(Sizes[i] >= 256 ? 0 : Sizes[i]));
            w.Write((byte)(Sizes[i] >= 256 ? 0 : Sizes[i]));
            w.Write((byte)0);              // 调色板数，真彩色写 0
            w.Write((byte)0);              // 保留
            w.Write((short)1);             // 颜色平面
            w.Write((short)32);            // 位深
            w.Write(pngs[i].Length);
            w.Write(offset);
            offset += pngs[i].Length;
        }

        foreach (var p in pngs) w.Write(p);
        Console.WriteLine($"图标已写入 {path}（{Sizes.Length} 档，{new FileInfo(path).Length / 1024} KB）");
    }
}
