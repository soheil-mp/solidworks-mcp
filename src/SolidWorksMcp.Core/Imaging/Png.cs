using System.Buffers.Binary;
using System.IO.Compression;

namespace SolidWorksMcp.Core.Imaging;

/// <summary>
/// Minimal, dependency-free image helpers: read an uncompressed 24/32-bit BMP (what <c>IModelDoc2.SaveBMP</c> writes),
/// downscale with area averaging, and encode an 8-bit RGB PNG.
/// </summary>
public static class Png
{
    /// <summary>Converts BMP bytes to PNG, downscaling so that the longer edge is at most <paramref name="maxEdge"/>.</summary>
    public static byte[] FromBmp(ReadOnlySpan<byte> bmp, int maxEdge, out int width, out int height)
    {
        var (w, h, rgb) = ReadBmp(bmp);
        if (maxEdge > 0 && Math.Max(w, h) > maxEdge)
        {
            double s = (double)maxEdge / Math.Max(w, h);
            int nw = Math.Max(1, (int)Math.Round(w * s)), nh = Math.Max(1, (int)Math.Round(h * s));
            rgb = Resize(rgb, w, h, nw, nh);
            w = nw;
            h = nh;
        }
        width = w;
        height = h;
        return Encode(rgb, w, h);
    }

    /// <summary>True when every pixel is the same colour (a blank capture).</summary>
    public static bool IsUniform(byte[] rgb)
    {
        for (int i = 3; i < rgb.Length; i++)
            if (rgb[i] != rgb[i % 3]) return false;
        return true;
    }

    public static (int W, int H, byte[] Rgb) ReadBmp(ReadOnlySpan<byte> b)
    {
        if (b.Length < 54 || b[0] != (byte)'B' || b[1] != (byte)'M') throw new InvalidDataException("Not a BMP file.");
        int offset = BinaryPrimitives.ReadInt32LittleEndian(b[10..]);
        int w = BinaryPrimitives.ReadInt32LittleEndian(b[18..]);
        int hRaw = BinaryPrimitives.ReadInt32LittleEndian(b[22..]);
        int bpp = BinaryPrimitives.ReadUInt16LittleEndian(b[28..]);
        int compression = BinaryPrimitives.ReadInt32LittleEndian(b[30..]);
        if (bpp is not (24 or 32) || compression is not (0 or 3)) throw new InvalidDataException($"Unsupported BMP ({bpp} bpp, compression {compression}).");
        bool bottomUp = hRaw > 0;
        int h = Math.Abs(hRaw);
        int stride = ((bpp * w + 31) / 32) * 4;
        int bytesPer = bpp / 8;
        var rgb = new byte[w * h * 3];
        for (int y = 0; y < h; y++)
        {
            int srcRow = offset + (bottomUp ? h - 1 - y : y) * stride;
            for (int x = 0; x < w; x++)
            {
                int s = srcRow + x * bytesPer, d = (y * w + x) * 3;
                rgb[d] = b[s + 2];
                rgb[d + 1] = b[s + 1];
                rgb[d + 2] = b[s];
            }
        }
        return (w, h, rgb);
    }

    /// <summary>Area-averaging downscale (good quality for UI captures; no ringing).</summary>
    public static byte[] Resize(byte[] src, int sw, int sh, int dw, int dh)
    {
        var dst = new byte[dw * dh * 3];
        double fx = (double)sw / dw, fy = (double)sh / dh;
        for (int y = 0; y < dh; y++)
        {
            int y0 = (int)(y * fy), y1 = Math.Max(y0 + 1, (int)((y + 1) * fy));
            for (int x = 0; x < dw; x++)
            {
                int x0 = (int)(x * fx), x1 = Math.Max(x0 + 1, (int)((x + 1) * fx));
                long r = 0, g = 0, bl = 0, n = 0;
                for (int yy = y0; yy < y1 && yy < sh; yy++)
                    for (int xx = x0; xx < x1 && xx < sw; xx++)
                    {
                        int i = (yy * sw + xx) * 3;
                        r += src[i]; g += src[i + 1]; bl += src[i + 2]; n++;
                    }
                int o = (y * dw + x) * 3;
                if (n == 0) n = 1;
                dst[o] = (byte)(r / n); dst[o + 1] = (byte)(g / n); dst[o + 2] = (byte)(bl / n);
            }
        }
        return dst;
    }

    public static byte[] Encode(byte[] rgb, int w, int h)
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, w);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 2;  // colour type RGB
        Chunk(ms, "IHDR", ihdr);

        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
        {
            int stride = w * 3;
            var row = new byte[stride + 1];
            for (int y = 0; y < h; y++)
            {
                row[0] = 1; // filter "Sub": cheap and effective on flat UI colours
                int s = y * stride;
                for (int i = 0; i < stride; i++)
                {
                    byte left = i >= 3 ? rgb[s + i - 3] : (byte)0;
                    row[i + 1] = (byte)(rgb[s + i] - left);
                }
                z.Write(row);
            }
        }
        Chunk(ms, "IDAT", raw.ToArray());
        Chunk(ms, "IEND", []);
        return ms.ToArray();
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        var t = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(t);
        s.Write(data);
        uint crc = Crc32(t, 0xFFFFFFFF);
        crc = Crc32(data, crc) ^ 0xFFFFFFFF;
        Span<byte> c = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(c, crc);
        s.Write(c);
    }

    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    private static uint Crc32(byte[] data, uint crc)
    {
        foreach (var b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }
}
