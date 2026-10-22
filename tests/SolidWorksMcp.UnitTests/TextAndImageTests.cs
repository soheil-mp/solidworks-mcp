using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;
using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Imaging;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Services;
using Xunit;

namespace SolidWorksMcp.UnitTests;

public class PngTests
{
    private static byte[] Bmp24(int w, int h, Func<int, int, (byte R, byte G, byte B)> px)
    {
        int stride = ((24 * w + 31) / 32) * 4;
        var b = new byte[54 + stride * h];
        b[0] = (byte)'B'; b[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(2), b.Length);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(10), 54);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(18), w);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(22), h); // bottom-up
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(28), 24);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var (r, g, bl) = px(x, y);
                int o = 54 + (h - 1 - y) * stride + x * 3;
                b[o] = bl; b[o + 1] = g; b[o + 2] = r;
            }
        return b;
    }

    [Fact]
    public void Bmp_to_png_keeps_pixels_and_has_valid_chunks()
    {
        var bmp = Bmp24(3, 2, (x, y) => ((byte)(x * 100), (byte)(y * 200), 7));
        var (w, h, rgb) = Png.ReadBmp(bmp);
        Assert.Equal((3, 2), (w, h));
        Assert.Equal(new byte[] { 200, 0, 7 }, rgb[6..9]);   // top-down: (2, 0)
        Assert.Equal(new byte[] { 0, 200, 7 }, rgb[9..12]);  // (0, 1)

        var png = Png.FromBmp(bmp, 0, out int pw, out int ph);
        Assert.Equal((3, 2), (pw, ph));
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);
        // Decode IDAT and undo the Sub filter: the pixels must round-trip.
        int idat = IndexOf(png, "IDAT"u8.ToArray());
        int len = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(idat - 4));
        using var z = new ZLibStream(new MemoryStream(png, idat + 4, len), CompressionMode.Decompress);
        using var ms = new MemoryStream();
        z.CopyTo(ms);
        var raw = ms.ToArray();
        Assert.Equal(2 * (1 + 9), raw.Length);
        var row1 = raw[11..20];
        for (int i = 3; i < 9; i++) row1[i] = (byte)(row1[i] + row1[i - 3]);
        Assert.Equal(new byte[] { 0, 200, 7, 100, 200, 7, 200, 200, 7 }, row1);
    }

    [Fact]
    public void Downscales_to_the_longest_edge()
    {
        var bmp = Bmp24(400, 100, (_, _) => (10, 20, 30));
        Png.FromBmp(bmp, 200, out int w, out int h);
        Assert.Equal((200, 50), (w, h));
    }

    private static int IndexOf(byte[] hay, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= hay.Length; i++)
            if (hay.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;
        return -1;
    }
}

public class VbaTextTests
{
    [Fact]
    public void Non_ascii_in_strings_becomes_ChrW_and_the_file_is_ascii()
    {
        string code = VbaText.EscapeNonAnsi("Echo \"\u00D86 \u4E2D\"");
        Assert.Equal("Echo \"\" & ChrW(216) & \"6 \" & ChrW(20013) & \"\"", code);
        Assert.All(VbaText.Wrap("Echo 1", @"C:\t\out.txt", "Part1"), c => Assert.True(c < 128));
    }

    [Fact]
    public void Non_ascii_outside_strings_is_rejected() =>
        Assert.Equal(ErrorCodes.InvalidArgument, Assert.Throws<SwException>(() => VbaText.EscapeNonAnsi("Dim \u00D8 As Long")).Code);

    [Fact]
    public void Output_escapes_decode()
    {
        Assert.Equal("\u00D8 6 \u2300", VbaText.Unescape(@"\u00D8 6 \u2300"));
        Assert.Equal("a\\b", VbaText.Unescape(@"a\u005Cb"));
        Assert.Equal("plain", VbaText.Unescape("plain"));
        Assert.Equal(@"\u12", VbaText.Unescape(@"\u12")); // truncated escape left alone
    }

    [Fact]
    public void Wrapper_traps_errors_and_binds_the_document()
    {
        string w = VbaText.Wrap("x = 1", @"C:\t\o.txt", @"C:\parts\a.sldprt");
        Assert.Contains("On Error GoTo mcpErr", w);
        Assert.Contains("GetOpenDocumentByName(\"C:\\parts\\a.sldprt\")", w);
        Assert.Contains("Sub mcpUser()\r\nx = 1", w.Replace("\n", "\r\n").Replace("\r\r\n", "\r\n"));
    }
}

public class ModelJsonTests
{
    [Fact]
    public void EntityRef_where_is_parsed_strictly()
    {
        var r = JsonSerializer.Deserialize<EntityRef>("""{"where":{"curve":"line","parallelTo":"y"},"expect":4}""", JsonSerializerOptions.Web)!;
        var f = r.WhereFilter("edges[0]")!;
        Assert.Equal("line", f.Curve);
        Assert.Equal("y", f.ParallelTo);

        var bad = JsonSerializer.Deserialize<EntityRef>("""{"where":{"curvee":"line"}}""", JsonSerializerOptions.Web)!;
        var ex = Assert.Throws<SwException>(() => bad.WhereFilter("edges[0]"));
        Assert.Equal(ErrorCodes.InvalidArgument, ex.Code);
        Assert.Contains("curvee", ex.Message);
    }

    [Fact]
    public void Results_are_compact_camel_case_without_nulls()
    {
        var json = ResultJson.Serialize(new FeatureRef { Name = "Fillet1", Type = "Fillet" });
        Assert.Equal("""{"name":"Fillet1","type":"Fillet"}""", json);
    }

    [Fact]
    public void Error_text_starts_with_the_code()
    {
        var e = new SwException(ErrorCodes.EntityStale, "gone", "re-list");
        Assert.Equal("ENTITY_STALE: gone Hint: re-list", e.ToAgentText());
    }
}
