using System.Security.Cryptography;
using System.Text;

namespace TrennFix.App.Services;

/// <summary>Encrypts passwords with Windows DPAPI – only the same Windows user on the same PC can decrypt.</summary>
public static class SecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TrennFix.v1");

    public static string Protect(string plain) =>
        string.IsNullOrEmpty(plain) ? "" :
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser));

    public static string Unprotect(string protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue)) return "";
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedValue), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException)
        {
            return ""; // settings copied from another user / PC → password must be entered again
        }
    }
}
