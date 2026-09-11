using System.Security.Cryptography;
using System.Text;
using EncSymmetric = EncryptionClassLibrary.Encryption.Symmetric;
using EncData = EncryptionClassLibrary.Encryption.Data;

namespace Candela.Platform.Security;

/// <summary>
/// Candela's stored-password and config-flag cipher, on .NET 10.
///
/// Why this class exists
/// ---------------------
/// Candela encrypts TblSecurityUser.User_log_password and several tblRCMSConfiguration
/// values through Utility.SymmetricEncryption, which is a thin wrapper over
/// EncryptionClassLibrary (Rijndael). Utility.dll targets .NET Framework and drags in
/// Windows Forms, so it cannot be referenced here — but EncryptionClassLibrary itself is
/// a CLR 2.0 assembly that touches only mscorlib and System.Security, and loads on
/// .NET 10 without complaint.
///
/// Encryption needed nothing done to it: the library produces byte-identical ciphertext
/// on both runtimes, verified against .NET Framework for nine inputs (SymmetricEncryptionTests).
///
/// Decryption did. The library reads its CryptoStream with a single Read() call and
/// assumes the buffer came back full. On .NET Framework it did; on .NET Core and later a
/// stream may legally return fewer bytes, so the library silently returns only the first
/// 16-byte block. A 16-character password decrypts correctly and a 17-character one does
/// not — the worst possible failure mode, because it ships and then breaks one user.
///
/// So decryption here asks the library for the key and IV it derives — which is the part
/// that must match Candela exactly, and the part nobody should reimplement — and then
/// does the AES read itself, correctly. Nothing about the algorithm, key derivation, IV,
/// mode, padding or text encoding is reinvented.
///
/// The special cases below ("TRUE"/"FALSE" passed through, empty in / empty out, one
/// fixed message on any failure) are copied from Utility.vb:2027-2070. Callers depend on
/// them: AuthController reads the Candela edition flag as Decrypt(raw, "f") == "1", and a
/// value stored literally as "True" must come back as "True" rather than throwing.
/// </summary>
public static class SymmetricEncryption
{
    /// <summary>
    /// The single message Utility throws for any decryption failure, verbatim — it is
    /// user-visible and support staff recognise it.
    /// </summary>
    private const string FailureMessage =
        "License information is incorrect. Please contact LumenSoft Technologies (Pvt) Ltd. +92 42 111 290 290";

    static SymmetricEncryption()
    {
        // EncryptionClassLibrary's Data type has a static constructor that calls
        // Encoding.GetEncoding("Windows-1252"). That code page is not registered by
        // default outside .NET Framework, so without this the very first use throws a
        // TypeInitializationException. Registering here means no caller has to remember.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>Windows-1252 — what the library uses to turn text into bytes and back.</summary>
    private static Encoding Latin1 => Encoding.GetEncoding(1252);

    /// <summary>
    /// Encrypts to the uppercase hex Candela stores. Delegates to the library, which
    /// produces the same bytes here as it does on .NET Framework.
    /// </summary>
    public static string Encrypt(string? strData, string strKey)
    {
        // Utility.vb:2009 — empty in, empty out, before anything else happens.
        if (string.IsNullOrEmpty(strData)) return strData ?? "";

        var sym = new EncSymmetric(EncSymmetric.Provider.Rijndael);
        return sym.Encrypt(new EncData(strData), new EncData(strKey)).Hex.ToString();
    }

    /// <summary>
    /// Decrypts a hex value Candela stored. Throws with Candela's own message on anything
    /// malformed, which is what the .NET Framework wrapper did.
    /// </summary>
    public static string Decrypt(string? strEncryptedData, string strKey)
    {
        // Utility.vb:2031 — nothing to decrypt is not an error.
        if (string.IsNullOrEmpty(strEncryptedData)) return string.Empty;

        // Utility.vb:2035-2039 (issue 4369) — some flags were written as plain text
        // rather than encrypted, so these two are passed straight through.
        if (string.Equals(strEncryptedData, "FALSE", StringComparison.OrdinalIgnoreCase)) return "False";
        if (string.Equals(strEncryptedData, "TRUE", StringComparison.OrdinalIgnoreCase)) return "True";

        try
        {
            var (key, iv) = DeriveKeyAndIv(strKey);
            var cipher = Convert.FromHexString(strEncryptedData);

            // CBC with PKCS7 — the library's own settings, confirmed by decrypting its
            // output for inputs either side of the block boundary.
            using var aes = Aes.Create();
            aes.Key = key;
            var plain = aes.DecryptCbc(cipher, iv, PaddingMode.PKCS7);

            return Latin1.GetString(plain);
        }
        catch (Exception ex)
        {
            // Utility.vb:2052-2070 funnels every failure — bad hex, bad padding, wrong
            // key — into this one message. Kept, so a corrupt row reads the same to the
            // person at the till as it always has.
            throw new InvalidOperationException(FailureMessage, ex);
        }
    }

    /// <summary>
    /// The key and IV the library derives for a given key string: the key string's
    /// Windows-1252 bytes zero-padded to 16, and the library's fixed default IV.
    ///
    /// Read out of the library rather than recomputed. The padding rule and the default
    /// IV are the two things that would silently diverge if they were written out by
    /// hand here, and a divergence means every password fails at once.
    /// </summary>
    private static (byte[] Key, byte[] Iv) DeriveKeyAndIv(string strKey)
    {
        var sym = new EncSymmetric(EncSymmetric.Provider.Rijndael);
        sym.Key = new EncData(strKey);
        return (sym.Key.Bytes, sym.IntializationVector.Bytes);
    }
}
