using System.Text;
using ItamiTimer.App;

namespace ItamiTimer.App.Tests;

/// <summary>
/// Header parsing for <see cref="Sound.Duration"/> -- the spacing between the alarm's four
/// rings comes from here, so a wrong answer shows up as a stuttering or a dragging alarm.
///
/// The files are **built byte by byte in the test**, not shipped as fixtures: the repo
/// bundles no audio (DECISIONS D5), and the real system sounds differ between machines and
/// between the two platforms -- a test that reads `C:\Windows\Media` can't run on macOS and
/// can't assert a number.
/// </summary>
public class SoundDurationTests
{
    /// <summary>Writes bytes to a temp file, runs the parser, cleans up.</summary>
    private static TimeSpan? DurationOf(byte[] bytes, string ext)
    {
        var path = Path.Combine(Path.GetTempPath(), $"itami-test-{Guid.NewGuid():N}{ext}");
        try
        {
            File.WriteAllBytes(path, bytes);
            return Sound.Duration(path);
        }
        finally
        {
            try { File.Delete(path); } catch { /* A leftover temp file doesn't fail the test */ }
        }
    }

    // ---- RIFF/WAVE ----

    [Fact]
    public void AWav_IsDataBytesDividedByByteRate()
    {
        // 44.1kHz, mono, 16-bit -> 88200 bytes per second; 132300 bytes = 1.5 seconds
        var d = DurationOf(Wav(44100, 1, 16, dataBytes: 132300), ".wav");
        Assert.Equal(1.5, d!.Value.TotalSeconds, 3);
    }

    [Fact]
    public void AWav_StillParsesWithAnOddSizedChunkBeforeData()
    {
        // `C:\Windows\Media` ships wavs with a LIST chunk in front of the audio. An odd size
        // there means one pad byte -- miss it and every following chunk offset is off by one.
        var d = DurationOf(Wav(22050, 2, 16, dataBytes: 88200, junkBefore: 5), ".wav");
        Assert.Equal(1.0, d!.Value.TotalSeconds, 3);
    }

    [Fact]
    public void AWavWhoseByteRateIsZero_IsUnknownRatherThanADivideByZero()
    {
        Assert.Null(DurationOf(Wav(0, 1, 16, dataBytes: 1000), ".wav"));
    }

    // ---- AIFF ----

    [Fact]
    public void AnAiff_IsFramesDividedBySampleRate()
    {
        var d = DurationOf(Aiff(44100, frames: 22050), ".aiff");
        Assert.Equal(0.5, d!.Value.TotalSeconds, 3);
    }

    [Fact]
    public void AnAiff_ReadsTheSampleRateOutOfItsEightyBitExtendedFloat()
    {
        // The one field with a format no .NET type maps to. 11025Hz has a different
        // exponent from 44100Hz, so getting the bias wrong can't pass both.
        var d = DurationOf(Aiff(11025, frames: 11025), ".aiff");
        Assert.Equal(1.0, d!.Value.TotalSeconds, 3);
    }

    // ---- Anything else falls back rather than throwing ----

    [Fact]
    public void SomethingThatIsNotASoundFile_IsUnknown()
    {
        Assert.Null(DurationOf(Encoding.ASCII.GetBytes("this is not audio at all"), ".wav"));
    }

    [Fact]
    public void ATruncatedHeader_IsUnknown()
    {
        var full = Wav(44100, 1, 16, dataBytes: 44100);
        Assert.Null(DurationOf(full[..20], ".wav"));
    }

    [Fact]
    public void AMissingFile_IsUnknown()
    {
        Assert.Null(Sound.Duration(Path.Combine(Path.GetTempPath(), "itami-no-such-file.wav")));
    }

    // ---- Builders ----

    /// <summary>A minimal RIFF/WAVE: optional junk chunk, `fmt `, then `data` filled with silence.</summary>
    private static byte[] Wav(int sampleRate, short channels, short bits, int dataBytes, int junkBefore = 0)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8);
        w.Write(0);                                  // Container size: the parser ignores it
        w.Write("WAVE"u8);

        if (junkBefore > 0)
        {
            w.Write("LIST"u8);
            w.Write(junkBefore);
            w.Write(new byte[junkBefore]);
            if ((junkBefore & 1) != 0) w.Write((byte)0);   // Pad to an even length
        }

        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)1);                           // PCM
        w.Write(channels);
        w.Write(sampleRate);
        w.Write(sampleRate * channels * bits / 8);   // Byte rate
        w.Write((short)(channels * bits / 8));       // Block align
        w.Write(bits);

        w.Write("data"u8);
        w.Write(dataBytes);
        w.Write(new byte[dataBytes]);
        return ms.ToArray();
    }

    /// <summary>A minimal FORM/AIFF: big-endian throughout, one `COMM` chunk, an empty `SSND`.</summary>
    private static byte[] Aiff(double sampleRate, uint frames)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("FORM"u8);
        w.Write(0);
        w.Write("AIFF"u8);

        w.Write("COMM"u8);
        w.Write(Be(18));
        w.Write(Be((short)1));                       // Channels
        w.Write(Be((int)frames));
        w.Write(Be((short)16));                      // Bit depth
        w.Write(Ext80(sampleRate));

        w.Write("SSND"u8);
        w.Write(Be(0));
        return ms.ToArray();
    }

    private static byte[] Be(int v) => [.. BitConverter.GetBytes(v).Reverse()];
    private static byte[] Be(short v) => [.. BitConverter.GetBytes(v).Reverse()];

    /// <summary>
    /// The inverse of <c>Sound.Extended80</c>: 15 exponent bits (bias 16383) plus 64
    /// **explicit** mantissa bits, normalized so the top bit is set. Written independently
    /// here on purpose -- a shared helper would let one sign error cancel itself out.
    /// </summary>
    private static byte[] Ext80(double v)
    {
        var exponent = (int)Math.Floor(Math.Log2(v));
        var mantissa = (ulong)Math.Round(Math.ScaleB(v, 63 - exponent));
        var biased = (ushort)(exponent + 16383);

        var b = new byte[10];
        b[0] = (byte)(biased >> 8);
        b[1] = (byte)biased;
        for (var i = 0; i < 8; i++) b[2 + i] = (byte)(mantissa >> (56 - 8 * i));
        return b;
    }
}
