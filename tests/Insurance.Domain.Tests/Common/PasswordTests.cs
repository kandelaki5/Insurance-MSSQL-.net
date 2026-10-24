// src/lib/password.ts had no tests of its own; these check the C# scrypt against RFC 7914 and the round trip. That hashes of
// the old program verify here (and C#'s keys equal Node's byte for byte) is in CommonParityTests.
using System.Text;
using Insurance.Domain.Common;

namespace Insurance.Domain.Tests.Common;

public class PasswordTests
{
    [Fact(DisplayName = "scrypt gives the test vectors of RFC 7914")]
    public void Rfc7914Vectors()
    {
        Assert.Equal(
            "77d6576238657b203b19ca42c18a0497f16b4844e3074ae8dfdffa3fede21442fcd0069ded0948f8326a753a0fc81f17e8d3e0fb2e0d3628cf35e20c38d18906",
            Convert.ToHexStringLower(Scrypt.DeriveKey([], [], 16, 1, 1, 64)));
        Assert.Equal(
            "fdbabe1c9d3472007856e7190d01e9fe7c6ad7cbc8237830e77376634b3731622eaf30d92e22a3886ff109279d9830dac727afb94a83ee6d8360cbdfa2cc0640",
            Convert.ToHexStringLower(Scrypt.DeriveKey(Encoding.ASCII.GetBytes("password"), Encoding.ASCII.GetBytes("NaCl"), 1024, 8, 16, 64)));
        Assert.Equal(
            "7023bdcb3afd7348461c06cd81fd38ebfda8fbba904f8e3ea9b543f6545da1f2d5432955613f0fcf62d49705242a9af9e61e85dc0d651e40dfcf017b45575887",
            Convert.ToHexStringLower(Scrypt.DeriveKey(Encoding.ASCII.GetBytes("pleaseletmein"), Encoding.ASCII.GetBytes("SodiumChloride"), 16384, 8, 1, 64)));
    }

    [Fact(DisplayName = "a new hash has the old format and verifies its own password only")]
    public void RoundTrip()
    {
        foreach (var password in new[] { "", "Tbilisi-2026!", "პაროლი-სადაზღვევო" })
        {
            var stored = Password.HashPassword(password);
            var parts = stored.Split('$');
            Assert.Equal("scrypt", parts[0]);
            Assert.Equal(32, parts[1].Length);
            Assert.Equal(128, parts[2].Length);
            Assert.True(Password.VerifyPassword(password, stored));
            Assert.False(Password.VerifyPassword(password + "x", stored));
        }
        Assert.NotEqual(Password.HashPassword("same"), Password.HashPassword("same"));
    }

    [Fact(DisplayName = "a stored value that is not an scrypt hash verifies nothing")]
    public void Malformed()
    {
        foreach (var stored in new[] { "", "scrypt", "scrypt$", "scrypt$aa", "scrypt$aa$", "scrypt$$aa", "bcrypt$aa$bb" }) Assert.False(Password.VerifyPassword("x", stored), stored);
    }

    /// <summary>
    /// Codex R01: the old reading of a damaged hash gave a key of what hex was left, down to none, and an empty key matches
    /// every password. A damaged stored value — short or garbage hex, an odd digit, a cut or extended key, a salt of the wrong
    /// length, an extra part — must verify neither its own password nor any other.
    /// </summary>
    [Fact(DisplayName = "a damaged scrypt hash verifies no password, not even the one it was made from (R01)")]
    public void Damaged()
    {
        const string password = "Tbilisi-2026!";
        var good = Password.HashPassword(password);
        var (salt, key) = (good.Split('$')[1], good.Split('$')[2]);
        Assert.True(Password.VerifyPassword(password, good));
        var damaged = new[]
        {
            "scrypt$aa$zz", "scrypt$aa$z", "scrypt$xx$zz", "scrypt$zz$ab", "scrypt$aa$a", "scrypt$aa$aa",
            $"scrypt${salt}$", $"scrypt${salt}$zz", $"scrypt${salt}${key[..^1]}", $"scrypt${salt}${key[..^2]}", $"scrypt${salt}${key}0",
            $"scrypt${salt}${key}00", $"scrypt${salt}${key[..^2]}zz", $"scrypt${salt}${key[..2]}", $"scrypt${salt}z${key}",
            $"scrypt${salt[..^2]}${key}", $"scrypt${salt}00${key}", $"scrypt${salt}${key}$extra", $"scrypt${salt}${key}$",
            $"scrypt$${salt}${key}", $" scrypt${salt}${key}", $"scrypt${salt}${key} ", $"SCRYPT${salt}${key}", $"scrypt${salt}${key[..^1]}g",
        };
        foreach (var stored in damaged)
        {
            Assert.False(Password.VerifyPassword(password, stored), stored);
            Assert.False(Password.VerifyPassword("anything", stored), stored);
            Assert.False(Password.VerifyPassword("", stored), stored);
        }
        // Upper-case hex is still hex: the same hash written in capitals verifies its own password and no other.
        var upper = $"scrypt${salt.ToUpperInvariant()}${key.ToUpperInvariant()}";
        Assert.True(Password.VerifyPassword(password, upper));
        Assert.False(Password.VerifyPassword(password + "x", upper));
    }
}
