// Entry encoding of .m2d archives: zlib, AES-256-CTR (built on ECB), base64; or XOR.
// Ported from Maple2.File.IO (GPL-3.0-or-later, CryptoManager / AESCipher / CipherKeys) and tools/m2d/Repack.cs.
using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace MS2.ModKit.M2d
{
    /// <summary>Pack version: the u32 tag at the start of the .m2h ("MS2F", "NS2F", "OS2F", "PS2F" in file order).</summary>
    public enum PackVersion : uint
    {
        MS2F = 0x4632534D,
        NS2F = 0x4632534E,
        OS2F = 0x4632534F,
        PS2F = 0x46325350,
    }

    /// <summary>Entry flag bits as stored in the file table.</summary>
    public static class M2dFlag
    {
        public const uint Aes = 0xEE000000u;
        public const uint Zlib = 9u;
        public const uint Xor = 0xFF000000u;
        public const uint AesZlib = Aes | Zlib;

        // Upstream tests with Enum.HasFlag, Aes first, so an Xor flag (0xFF contains 0xEE) is taken as Aes there.
        // Here the top byte is compared whole. No shipped archive has an Xor entry, so the Xor path is untested on real data.
        public static bool IsAes(uint f) { return (f & 0xFF000000u) == Aes; }
        public static bool IsXor(uint f) { return (f & 0xFF000000u) == Xor; }
        public static bool IsZlib(uint f) { return (f & Zlib) == Zlib; }
    }

    public static class M2dCodec
    {
        static readonly object keyLock = new object();
        static readonly byte[][] keys = new byte[4][], ivs = new byte[4][], xors = new byte[4][];

        static int Slot(PackVersion v)
        {
            switch (v)
            {
                case PackVersion.MS2F: return 0;
                case PackVersion.NS2F: return 1;
                case PackVersion.OS2F: return 2;
                case PackVersion.PS2F: return 3;
            }
            throw new ModKitException("unknown pack version 0x" + ((uint)v).ToString("X8"));
        }

        static void Tables(PackVersion v, out byte[] key, out byte[] iv, out byte[] xor)
        {
            int s = Slot(v);
            lock (keyLock)
            {
                if (keys[s] == null)
                {
                    string k, i, x;
                    switch (s)
                    {
                        case 0: k = M2dKeys.MS2F_Key; i = M2dKeys.MS2F_Iv; x = M2dKeys.MS2F_Xor; break;
                        case 1: k = M2dKeys.NS2F_Key; i = M2dKeys.NS2F_Iv; x = M2dKeys.NS2F_Xor; break;
                        case 2: k = M2dKeys.OS2F_Key; i = M2dKeys.OS2F_Iv; x = M2dKeys.OS2F_Xor; break;
                        default: k = M2dKeys.PS2F_Key; i = M2dKeys.PS2F_Iv; x = M2dKeys.PS2F_Xor; break;
                    }
                    ivs[s] = Convert.FromBase64String(i);
                    xors[s] = Convert.FromBase64String(x);
                    keys[s] = Convert.FromBase64String(k);
                }
                key = keys[s]; iv = ivs[s]; xor = xors[s];
            }
        }

        /// <summary>AES-CTR in place. The key and IV are chosen by the compressed size, as the game does.</summary>
        static void AesCtr(PackVersion v, uint compressedSize, byte[] buf)
        {
            Tables(v, out byte[] keyTab, out byte[] ivTab, out byte[] _);
            int n = (int)(compressedSize & 0x7F);
            var key = new byte[32];
            var ctr = new byte[16];
            Buffer.BlockCopy(keyTab, n * 32, key, 0, 32);
            Buffer.BlockCopy(ivTab, n * 16, ctr, 0, 16);
            using (var aes = Aes.Create())
            {
                aes.Mode = CipherMode.ECB;
                aes.Padding = PaddingMode.None;
                aes.KeySize = 256;
                using (var enc = aes.CreateEncryptor(key, new byte[16]))
                {
                    var block = new byte[16];
                    for (int i = 0; i < buf.Length; i += 16)
                    {
                        enc.TransformBlock(ctr, 0, 16, block, 0);
                        for (int c = 15; c >= 0 && ++ctr[c] == 0; c--) { }
                        int m = Math.Min(16, buf.Length - i);
                        for (int j = 0; j < m; j++) buf[i + j] ^= block[j];
                    }
                }
            }
        }

        static void XorBuf(PackVersion v, byte[] buf)
        {
            Tables(v, out byte[] _, out byte[] _, out byte[] key);
            int words = buf.Length >> 2, k = 0;
            for (int w = 0; w < words; w++)
            {
                for (int j = 0; j < 4; j++) buf[4 * w + j] ^= key[4 * k + j];
                k = (k + 1) & 0x1FF;
            }
            for (int i = 0, p = 4 * words; p < buf.Length; p++, i++) buf[p] ^= key[i & 0x7FF];
        }

        public static uint Adler32(byte[] data)
        {
            uint a = 1, b = 0;
            int i = 0;
            while (i < data.Length)
            {
                int end = Math.Min(data.Length, i + 3800);   // a and b cannot overflow within 3800 bytes
                for (; i < end; i++) { a += data[i]; b += a; }
                a %= 65521; b %= 65521;
            }
            return (b << 16) | a;
        }

        public static byte[] ZlibCompress(byte[] src)
        {
            using (var ms = new MemoryStream())
            {
                ms.WriteByte(0x78); ms.WriteByte(0x9C);
                using (var ds = new DeflateStream(ms, CompressionMode.Compress, true)) ds.Write(src, 0, src.Length);
                uint ad = Adler32(src);
                ms.WriteByte((byte)(ad >> 24)); ms.WriteByte((byte)(ad >> 16)); ms.WriteByte((byte)(ad >> 8)); ms.WriteByte((byte)ad);
                return ms.ToArray();
            }
        }

        public static byte[] ZlibDecompress(byte[] src, long sizeHint)
        {
            if (src.Length < 6) throw new ModKitException("zlib stream too short (" + src.Length + " bytes)");
            using (var ins = new MemoryStream(src, 2, src.Length - 6))
            using (var ds = new DeflateStream(ins, CompressionMode.Decompress))
            using (var outs = new MemoryStream(sizeHint > 0 && sizeHint < int.MaxValue ? (int)sizeHint : 0))
            {
                var tmp = new byte[81920];
                int n;
                while ((n = ds.Read(tmp, 0, tmp.Length)) > 0) outs.Write(tmp, 0, n);
                return outs.ToArray();
            }
        }

        /// <summary>Stored bytes of one entry (or of the name list / file table) back to the plain bytes.</summary>
        public static byte[] Decode(PackVersion v, uint flag, byte[] encoded, ulong compressedSize, ulong size)
        {
            byte[] buf;
            if (M2dFlag.IsAes(flag))
            {
                try { buf = Convert.FromBase64String(Encoding.ASCII.GetString(encoded)); }
                catch (FormatException e) { throw new ModKitException("entry is not base64 (wrong flag or damaged archive)", e); }
                AesCtr(v, (uint)compressedSize, buf);
            }
            else
            {
                buf = (byte[])encoded.Clone();
                if (M2dFlag.IsXor(flag)) XorBuf(v, buf);
            }
            return M2dFlag.IsZlib(flag) ? ZlibDecompress(buf, (long)size) : buf;
        }

        /// <summary>Plain bytes to the stored form. Mirrors Repack.cs EncodeEntry (the whole compressed buffer is encrypted).</summary>
        public static byte[] Encode(PackVersion v, uint flag, byte[] plain, out uint compressedSize)
        {
            byte[] buf = M2dFlag.IsZlib(flag) ? ZlibCompress(plain) : (byte[])plain.Clone();
            compressedSize = (uint)buf.Length;
            if (M2dFlag.IsAes(flag))
            {
                AesCtr(v, compressedSize, buf);
                return Encoding.ASCII.GetBytes(Convert.ToBase64String(buf));
            }
            if (M2dFlag.IsXor(flag)) XorBuf(v, buf);
            return buf;
        }
    }
}
