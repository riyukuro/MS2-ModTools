// A small .dds texture decoder for the model preview: the top mip level of DXT1, DXT3, DXT5 and of plain
// 32-bit, 24-bit, 16-bit and 8-bit pixels. Anything else gives null and a short reason, never an exception.
using System;

namespace MS2.ModKit.Models
{
    /// <summary>A decoded picture: 0xAARRGGBB pixels, top row first.</summary>
    public sealed class Texture
    {
        public readonly int Width, Height;
        public readonly int[] Argb;
        /// <summary>True when some pixel is not fully opaque.</summary>
        public bool HasAlpha;

        public Texture(int width, int height)
        {
            Width = width; Height = height;
            Argb = new int[width * height];
        }
    }

    public static class Dds
    {
        /// <summary>The largest side decoded: a larger texture is not shown (the mesh is drawn plain).</summary>
        public const int MaxSide = 4096;

        static uint U32(byte[] b, int at) { return (uint)(b[at] | b[at + 1] << 8 | b[at + 2] << 16 | b[at + 3] << 24); }

        static uint FourCc(string s) { return (uint)(s[0] | s[1] << 8 | s[2] << 16 | s[3] << 24); }

        /// <summary>Decodes the top level of a .dds file, or returns null with the reason in <paramref name="why"/>.</summary>
        public static Texture Decode(byte[] b, out string why)
        {
            why = null;
            try { return DecodeCore(b, out why); }
            catch (IndexOutOfRangeException) { why = "the texture is cut short"; }
            catch (ArgumentException) { why = "the texture is cut short"; }
            catch (OverflowException) { why = "the texture is too large"; }
            catch (OutOfMemoryException) { why = "the texture is too large"; }
            return null;
        }

        static Texture DecodeCore(byte[] b, out string why)
        {
            why = null;
            if (b == null || b.Length < 128 || U32(b, 0) != 0x20534444 || U32(b, 4) != 124) { why = "not a DDS texture"; return null; }
            int h = (int)U32(b, 12), w = (int)U32(b, 16);
            if (w <= 0 || h <= 0 || w > MaxSide || h > MaxSide) { why = "a texture of " + w + " x " + h + " is not shown"; return null; }
            uint pfFlags = U32(b, 80), fourCc = U32(b, 84), bits = U32(b, 88);
            uint rm = U32(b, 92), gm = U32(b, 96), bm = U32(b, 100), am = U32(b, 104);
            int at = 128;
            Texture t = null;   // made only after the file was seen to hold the picture's data
            int block = 0;
            if ((pfFlags & 4) != 0)
            {
                if (fourCc == FourCc("DXT1")) block = 1;
                else if (fourCc == FourCc("DXT2") || fourCc == FourCc("DXT3")) block = 3;
                else if (fourCc == FourCc("DXT4") || fourCc == FourCc("DXT5")) block = 5;
                else if (fourCc == FourCc("DX10"))
                {
                    if (b.Length < 148) { why = "the texture is cut short"; return null; }
                    uint dxgi = U32(b, 128);
                    at = 148;
                    if (dxgi >= 70 && dxgi <= 72) block = 1;
                    else if (dxgi >= 73 && dxgi <= 75) block = 3;
                    else if (dxgi >= 76 && dxgi <= 78) block = 5;
                    else if (dxgi >= 27 && dxgi <= 29) { bits = 32; rm = 0xff; gm = 0xff00; bm = 0xff0000; am = 0xff000000; }
                    else if (dxgi == 87 || dxgi == 91) { bits = 32; rm = 0xff0000; gm = 0xff00; bm = 0xff; am = 0xff000000; }
                    else if (dxgi == 88 || dxgi == 93) { bits = 32; rm = 0xff0000; gm = 0xff00; bm = 0xff; am = 0; }
                    else { why = "texture format DXGI " + dxgi + " is not shown"; return null; }
                }
                else
                {
                    why = "texture format " + (fourCc < 0x20202020 ? fourCc.ToString() : "" + (char)(fourCc & 0xff) + (char)(fourCc >> 8 & 0xff) + (char)(fourCc >> 16 & 0xff) + (char)(fourCc >> 24 & 0xff)) + " is not shown";
                    return null;
                }
            }
            if (block != 0)
            {
                int bw = (w + 3) / 4, bh = (h + 3) / 4, size = block == 1 ? 8 : 16;
                if ((long)bw * bh * size > b.Length - at) { why = "the texture is cut short"; return null; }
                t = new Texture(w, h);
                Blocks(b, at, t, block);
            }
            else
            {
                if (bits != 8 && bits != 16 && bits != 24 && bits != 32) { why = "a texture of " + bits + " bits a pixel is not shown"; return null; }
                int bytes = (int)bits / 8;
                if ((long)w * h * bytes > b.Length - at) { why = "the texture is cut short"; return null; }
                bool lum = (pfFlags & 0x20000) != 0 || (rm != 0 && gm == 0 && bm == 0);
                bool alphaOnly = rm == 0 && gm == 0 && bm == 0 && am != 0;
                if (rm == 0 && gm == 0 && bm == 0 && am == 0) { why = "the texture names no colour layout"; return null; }
                bool hasA = am != 0 && ((pfFlags & 1) != 0 || (pfFlags & 2) != 0 || alphaOnly);
                t = new Texture(w, h);
                for (int i = 0; i < w * h; i++)
                {
                    uint v = 0;
                    for (int k = 0; k < bytes; k++) v |= (uint)b[at + i * bytes + k] << (8 * k);
                    int r = Channel(v, rm), g = Channel(v, gm), bl = Channel(v, bm), a = hasA ? Channel(v, am) : 255;
                    if (lum) { g = r; bl = r; }
                    if (alphaOnly) { r = g = bl = 255; }
                    t.Argb[i] = a << 24 | r << 16 | g << 8 | bl;
                }
            }
            for (int i = 0; i < t.Argb.Length; i++)
                if ((uint)t.Argb[i] >> 24 != 255) { t.HasAlpha = true; break; }
            return t;
        }

