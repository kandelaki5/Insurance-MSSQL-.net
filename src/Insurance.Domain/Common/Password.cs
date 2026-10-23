// Ported from src/lib/password.ts: passwords are kept as "scrypt$<salt hex>$<key hex>" — Node's crypto.scrypt with its
// defaults (N = 16384, r = 8, p = 1), a 16-byte random salt and a 64-byte key — so hashes made by the old program verify
// here and the other way round.
using System.Security.Cryptography;
using System.Text;

namespace Insurance.Domain.Common;

public static class Password
{
    private const int SaltLength = 16;
    private const int KeyLength = 64;
    private const int N = 16384;
    private const int R = 8;
    private const int P = 1;

    public static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        var key = Scrypt.DeriveKey(Encoding.UTF8.GetBytes(password), salt, N, R, P, KeyLength);
        return $"scrypt${Convert.ToHexStringLower(salt)}${Convert.ToHexStringLower(key)}";
    }

    /// <summary>
    /// Whether the password gives the stored key, compared in constant time. Only the exact form <see cref="HashPassword"/>
    /// writes — and the old program wrote, its only producer — is a hash: "scrypt", a 16-byte salt and a 64-byte key in hex,
    /// three parts and nothing else. Anything else verifies no password. The old code read the hex as Node's Buffer.from
    /// does, stopping at the first pair that is not hex and taking the key's length from what was left, so a damaged value
    /// such as "scrypt$aa$zz" gave an empty key and let any password in (Codex R01); that reading is not kept.
    /// </summary>
    public static bool VerifyPassword(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 3 || parts[0] != "scrypt") return false;
        if (Hex(parts[1], SaltLength) is not { } salt || Hex(parts[2], KeyLength) is not { } expected) return false;
        var actual = Scrypt.DeriveKey(Encoding.UTF8.GetBytes(password), salt, N, R, P, KeyLength);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>The bytes of a hex text of exactly <paramref name="length"/> bytes, or null when it is anything else.</summary>
    private static byte[]? Hex(string hex, int length)
    {
        if (hex.Length != length * 2) return null;
        var bytes = new byte[length];
        for (int i = 0; i < length; i++)
        {
            int hi = HexDigit(hex[2 * i]), lo = HexDigit(hex[2 * i + 1]);
            if (hi < 0 || lo < 0) return null;
            bytes[i] = (byte)(hi * 16 + lo);
        }
        return bytes;
    }

    private static int HexDigit(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };
}
