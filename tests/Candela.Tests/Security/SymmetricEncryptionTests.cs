using Candela.Platform.Security;

namespace Candela.Tests.Security;

/// <summary>
/// Golden master for Candela's password cipher.
///
/// Every ciphertext below was produced by the real EncryptionClassLibrary running on
/// .NET Framework (CLR 4.0.30319, via Windows PowerShell) through exactly the call
/// sequence Utility.SymmetricEncryption uses, with key "f" — the only key the POS API
/// ever passes. They are the contract: a stored password in a live Candela database looks
/// like this, and if this file ever fails, nobody can log in.
///
/// These are not round-trip tests. A round trip would pass even if both directions were
/// wrong together, which is precisely the bug that has to be caught: the library's own
/// Decrypt silently truncates at one 16-byte block on .NET 10, so anything over 16
/// characters came back short while encryption stayed correct.
///
/// Note the lengths on purpose — 16, 17, 21 and 41 characters. The 16/17 pair is the
/// block boundary the truncation bug sat on.
/// </summary>
public sealed class SymmetricEncryptionTests
{
    private const string Key = "f";

    [Theory]
    // plaintext                                   ciphertext produced on .NET Framework
    [InlineData("a", "9D24C4E8DA20764E710D9DB9CCB4E331")]
    [InlineData("abc", "4E7D6DE845C3FAC791DEADA86C939943")]
    [InlineData("1", "2939F1166AD1C7B2C0A1A1870E5DA5B8")]
    [InlineData("0", "170F029E821BEA0E695A9DB70712E0FC")]
    [InlineData("P@ssw0rd!", "AE520F9D95E0407D0C2D02ADC7418D09")]
    [InlineData("1234567890123456", "42DA1BD93B768BDA526189880411A3430B6CD8A32A3FAA4AD3FC80D3DF19681B")]
    [InlineData("12345678901234567", "42DA1BD93B768BDA526189880411A3433BEBA363C03CA3697AD4F03A42C4BC8B")]
    [InlineData("cashier_supervisor_01", "62E22A3F941BE5BD69F08F99E6459BE6A8504C9D31CC79940DBCEBFCDAB761D5")]
    public void Decrypt_matches_dotnet_framework(string expectedPlain, string cipherFromNet48)
    {
        Assert.Equal(expectedPlain, SymmetricEncryption.Decrypt(cipherFromNet48, Key));
    }

    [Theory]
    [InlineData("a", "9D24C4E8DA20764E710D9DB9CCB4E331")]
    [InlineData("P@ssw0rd!", "AE520F9D95E0407D0C2D02ADC7418D09")]
    [InlineData("1234567890123456", "42DA1BD93B768BDA526189880411A3430B6CD8A32A3FAA4AD3FC80D3DF19681B")]
    [InlineData("12345678901234567", "42DA1BD93B768BDA526189880411A3433BEBA363C03CA3697AD4F03A42C4BC8B")]
    [InlineData("cashier_supervisor_01", "62E22A3F941BE5BD69F08F99E6459BE6A8504C9D31CC79940DBCEBFCDAB761D5")]
    public void Encrypt_matches_dotnet_framework(string plain, string expectedCipherFromNet48)
    {
        Assert.Equal(expectedCipherFromNet48, SymmetricEncryption.Encrypt(plain, Key));
    }

    /// <summary>
    /// The encryption has a fixed IV, so the same input always gives the same hex. Candela
    /// relies on that — it is how a stored password can be compared at all.
    /// </summary>
    [Fact]
    public void Encrypt_is_deterministic()
    {
        var first = SymmetricEncryption.Encrypt("cashier_supervisor_01", Key);
        var second = SymmetricEncryption.Encrypt("cashier_supervisor_01", Key);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Empty_input_is_not_an_error()
    {
        // Utility.vb:2031 and :2009 — both directions pass empty straight through rather
        // than throwing, and callers rely on it.
        Assert.Equal("", SymmetricEncryption.Decrypt("", Key));
        Assert.Equal("", SymmetricEncryption.Decrypt(null, Key));
        Assert.Equal("", SymmetricEncryption.Encrypt("", Key));
    }

    [Theory]
    [InlineData("TRUE", "True")]
    [InlineData("true", "True")]
    [InlineData("FALSE", "False")]
    [InlineData("false", "False")]
    public void Plain_text_boolean_flags_pass_through(string stored, string expected)
    {
        // Utility.vb:2035-2039 (issue 4369): some config flags were written unencrypted.
        // AuthController's edition check reads one of these, so the passthrough matters.
        Assert.Equal(expected, SymmetricEncryption.Decrypt(stored, Key));
    }

    [Fact]
    public void Malformed_value_throws_candelas_own_message()
    {
        // Utility.vb:2052-2070 funnels every failure into this one message.
        var ex = Assert.Throws<InvalidOperationException>(
            () => SymmetricEncryption.Decrypt("NOT-HEX-AT-ALL", Key));

        Assert.Contains("License information is incorrect", ex.Message);
    }

    /// <summary>
    /// The specific regression this class exists for. The library's own Decrypt returns
    /// only the first block on .NET 10, so a 17-character password came back as 16
    /// characters and login failed for that user alone — while a 16-character password
    /// worked, which is what made it so easy to miss.
    /// </summary>
    [Fact]
    public void Values_longer_than_one_block_are_not_truncated()
    {
        const string cipher = "62E22A3F941BE5BD69F08F99E6459BE6A8504C9D31CC79940DBCEBFCDAB761D5";

        var plain = SymmetricEncryption.Decrypt(cipher, Key);

        Assert.Equal(21, plain.Length);
        Assert.Equal("cashier_supervisor_01", plain);
    }
}
