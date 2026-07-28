using Avalonia.Media.Imaging;

namespace ItamiTimer.App;

/// <summary>
/// 把番茄图标导成两个平台各自要的格式：Windows 的 <c>.ico</c>（装进 exe 的资源，
/// 见 csproj 的 ApplicationIcon）、macOS 的 <c>.iconset</c> 目录（再由系统的
/// <c>iconutil</c> 压成 .icns 放进 .app）。
///
/// **图标仍然是代码画出来的**，仓库里不放位图 —— 这条纪律从表盘一路贯到这里。
/// 这个文件的作用只是"把画好的东西按各家的格式排一遍"，不含任何美术信息，
/// 美术信息全在 <see cref="TomatoIcon"/>。番茄的形状要改就去改那个文件，重导一次。
///
/// 用法（都是**调试出口，不是产品功能**，正常启动路径一个字节都没碰）：
/// <code>
/// ItamiTimer --export-icon    &lt;输出路径.ico&gt;     Windows
/// ItamiTimer --export-iconset &lt;输出目录.iconset&gt;  macOS，之后 iconutil -c icns
/// </code>
///
/// ICO 里塞的是 PNG 而不是 BMP：Vista 之后的 Windows 认 PNG 负载，省掉手写
/// DIB 头和 AND 掩码那一堆东西，256×256 那一档也只有 PNG 才装得下。
/// </summary>
internal static class IconExport
{
    /// <summary>Windows 会从这几档里挑最合适的：任务栏 32、桌面大图标 48、超大 256。</summary>
    private static readonly int[] Sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

    /// <summary>
    /// macOS 的 iconset 档位。**文件名是硬约定**，<c>iconutil</c> 只认这些名字：
    /// 每一档都有 1x 和 2x（@2x 就是下一档的像素数），Dock、访达、Cmd-Tab 各取所需。
    /// </summary>
    private static readonly (string Name, int Px)[] IconsetSizes =
    [
        ("icon_16x16.png", 16),      ("icon_16x16@2x.png", 32),
        ("icon_32x32.png", 32),      ("icon_32x32@2x.png", 64),
        ("icon_128x128.png", 128),   ("icon_128x128@2x.png", 256),
        ("icon_256x256.png", 256),   ("icon_256x256@2x.png", 512),
        ("icon_512x512.png", 512),   ("icon_512x512@2x.png", 1024),
    ];

    /// <summary>把十档 PNG 铺进一个 .iconset 目录。压成 .icns 是 iconutil 的事（见 pack-macos.sh）。</summary>
    public static void WriteIconset(string dir)
    {
        Directory.CreateDirectory(dir);
        foreach (var (name, px) in IconsetSizes)
        {
            using var bmp = TomatoIcon.Render(px);
            using var f = File.Create(Path.Combine(dir, name));
#pragma warning disable CS0618 // Avalonia 12 标了过时，新重载要编码器选项对象；默认 PNG 就够
            bmp.Save(f);
#pragma warning restore CS0618
        }
        Console.WriteLine($"iconset written to {dir} ({IconsetSizes.Length} sizes)");
    }

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
        Console.WriteLine($"Icon written to {path} ({Sizes.Length} sizes, {new FileInfo(path).Length / 1024} KB)");
    }
}
