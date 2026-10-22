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
}
