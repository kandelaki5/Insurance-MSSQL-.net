using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;

namespace Insurance.Domain.Common;

/// <summary>
/// scrypt (RFC 7914), as Node's crypto.scrypt computes it: PBKDF2-HMAC-SHA256 with one iteration around ROMix of
/// Salsa20/8 BlockMix. Node's defaults are N = 16384, r = 8, p = 1.
/// </summary>
public static class Scrypt
{
    public static byte[] DeriveKey(byte[] password, byte[] salt, int n, int r, int p, int keyLength)
    {
        if (n < 2 || (n & (n - 1)) != 0) throw new ArgumentOutOfRangeException(nameof(n), "N must be a power of two above 1");
        if (r < 1 || p < 1) throw new ArgumentOutOfRangeException(nameof(r));
        ArgumentOutOfRangeException.ThrowIfNegative(keyLength);
        int blockBytes = 128 * r;
        var b = Rfc2898DeriveBytes.Pbkdf2(password, salt, 1, HashAlgorithmName.SHA256, p * blockBytes);
        var x = new uint[32 * r];
        var v = new uint[32 * r * n];
        var y = new uint[32 * r];
        for (int i = 0; i < p; i++)
        {
            var span = b.AsSpan(i * blockBytes, blockBytes);
            for (int k = 0; k < x.Length; k++) x[k] = BinaryPrimitives.ReadUInt32LittleEndian(span[(k * 4)..]);
            RoMix(x, v, y, r, n);
            for (int k = 0; k < x.Length; k++) BinaryPrimitives.WriteUInt32LittleEndian(span[(k * 4)..], x[k]);
        }
        if (keyLength == 0) return [];
        return Rfc2898DeriveBytes.Pbkdf2(password, b, 1, HashAlgorithmName.SHA256, keyLength);
    }

    private static void RoMix(uint[] x, uint[] v, uint[] y, int r, int n)
    {
        int words = 32 * r;
        for (int i = 0; i < n; i++)
        {
            Array.Copy(x, 0, v, i * words, words);
            BlockMix(x, y, r);
        }
        for (int i = 0; i < n; i++)
        {
            int j = (int)(x[(2 * r - 1) * 16] & (uint)(n - 1));
            for (int k = 0; k < words; k++) x[k] ^= v[j * words + k];
            BlockMix(x, y, r);
        }
    }

    // B'[i] = Salsa(X ^ B[i]); even results first, then odd ones.
    private static void BlockMix(uint[] b, uint[] y, int r)
    {
        Span<uint> t = stackalloc uint[16];
        b.AsSpan((2 * r - 1) * 16, 16).CopyTo(t);
        for (int i = 0; i < 2 * r; i++)
        {
            for (int k = 0; k < 16; k++) t[k] ^= b[i * 16 + k];
            Salsa208(t);
            int dest = (i % 2 == 0 ? i / 2 : r + i / 2) * 16;
            t.CopyTo(y.AsSpan(dest, 16));
        }
        Array.Copy(y, b, 32 * r);
    }

    private static void Salsa208(Span<uint> b)
    {
        uint x0 = b[0], x1 = b[1], x2 = b[2], x3 = b[3], x4 = b[4], x5 = b[5], x6 = b[6], x7 = b[7];
        uint x8 = b[8], x9 = b[9], x10 = b[10], x11 = b[11], x12 = b[12], x13 = b[13], x14 = b[14], x15 = b[15];
        for (int i = 0; i < 8; i += 2)
        {
            x4 ^= BitOperations.RotateLeft(x0 + x12, 7); x8 ^= BitOperations.RotateLeft(x4 + x0, 9);
            x12 ^= BitOperations.RotateLeft(x8 + x4, 13); x0 ^= BitOperations.RotateLeft(x12 + x8, 18);
            x9 ^= BitOperations.RotateLeft(x5 + x1, 7); x13 ^= BitOperations.RotateLeft(x9 + x5, 9);
            x1 ^= BitOperations.RotateLeft(x13 + x9, 13); x5 ^= BitOperations.RotateLeft(x1 + x13, 18);
            x14 ^= BitOperations.RotateLeft(x10 + x6, 7); x2 ^= BitOperations.RotateLeft(x14 + x10, 9);
            x6 ^= BitOperations.RotateLeft(x2 + x14, 13); x10 ^= BitOperations.RotateLeft(x6 + x2, 18);
            x3 ^= BitOperations.RotateLeft(x15 + x11, 7); x7 ^= BitOperations.RotateLeft(x3 + x15, 9);
            x11 ^= BitOperations.RotateLeft(x7 + x3, 13); x15 ^= BitOperations.RotateLeft(x11 + x7, 18);
            x1 ^= BitOperations.RotateLeft(x0 + x3, 7); x2 ^= BitOperations.RotateLeft(x1 + x0, 9);
            x3 ^= BitOperations.RotateLeft(x2 + x1, 13); x0 ^= BitOperations.RotateLeft(x3 + x2, 18);
            x6 ^= BitOperations.RotateLeft(x5 + x4, 7); x7 ^= BitOperations.RotateLeft(x6 + x5, 9);
            x4 ^= BitOperations.RotateLeft(x7 + x6, 13); x5 ^= BitOperations.RotateLeft(x4 + x7, 18);
            x11 ^= BitOperations.RotateLeft(x10 + x9, 7); x8 ^= BitOperations.RotateLeft(x11 + x10, 9);
            x9 ^= BitOperations.RotateLeft(x8 + x11, 13); x10 ^= BitOperations.RotateLeft(x9 + x8, 18);
            x12 ^= BitOperations.RotateLeft(x15 + x14, 7); x13 ^= BitOperations.RotateLeft(x12 + x15, 9);
            x14 ^= BitOperations.RotateLeft(x13 + x12, 13); x15 ^= BitOperations.RotateLeft(x14 + x13, 18);
        }
        b[0] += x0; b[1] += x1; b[2] += x2; b[3] += x3; b[4] += x4; b[5] += x5; b[6] += x6; b[7] += x7;
        b[8] += x8; b[9] += x9; b[10] += x10; b[11] += x11; b[12] += x12; b[13] += x13; b[14] += x14; b[15] += x15;
    }
}
