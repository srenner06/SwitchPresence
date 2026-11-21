using System.Security.Cryptography;
using System.Text;

namespace SwitchPresence.Storage;

/// <summary>
/// DPAPI (CurrentUser) at rest. The Nintendo session token is full access to the second
/// account, and the Coral token is live credentials, so neither is kept in plaintext.
/// </summary>
public static class SecretStore
{
    public static void Write(string path, string value) =>
        File.WriteAllBytes(path, ProtectedData.Protect(
            Encoding.UTF8.GetBytes(value), optionalEntropy: null, DataProtectionScope.CurrentUser));

    /// <summary>null when absent, or when it cannot be decrypted (different user or machine).</summary>
    public static string? Read(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(
                File.ReadAllBytes(path), optionalEntropy: null, DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException ex)
        {
            Logging.Log.Warn($"could not decrypt {Path.GetFileName(path)} ({ex.Message}); treating as absent");
            return null;
        }
    }

    public static void Delete(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }
}
