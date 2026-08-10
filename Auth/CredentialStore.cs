using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RslCompanionUploader.Auth;

/// <summary>What we persist for "keep me signed in": enough to silently re-authenticate on next launch.</summary>
public sealed class SavedCredentials
{
    public string? Email { get; set; }
    public string? Uid { get; set; }
    public string? DisplayName { get; set; }
    /// <summary>Firebase refresh token — lets us mint a fresh ID token without re-entering anything.</summary>
    public string? RefreshToken { get; set; }
}

/// <summary>
/// Persists a remembered session to <c>%LocalAppData%\RslCompanionUploader\creds.dat</c>.
///
/// <para><b>Nothing is written unless the user ticked "Keep me signed in".</b> Not the refresh token,
/// not the email, not the display name — the file simply does not exist. Convenience fields used to
/// be saved either way; they are identity, the checkbox is the only mandate we have for keeping any
/// of it, and a file that exists only when consent was given is also a file whose mere presence
/// answers "did I agree to this?".</para>
///
/// <para><b>Two protection levels, chosen at sign-in:</b></para>
/// <list type="bullet">
///   <item><description><b>DPAPI</b> (default) — CurrentUser scope, so another Windows user or
///   someone holding the disk gets nothing. Code running as the signed-in user can still read it;
///   that is the honest limit of DPAPI and no amount of extra entropy changes it.</description></item>
///   <item><description><b>DPAPI over Windows Hello</b> (opt-in) — additionally encrypted under a key
///   the TPM only releases after a Hello prompt, which closes exactly the gap above. See
///   <see cref="HelloProtector"/>.</description></item>
/// </list>
///
/// <para>What is stored is the <b>refresh token</b>, never a password and never the ID token: it is
/// revocable server-side (Help ▸ sign out everywhere), and it is the only thing a silent restart
/// actually needs.</para>
/// </summary>
public static class CredentialStore
{
    /// <summary>
    /// DPAPI secondary entropy. Note this is a public constant in a public repo and therefore adds no
    /// secrecy — it is retained only so that files written by earlier versions stay readable. The
    /// protection that matters comes from DPAPI's CurrentUser scope and, optionally, from Hello.
    /// </summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RslCompanionUploader.v1");

    /// <summary>Marks a file written by this version, so a legacy blob can be told apart and still read.</summary>
    private static readonly byte[] Magic = "RSLC"u8.ToArray();

    private const byte FormatVersion = 2;
    private const byte ModeDpapiOnly = 0;
    private const byte ModeHello = 1;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RslCompanionUploader", "creds.dat");

    /// <summary>Whether a saved session exists at all — cheap enough to check before prompting for Hello.</summary>
    public static bool HasSavedSession => File.Exists(FilePath);

    /// <summary>
    /// Loads the remembered session, or null when there is none, when the user dismissed the Hello
    /// prompt, or when the file cannot be decrypted. All three are the same thing to the caller: show
    /// sign-in. Async because the Hello path prompts the user.
    /// </summary>
    public static async Task<SavedCredentials?> LoadAsync()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var raw = await File.ReadAllBytesAsync(FilePath);

            var json = HasMagic(raw) ? await ReadCurrentFormatAsync(raw) : ReadLegacyFormat(raw);
            if (json is null) return null;

            var creds = JsonSerializer.Deserialize<SavedCredentials>(json);
            // A file left by an older version that saved identity without a token ("remember me"
            // unticked) carries nothing we can sign in with — same as no file.
            return string.IsNullOrEmpty(creds?.RefreshToken) ? null : creds;
        }
        catch
        {
            return null; // corrupt/unreadable — treat as nothing saved
        }
    }

    /// <summary>
    /// Saves a session at the requested protection level. Returns false when Hello was chosen but the
    /// user declined or it failed — in that case <b>nothing is written</b>, because silently falling
    /// back to weaker storage would hand the user the opposite of what they asked for.
    /// </summary>
    public static async Task<bool> SaveAsync(SavedCredentials creds, SessionProtection level)
    {
        if (!level.Persists())
        {
            await ClearAsync();
            return true;
        }

        try
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(creds);

            byte[] payload;
            byte mode;
            if (level == SessionProtection.WindowsHello)
            {
                var sealedBytes = await HelloProtector.ProtectAsync(json);
                if (sealedBytes is null) return false;
                payload = sealedBytes;
                mode = ModeHello;
            }
            else
            {
                payload = json;
                mode = ModeDpapiOnly;
            }

            // DPAPI wraps whichever payload we ended up with, so the Hello mode is strictly additive:
            // it never trades away the protection the default already gives.
            var protectedBytes = ProtectedData.Protect(payload, Entropy, DataProtectionScope.CurrentUser);

            var file = new byte[Magic.Length + 2 + protectedBytes.Length];
            Magic.CopyTo(file, 0);
            file[Magic.Length] = FormatVersion;
            file[Magic.Length + 1] = mode;
            protectedBytes.CopyTo(file, Magic.Length + 2);

            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            await File.WriteAllBytesAsync(FilePath, file);
            return true;
        }
        catch
        {
            return false; // best-effort; never block sign-in on a failed save
        }
    }

    /// <summary>
    /// Forgets the saved session entirely — the file is deleted, and any Hello key that was guarding
    /// it is deleted with it. There is nothing to keep "for convenience": the identity fields were
    /// only ever stored under the same consent as the token.
    /// </summary>
    public static async Task ClearAsync()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { }
        await HelloProtector.DeleteAsync();
    }

    private static bool HasMagic(byte[] raw) =>
        raw.Length > Magic.Length + 2 && raw.AsSpan(0, Magic.Length).SequenceEqual(Magic);

    private static async Task<byte[]?> ReadCurrentFormatAsync(byte[] raw)
    {
        var mode = raw[Magic.Length + 1];
        var body = raw[(Magic.Length + 2)..];

        var unwrapped = ProtectedData.Unprotect(body, Entropy, DataProtectionScope.CurrentUser);
        return mode == ModeHello ? await HelloProtector.UnprotectAsync(unwrapped) : unwrapped;
    }

    /// <summary>
    /// Reads a file written before the format header existed: a bare DPAPI blob of the JSON. Kept so
    /// upgrading does not silently sign everyone out; the next save rewrites it in the current format.
    /// </summary>
    private static byte[]? ReadLegacyFormat(byte[] raw)
    {
        try { return ProtectedData.Unprotect(raw, Entropy, DataProtectionScope.CurrentUser); }
        catch { return null; }
    }
}