        static int Channel(uint v, uint mask)
        {
            if (mask == 0) return 0;
            int shift = 0;
            while ((mask >> shift & 1) == 0) shift++;
            uint max = mask >> shift;
            uint c = (v & mask) >> shift;
            return max == 255 ? (int)c : (int)(c * 255 / max);
        }

        static void Blocks(byte[] b, int at, Texture t, int kind)
        {
            int w = t.Width, h = t.Height;
            var colour = new int[4];
            var alpha = new int[16];
            var ramp = new int[8];
            for (int by = 0; by < h; by += 4)
                for (int bx = 0; bx < w; bx += 4)
                {
                    for (int i = 0; i < 16; i++) alpha[i] = 255;
                    if (kind == 3)
                    {
                        for (int i = 0; i < 16; i++) alpha[i] = (b[at + i / 2] >> (i % 2 * 4) & 15) * 17;
                        at += 8;
                    }
                    else if (kind == 5)
                    {
                        int a0 = b[at], a1 = b[at + 1];
                        ramp[0] = a0; ramp[1] = a1;
                        if (a0 > a1) for (int i = 1; i < 7; i++) ramp[i + 1] = ((7 - i) * a0 + i * a1) / 7;
                        else
                        {
                            for (int i = 1; i < 5; i++) ramp[i + 1] = ((5 - i) * a0 + i * a1) / 5;
                            ramp[6] = 0; ramp[7] = 255;
                        }
                        ulong bitsA = 0;
                        for (int i = 0; i < 6; i++) bitsA |= (ulong)b[at + 2 + i] << (8 * i);
                        for (int i = 0; i < 16; i++) alpha[i] = ramp[(int)(bitsA >> (3 * i) & 7)];
                        at += 8;
                    }
                    int c0 = b[at] | b[at + 1] << 8, c1 = b[at + 2] | b[at + 3] << 8;
                    int r0 = (c0 >> 11) * 255 / 31, g0 = (c0 >> 5 & 63) * 255 / 63, b0 = (c0 & 31) * 255 / 31;
                    int r1 = (c1 >> 11) * 255 / 31, g1 = (c1 >> 5 & 63) * 255 / 63, b1 = (c1 & 31) * 255 / 31;
                    colour[0] = 255 << 24 | r0 << 16 | g0 << 8 | b0;
                    colour[1] = 255 << 24 | r1 << 16 | g1 << 8 | b1;
                    if (kind != 1 || c0 > c1)
                    {
                        colour[2] = 255 << 24 | (2 * r0 + r1) / 3 << 16 | (2 * g0 + g1) / 3 << 8 | (2 * b0 + b1) / 3;
                        colour[3] = 255 << 24 | (r0 + 2 * r1) / 3 << 16 | (g0 + 2 * g1) / 3 << 8 | (b0 + 2 * b1) / 3;
                    }
                    else
                    {
                        colour[2] = 255 << 24 | (r0 + r1) / 2 << 16 | (g0 + g1) / 2 << 8 | (b0 + b1) / 2;
                        colour[3] = 0;
                    }
                    uint idx = U32(b, at + 4);
                    at += 8;
                    for (int i = 0; i < 16; i++)
                    {
                        int x = bx + i % 4, y = by + i / 4;
                        if (x >= w || y >= h) continue;
                        int c = colour[(int)(idx >> (2 * i) & 3)];
                        if (kind != 1) c = c & 0xffffff | alpha[i] << 24;
                        t.Argb[y * w + x] = c;
                    }
                }
        }
    }
}
