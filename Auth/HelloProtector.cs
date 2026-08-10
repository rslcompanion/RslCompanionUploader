using System.Runtime.Versioning;
using System.Security.Cryptography;
using Windows.Security.Credentials;
using Windows.Security.Cryptography;
using Windows.Storage.Streams;

namespace RslCompanionUploader.Auth;

/// <summary>
/// Optional Windows Hello lock on the saved session.
///
/// <para><b>What it is for.</b> DPAPI, which is what <see cref="CredentialStore"/> uses by default,
/// stops another Windows user and anyone holding the disk. It does <i>not</i> stop code running as
/// the signed-in user — malware in that position can simply call <c>Unprotect</c> itself. Hello
/// closes that gap: the key is held by the TPM and only released after the user proves themselves
/// with a face, a fingerprint or the machine PIN, so possession of the file is no longer enough.</para>
///
/// <para><b>How the key is derived.</b> Hello does not hand out key material, it signs. So the stored
/// blob carries a random 32-byte challenge; signing it with the Hello credential yields a value only
/// this TPM can produce, and SHA-256 of that signature is the AES key. This works because Hello signs
/// with RSASSA-PKCS1-v1_5, which is deterministic — the same challenge always yields the same
/// signature, and therefore the same key. Nothing derived from it is ever written down.</para>
///
/// <para><b>Failure is recoverable by design.</b> If the credential is gone (Hello reset, PIN removed,
/// TPM cleared) the key cannot be re-derived and the saved session is unreadable — so every caller
/// treats a failure here as "no saved session" and shows sign-in, rather than surfacing an error the
/// user cannot act on.</para>
/// </summary>
internal static class HelloProtector
{
    /// <summary>
    /// Names the key pair inside Hello's own store. Changing it strands every session saved under the
    /// old name, so it is fixed for the life of the app.
    /// </summary>
    private const string CredentialName = "RslCompanionUploader.Session";

    /// <summary>
    /// Whether this machine can do Hello at all — false when no PIN or biometric is enrolled. The UI
    /// checks this before offering the option, because a checkbox that silently does nothing is worse
    /// than no checkbox.
    /// </summary>
    public static async Task<bool> IsAvailableAsync()
    {
        try { return await KeyCredentialManager.IsSupportedAsync(); }
        catch { return false; }
    }

    /// <summary>
    /// Encrypts <paramref name="plaintext"/> under a fresh challenge, prompting the user once to
    /// create or confirm the Hello credential. Returns null if they decline or Hello fails — the
    /// caller then saves nothing rather than silently downgrading to unprotected storage.
    /// </summary>
    public static async Task<byte[]?> ProtectAsync(byte[] plaintext)
    {
        var challenge = RandomNumberGenerator.GetBytes(32);
        var key = await DeriveKeyAsync(challenge, create: true);
        if (key is null) return null;

        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];

        using (var aes = new AesGcm(key, tag.Length))
            aes.Encrypt(nonce, plaintext, ciphertext, tag);
        CryptographicOperations.ZeroMemory(key);

        // challenge ‖ nonce ‖ tag ‖ ciphertext — all fixed-size but the last, so no framing needed.
        var blob = new byte[challenge.Length + nonce.Length + tag.Length + ciphertext.Length];
        challenge.CopyTo(blob, 0);
        nonce.CopyTo(blob, challenge.Length);
        tag.CopyTo(blob, challenge.Length + nonce.Length);
        ciphertext.CopyTo(blob, challenge.Length + nonce.Length + tag.Length);
        return blob;
    }

    /// <summary>
    /// Re-derives the key by asking Hello to sign the stored challenge (this is the prompt the user
    /// sees at startup) and decrypts. Null on any failure, including the user dismissing the prompt.
    /// </summary>
    public static async Task<byte[]?> UnprotectAsync(byte[] blob)
    {
        const int challengeLength = 32;
        var nonceLength = AesGcm.NonceByteSizes.MaxSize;
        var tagLength = AesGcm.TagByteSizes.MaxSize;
        if (blob.Length < challengeLength + nonceLength + tagLength) return null;

        var challenge = blob[..challengeLength];
        var nonce = blob[challengeLength..(challengeLength + nonceLength)];
        var tag = blob[(challengeLength + nonceLength)..(challengeLength + nonceLength + tagLength)];
        var ciphertext = blob[(challengeLength + nonceLength + tagLength)..];

        var key = await DeriveKeyAsync(challenge, create: false);
        if (key is null) return null;

        try
        {
            var plaintext = new byte[ciphertext.Length];
            using var aes = new AesGcm(key, tagLength);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
            return plaintext;
        }
        catch (CryptographicException)
        {
            // Wrong key (a re-enrolled Hello credential signs differently) or a tampered blob. Both
            // mean the same thing to the caller: this saved session is gone.
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>Removes the Hello key pair, so any blob protected with it becomes permanently unreadable.</summary>
    public static async Task DeleteAsync()
    {
        try { await KeyCredentialManager.DeleteAsync(CredentialName); }
        catch { /* never existed, or already gone */ }
    }

    [SupportedOSPlatform("windows10.0.10240.0")]
    private static async Task<byte[]?> DeriveKeyAsync(byte[] challenge, bool create)
    {
        try
        {
            var open = await KeyCredentialManager.OpenAsync(CredentialName);
            var credential = open.Credential;

            if (credential is null)
            {
                if (!create) return null;
                var made = await KeyCredentialManager.RequestCreateAsync(
                    CredentialName, KeyCredentialCreationOption.ReplaceExisting);
                if (made.Status != KeyCredentialStatus.Success) return null;
                credential = made.Credential;
            }

            // This is the call that prompts for face/fingerprint/PIN.
            var signed = await credential.RequestSignAsync(CryptographicBuffer.CreateFromByteArray(challenge));
            if (signed.Status != KeyCredentialStatus.Success) return null;

            CryptographicBuffer.CopyToByteArray(signed.Result, out var signature);
            return SHA256.HashData(signature);
        }
        catch
        {
            return null; // Hello unavailable, cancelled, or the WinRT call itself failed
        }
    }
}
