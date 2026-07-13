using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RslCompanionUploader.Auth;

/// <summary>What we persist for "remember me": enough to silently re-authenticate on next launch.</summary>
public sealed class SavedCredentials
{
    public string? Email { get; set; }
    public string? Uid { get; set; }
    public string? DisplayName { get; set; }
    /// <summary>Firebase refresh token — lets us mint a fresh ID token without re-entering anything.</summary>
    public string? RefreshToken { get; set; }
    /// <summary>True when the user asked to stay signed in (a refresh token is stored).</summary>
    public bool RememberSession { get; set; }
}

/// <summary>
/// Persists remembered credentials to <c>%LocalAppData%\RslCompanionUploader\creds.dat</c>,
/// encrypted with Windows DPAPI (CurrentUser scope) so only this Windows user on this machine can
/// read it. We store the Firebase refresh token rather than the password — it is revocable and
/// avoids ever writing the password to disk.
/// </summary>
public static class CredentialStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RslCompanionUploader.v1");

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RslCompanionUploader", "creds.dat");

    public static SavedCredentials Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new SavedCredentials();
            var encrypted = File.ReadAllBytes(FilePath);
            var bytes = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<SavedCredentials>(bytes) ?? new SavedCredentials();
        }
        catch
        {
            return new SavedCredentials(); // corrupt/unreadable — treat as nothing saved
        }
    }

    public static void Save(SavedCredentials creds)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(creds);
            var encrypted = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(FilePath, encrypted);
        }
        catch
        {
            // best-effort; never block sign-in on a failed save
        }
    }

    /// <summary>Removes the refresh token but keeps the remembered email for convenience.</summary>
    public static void ClearSession()
    {
        var existing = Load();
        Save(new SavedCredentials { Email = existing.Email, RememberSession = false });
    }

    public static void ClearAll()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { }
    }
}
