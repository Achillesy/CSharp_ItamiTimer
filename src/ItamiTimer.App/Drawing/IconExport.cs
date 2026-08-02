using Avalonia.Media.Imaging;

namespace ItamiTimer.App;

/// <summary>
/// Exports the tomato icon into the format each platform needs: Windows's <c>.ico</c>
/// (embedded into the exe's resources, see the csproj's ApplicationIcon), macOS's
/// <c>.iconset</c> directory (then compressed into a .icns by the system's
/// <c>iconutil</c> and dropped into the .app).
///
/// **The icon is still drawn in code**, no bitmap in the repository -- that rule runs all
/// the way from the dial down to here. This file's only job is "lay out the already-drawn
/// artwork in each platform's format"; it carries no art information of its own, all of
/// which lives in <see cref="TomatoIcon"/>. Changing the tomato's shape means editing that
/// file and re-exporting once.
///
/// Usage (both are **debug exits, not product features** -- the normal startup path isn't
/// touched at all):
/// <code>
/// ItamiTimer --export-icon    &lt;output path.ico&gt;     Windows
/// ItamiTimer --export-iconset &lt;output dir.iconset&gt;  macOS, then iconutil -c icns
/// </code>
///
/// The ICO holds PNG payloads, not BMP: Windows since Vista accepts PNG payloads, which
/// skips hand-writing a DIB header and an AND mask, and the 256x256 tier only fits in a PNG
/// anyway.
/// </summary>
internal static class IconExport
{
    /// <summary>Windows picks whichever of these tiers fits: 32 for the taskbar, 48 for large desktop icons, 256 for extra-large.</summary>
    private static readonly int[] Sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

    /// <summary>
    /// macOS's iconset tiers. **The filenames are a hard convention** -- <c>iconutil</c>
    /// only recognizes these exact names: each tier has a 1x and a 2x (@2x is just the next
    /// tier's pixel count), and the Dock, Finder, and Cmd-Tab each pick whichever they need.
    /// </summary>
    private static readonly (string Name, int Px)[] IconsetSizes =
    [
        ("icon_16x16.png", 16),      ("icon_16x16@2x.png", 32),
        ("icon_32x32.png", 32),      ("icon_32x32@2x.png", 64),
        ("icon_128x128.png", 128),   ("icon_128x128@2x.png", 256),
        ("icon_256x256.png", 256),   ("icon_256x256@2x.png", 512),
        ("icon_512x512.png", 512),   ("icon_512x512@2x.png", 1024),
    ];

    /// <summary>Lays out ten tiers of PNG into a .iconset directory. Compressing into .icns is iconutil's job (see pack-macos.sh).</summary>
    public static void WriteIconset(string dir)
    {
        Directory.CreateDirectory(dir);
        foreach (var (name, px) in IconsetSizes)
        {
            using var bmp = TomatoIcon.Render(px);
            using var f = File.Create(Path.Combine(dir, name));
#pragma warning disable CS0618 // Avalonia 12 marks this obsolete; the new overload wants an encoder-options object, and default PNG is good enough
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
#pragma warning disable CS0618 // Avalonia 12 marks this obsolete; the new overload wants an encoder-options object, and default PNG is good enough
            bmp.Save(ms);
#pragma warning restore CS0618
            pngs.Add(ms.ToArray());
        }

        using var f = File.Create(path);
        using var w = new BinaryWriter(f);

        // ICONDIR
        w.Write((short)0);                 // Reserved
        w.Write((short)1);                 // 1 = icon (2 would be a cursor)
        w.Write((short)Sizes.Length);

        // ICONDIRENTRY x N. Data is laid out starting right after the directory ends.
        var offset = 6 + Sizes.Length * 16;
        for (var i = 0; i < Sizes.Length; i++)
        {
            // 256 is written as 0 in this byte field -- a single byte can't hold 256, that's just how the spec defines it
            w.Write((byte)(Sizes[i] >= 256 ? 0 : Sizes[i]));
            w.Write((byte)(Sizes[i] >= 256 ? 0 : Sizes[i]));
            w.Write((byte)0);              // Palette count, 0 for true colour
            w.Write((byte)0);              // Reserved
            w.Write((short)1);             // Colour planes
            w.Write((short)32);            // Bit depth
            w.Write(pngs[i].Length);
            w.Write(offset);
            offset += pngs[i].Length;
        }

        foreach (var p in pngs) w.Write(p);
        Console.WriteLine($"Icon written to {path} ({Sizes.Length} sizes, {new FileInfo(path).Length / 1024} KB)");
    }
}
