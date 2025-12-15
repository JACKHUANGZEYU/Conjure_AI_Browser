using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ConjureBrowser.Core.Import;

/// <summary>
/// Helpers for decrypting/encrypting Chromium (Chrome/CEF) password blobs on Windows.
/// </summary>
public static class ChromiumCrypt
{
    private static readonly byte[] DpapiPrefix = Encoding.ASCII.GetBytes("DPAPI");

    /// <summary>
    /// Reads the AES master key from a Chromium "Local State" file.
    /// Returns null if not on Windows or if the key can't be read.
    /// </summary>
    public static byte[]? TryGetAesKeyFromLocalState(string localStatePath)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        if (!File.Exists(localStatePath))
            return null;

        try
        {
            var json = File.ReadAllText(localStatePath);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("os_crypt", out var osCrypt) ||
                !osCrypt.TryGetProperty("encrypted_key", out var encryptedKeyElement))
                return null;

            var encryptedKeyB64 = encryptedKeyElement.GetString();
            if (string.IsNullOrWhiteSpace(encryptedKeyB64))
                return null;

            var encryptedKeyWithPrefix = Convert.FromBase64String(encryptedKeyB64);

            // Remove "DPAPI" prefix if present.
            var keyBytes = encryptedKeyWithPrefix;
            if (encryptedKeyWithPrefix.Length > DpapiPrefix.Length &&
                encryptedKeyWithPrefix.AsSpan(0, DpapiPrefix.Length).SequenceEqual(DpapiPrefix))
            {
                keyBytes = encryptedKeyWithPrefix[DpapiPrefix.Length..];
            }

            return ProtectedData.Unprotect(keyBytes, null, DataProtectionScope.CurrentUser);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Decrypts a Chromium password_value blob into plaintext.
    /// Supports DPAPI blobs and v10/v11 AES-GCM blobs.
    /// </summary>
    public static string? DecryptPassword(byte[] encryptedValue, byte[]? aesKey)
    {
        if (!OperatingSystem.IsWindows() || encryptedValue.Length == 0)
            return null;

        try
        {
            // AES-GCM encrypted values start with "v10"/"v11"/"v20"
            if (encryptedValue.Length > 3 &&
                encryptedValue[0] == (byte)'v' &&
                (encryptedValue[1] == (byte)'1' || encryptedValue[1] == (byte)'2'))
            {
                if (aesKey == null)
                    return null;

                if (encryptedValue.Length < 3 + 12 + 16)
                    return null;

                var nonce = encryptedValue.AsSpan(3, 12).ToArray();
                var cipherWithTag = encryptedValue.AsSpan(15).ToArray();

                if (cipherWithTag.Length < 16)
                    return null;

                var cipherText = cipherWithTag.AsSpan(0, cipherWithTag.Length - 16).ToArray();
                var tag = cipherWithTag.AsSpan(cipherWithTag.Length - 16, 16).ToArray();

                var plainBytes = new byte[cipherText.Length];
                using var aesGcm = new AesGcm(aesKey, 16);
                aesGcm.Decrypt(nonce, cipherText, tag, plainBytes);

                return Encoding.UTF8.GetString(plainBytes);
            }

            // Older DPAPI-protected blobs.
            var decrypted = ProtectedData.Unprotect(encryptedValue, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Encrypts plaintext into a Chromium-compatible v10 AES-GCM password_value blob.
    /// </summary>
    public static byte[] EncryptPassword(string plaintext, byte[] aesKey)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var cipherBytes = new byte[plainBytes.Length];
        var tag = new byte[16];

        using var aesGcm = new AesGcm(aesKey, 16);
        aesGcm.Encrypt(nonce, plainBytes, cipherBytes, tag);

        var result = new byte[3 + nonce.Length + cipherBytes.Length + tag.Length];
        Encoding.ASCII.GetBytes("v10").CopyTo(result, 0);
        Buffer.BlockCopy(nonce, 0, result, 3, nonce.Length);
        Buffer.BlockCopy(cipherBytes, 0, result, 3 + nonce.Length, cipherBytes.Length);
        Buffer.BlockCopy(tag, 0, result, 3 + nonce.Length + cipherBytes.Length, tag.Length);

        return result;
    }
}
