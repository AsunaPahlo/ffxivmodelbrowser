using System;

namespace BgModelBrowser;

public static class TexDecoder
{
    // FFXIV .tex header layout:
    //   Offset 0:  uint32 Attributes/Flags
    //   Offset 4:  uint32 Format
    //   Offset 8:  uint16 Width
    //   Offset 10: uint16 Height
    //   Offset 12: uint16 Depth
    //   Offset 14: uint16 MipLevels
    //   Offset 16: uint32[3] LodOffset (12 bytes)
    //   Offset 28: uint32[13] OffsetToSurface (52 bytes)
    //   Total header: 80 bytes

    private const int FmtBgra8 = 0x1450;
    private const int FmtBc1 = 0x3420;
    private const int FmtBc2 = 0x3430;
    private const int FmtBc3 = 0x3431;
    private const int FmtBc5 = 0x6230;
    private const int FmtBc7 = 0x6231;

    public static byte[]? DecodeTexFile(byte[] fileData, out int width, out int height)
    {
        width = height = 0;
        if (fileData.Length < 80) return null;

        var format = BitConverter.ToInt32(fileData, 4);
        width = BitConverter.ToUInt16(fileData, 8);
        height = BitConverter.ToUInt16(fileData, 10);

        if (width <= 0 || height <= 0 || width > 4096 || height > 4096) return null;

        // First mip surface offset (relative to byte 80)
        var surfaceOffset = BitConverter.ToUInt32(fileData, 28);
        var dataStart = (int)(80 + surfaceOffset);
        if (dataStart >= fileData.Length) return null;

        var imageData = fileData.AsSpan(dataStart);

        try
        {
            return format switch
            {
                FmtBgra8 => DecodeBgra(imageData, width, height),
                FmtBc1 => DecodeBc1(imageData, width, height),
                FmtBc2 => DecodeBc2(imageData, width, height),
                FmtBc3 => DecodeBc3(imageData, width, height),
                FmtBc5 => DecodeBc5(imageData, width, height),
                // BC7 is very complex to decode — skip for now
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    private static byte[] DecodeBgra(ReadOnlySpan<byte> data, int w, int h)
    {
        var size = w * h * 4;
        if (data.Length < size) return null!;
        return data[..size].ToArray();
    }

    private static byte[] DecodeBc1(ReadOnlySpan<byte> data, int w, int h)
    {
        var output = new byte[w * h * 4];
        var bx = (w + 3) / 4;
        var by = (h + 3) / 4;
        var offset = 0;

        for (int y = 0; y < by; y++)
        {
            for (int x = 0; x < bx; x++)
            {
                if (offset + 8 > data.Length) return output;
                DecodeBc1Block(data[offset..], output, x * 4, y * 4, w, h);
                offset += 8;
            }
        }
        return output;
    }

    private static byte[] DecodeBc2(ReadOnlySpan<byte> data, int w, int h)
    {
        var output = new byte[w * h * 4];
        var bx = (w + 3) / 4;
        var by = (h + 3) / 4;
        var offset = 0;

        for (int y = 0; y < by; y++)
        {
            for (int x = 0; x < bx; x++)
            {
                if (offset + 16 > data.Length) return output;
                DecodeBc1Block(data[(offset + 8)..], output, x * 4, y * 4, w, h);
                DecodeBc2Alpha(data[offset..], output, x * 4, y * 4, w, h);
                offset += 16;
            }
        }
        return output;
    }

    private static byte[] DecodeBc3(ReadOnlySpan<byte> data, int w, int h)
    {
        var output = new byte[w * h * 4];
        var bx = (w + 3) / 4;
        var by = (h + 3) / 4;
        var offset = 0;

        for (int y = 0; y < by; y++)
        {
            for (int x = 0; x < bx; x++)
            {
                if (offset + 16 > data.Length) return output;
                DecodeBc3Alpha(data[offset..], output, x * 4, y * 4, w, h);
                DecodeBc1Block(data[(offset + 8)..], output, x * 4, y * 4, w, h);
                offset += 16;
            }
        }
        return output;
    }

    private static byte[] DecodeBc5(ReadOnlySpan<byte> data, int w, int h)
    {
        // BC5 stores two channels (R,G) as two BC3-style alpha blocks
        // Used for normal maps — render as gray for preview
        var output = new byte[w * h * 4];
        var bx = (w + 3) / 4;
        var by = (h + 3) / 4;
        var offset = 0;

        var redChannel = new byte[w * h];
        var greenChannel = new byte[w * h];

        for (int y = 0; y < by; y++)
        {
            for (int x = 0; x < bx; x++)
            {
                if (offset + 16 > data.Length) break;
                DecodeBc3AlphaChannel(data[offset..], redChannel, x * 4, y * 4, w, h);
                DecodeBc3AlphaChannel(data[(offset + 8)..], greenChannel, x * 4, y * 4, w, h);
                offset += 16;
            }
        }

        for (int i = 0; i < w * h; i++)
        {
            output[i * 4 + 0] = redChannel[i];   // B
            output[i * 4 + 1] = greenChannel[i];  // G
            output[i * 4 + 2] = redChannel[i];    // R
            output[i * 4 + 3] = 255;              // A
        }
        return output;
    }

    private static void DecodeBc1Block(ReadOnlySpan<byte> src, byte[] dst, int blockX, int blockY, int w, int h)
    {
        var c0 = (ushort)(src[0] | (src[1] << 8));
        var c1 = (ushort)(src[2] | (src[3] << 8));

        Span<byte> colors = stackalloc byte[16];
        Unpack565(c0, out colors[2], out colors[1], out colors[0]); colors[3] = 255;
        Unpack565(c1, out colors[6], out colors[5], out colors[4]); colors[7] = 255;

        if (c0 > c1)
        {
            colors[8]  = (byte)((2 * colors[0] + colors[4]) / 3);
            colors[9]  = (byte)((2 * colors[1] + colors[5]) / 3);
            colors[10] = (byte)((2 * colors[2] + colors[6]) / 3);
            colors[11] = 255;
            colors[12] = (byte)((colors[0] + 2 * colors[4]) / 3);
            colors[13] = (byte)((colors[1] + 2 * colors[5]) / 3);
            colors[14] = (byte)((colors[2] + 2 * colors[6]) / 3);
            colors[15] = 255;
        }
        else
        {
            colors[8]  = (byte)((colors[0] + colors[4]) / 2);
            colors[9]  = (byte)((colors[1] + colors[5]) / 2);
            colors[10] = (byte)((colors[2] + colors[6]) / 2);
            colors[11] = 255;
            colors[12] = colors[13] = colors[14] = 0;
            colors[15] = 0;
        }

        var lookup = (uint)(src[4] | (src[5] << 8) | (src[6] << 16) | (src[7] << 24));
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                var px = blockX + x;
                var py = blockY + y;
                if (px >= w || py >= h) continue;

                var idx = (int)((lookup >> (2 * (4 * y + x))) & 3);
                var dstOff = (py * w + px) * 4;
                dst[dstOff + 0] = colors[idx * 4 + 0];
                dst[dstOff + 1] = colors[idx * 4 + 1];
                dst[dstOff + 2] = colors[idx * 4 + 2];
                dst[dstOff + 3] = colors[idx * 4 + 3];
            }
        }
    }

    private static void DecodeBc2Alpha(ReadOnlySpan<byte> src, byte[] dst, int blockX, int blockY, int w, int h)
    {
        for (int y = 0; y < 4; y++)
        {
            var alphaBits = (ushort)(src[y * 2] | (src[y * 2 + 1] << 8));
            for (int x = 0; x < 4; x++)
            {
                var px = blockX + x;
                var py = blockY + y;
                if (px >= w || py >= h) continue;
                var a4 = (alphaBits >> (x * 4)) & 0xF;
                dst[(py * w + px) * 4 + 3] = (byte)(a4 * 17);
            }
        }
    }

    private static void DecodeBc3Alpha(ReadOnlySpan<byte> src, byte[] dst, int blockX, int blockY, int w, int h)
    {
        Span<byte> alphas = stackalloc byte[8];
        InterpolateAlphas(src, alphas);

        ulong bits = 0;
        for (int i = 0; i < 6; i++)
            bits |= (ulong)src[2 + i] << (8 * i);

        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                var px = blockX + x;
                var py = blockY + y;
                if (px >= w || py >= h) continue;
                var idx = (int)((bits >> (3 * (4 * y + x))) & 7);
                dst[(py * w + px) * 4 + 3] = alphas[idx];
            }
        }
    }

    private static void DecodeBc3AlphaChannel(ReadOnlySpan<byte> src, byte[] channel, int blockX, int blockY, int w, int h)
    {
        Span<byte> alphas = stackalloc byte[8];
        InterpolateAlphas(src, alphas);

        ulong bits = 0;
        for (int i = 0; i < 6; i++)
            bits |= (ulong)src[2 + i] << (8 * i);

        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                var px = blockX + x;
                var py = blockY + y;
                if (px >= w || py >= h) continue;
                var idx = (int)((bits >> (3 * (4 * y + x))) & 7);
                channel[py * w + px] = alphas[idx];
            }
        }
    }

    private static void InterpolateAlphas(ReadOnlySpan<byte> src, Span<byte> alphas)
    {
        var a0 = src[0];
        var a1 = src[1];
        alphas[0] = a0;
        alphas[1] = a1;

        if (a0 > a1)
        {
            alphas[2] = (byte)((6 * a0 + 1 * a1) / 7);
            alphas[3] = (byte)((5 * a0 + 2 * a1) / 7);
            alphas[4] = (byte)((4 * a0 + 3 * a1) / 7);
            alphas[5] = (byte)((3 * a0 + 4 * a1) / 7);
            alphas[6] = (byte)((2 * a0 + 5 * a1) / 7);
            alphas[7] = (byte)((1 * a0 + 6 * a1) / 7);
        }
        else
        {
            alphas[2] = (byte)((4 * a0 + 1 * a1) / 5);
            alphas[3] = (byte)((3 * a0 + 2 * a1) / 5);
            alphas[4] = (byte)((2 * a0 + 3 * a1) / 5);
            alphas[5] = (byte)((1 * a0 + 4 * a1) / 5);
            alphas[6] = 0;
            alphas[7] = 255;
        }
    }

    private static void Unpack565(ushort c, out byte r, out byte g, out byte b)
    {
        r = (byte)(((c >> 11) & 0x1F) * 255 / 31);
        g = (byte)(((c >> 5) & 0x3F) * 255 / 63);
        b = (byte)((c & 0x1F) * 255 / 31);
    }
}
