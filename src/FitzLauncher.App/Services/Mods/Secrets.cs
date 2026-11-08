using System.Security.Cryptography;
using System.Text;

namespace FitzLauncher.App.Services.Mods;

/// <summary>API keys stored encrypted with Windows DPAPI (only this Windows user can read them).</summary>
public static class Secrets
{
    public static string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return null;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored), null, DataProtectionScope.CurrentUser)); }
        catch (Exception e) when (e is CryptographicException or FormatException) { return null; }
    }

    public static string? Protect(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null
            : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value.Trim()), null, DataProtectionScope.CurrentUser));
}
