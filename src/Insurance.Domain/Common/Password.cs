// Ported from src/lib/password.ts: passwords are kept as "scrypt$<salt hex>$<key hex>" — Node's crypto.scrypt with its
// defaults (N = 16384, r = 8, p = 1), a 16-byte random salt and a 64-byte key — so hashes made by the old program verify
// here and the other way round.
using System.Security.Cryptography;
using System.Text;

namespace Insurance.Domain.Common;

public static class Password
{
    private const int KeyLength = 64;
    private const int N = 16384;
    private const int R = 8;
    private const int P = 1;

    public static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var key = Scrypt.DeriveKey(Encoding.UTF8.GetBytes(password), salt, N, R, P, KeyLength);
        return $"scrypt${Convert.ToHexStringLower(salt)}${Convert.ToHexStringLower(key)}";
    }

    /// <summary>
    /// Whether the password gives the stored key, compared in constant time. As in the old code the key's length is read
    /// from the stored hex, and the hex is read as Node's Buffer.from(hex, 'hex') reads it: up to the first pair that is not
    /// hex, an odd last digit dropped.
    /// </summary>
    public static bool VerifyPassword(string password, string stored)
    {
        var parts = stored.Split('$');
        var scheme = parts[0];
        var saltHex = parts.Length > 1 ? parts[1] : "";
        var keyHex = parts.Length > 2 ? parts[2] : "";
        if (scheme != "scrypt" || saltHex == "" || keyHex == "") return false;
        var expected = NodeHex(keyHex);
        var actual = Scrypt.DeriveKey(Encoding.UTF8.GetBytes(password), NodeHex(saltHex), N, R, P, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static byte[] NodeHex(string hex)
    {
        var bytes = new List<byte>(hex.Length / 2);
        for (int i = 0; i + 1 < hex.Length; i += 2)
        {
            int hi = HexDigit(hex[i]), lo = HexDigit(hex[i + 1]);
            if (hi < 0 || lo < 0) break;
            bytes.Add((byte)(hi * 16 + lo));
        }
        return bytes.ToArray();
    }

    private static int HexDigit(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };
}
