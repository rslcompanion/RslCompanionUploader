namespace RslCompanionUploader.Auth;

/// <summary>
/// Owns the lifetime of the app's own Firebase session: restoring it at startup, persisting it when
/// the user asked us to, and forgetting it on sign-out.
///
/// <para>The app holds a session <b>separate from the browser's</b>, and deliberately so. The
/// handshake hands over a one-time code, not the website's credentials, and
/// <c>signInWithCustomToken</c> mints tokens that belong to this install — so revoking one client
/// does not have to take the other down with it, and a token sitting on this disk is never worth a
/// browser session.</para>
///
/// <para>Restoring is async and runs <b>after</b> the window is up rather than blocking startup: it
/// makes a network call, and with the Windows Hello option on it also prompts the user. Neither
/// belongs in front of a window that has not been drawn yet.</para>
/// </summary>
public sealed class SessionManager
{
    private readonly FirebaseAuthClient _auth;

    public SessionManager(FirebaseAuthClient auth) => _auth = auth;

    /// <summary>Whether there is anything on disk to restore — checked before we prompt for anything.</summary>
    public static bool HasSavedSession => CredentialStore.HasSavedSession;

    /// <summary>
    /// Re-authenticates from the saved refresh token, or returns null when there is nothing saved,
    /// the user dismissed the Hello prompt, or the token was rejected.
    ///
    /// <para><b>A failed refresh only clears the saved session when Firebase actually rejected it.</b>
    /// Anything else — no network yet, DNS not up, Google returning a 503 — leaves the file alone. It
    /// used to clear on any exception, which meant launching the app before the network was ready
    /// silently discarded a perfectly good session and sent the user back to the website. Offline
    /// simply means the app opens signed-out and the next launch restores normally.</para>
    /// </summary>
    public async Task<AuthSession?> TryRestoreAsync(CancellationToken ct = default)
    {
        var saved = await CredentialStore.LoadAsync();
        if (saved is null || string.IsNullOrEmpty(saved.RefreshToken))
            return null;

        var seed = new AuthSession
        {
            IdToken = string.Empty,
            RefreshToken = saved.RefreshToken!,
            ExpiresAtUtc = DateTime.MinValue,
            Uid = saved.Uid,
            Email = saved.Email,
            DisplayName = saved.DisplayName,
        };

        try
        {
            var refreshed = await _auth.RefreshAsync(seed, ct);
            // The refresh token rotates, so the saved copy has to keep up or the next launch fails.
            await PersistAsync(refreshed);
            return refreshed;
        }
        catch (FirebaseAuthException ex) when (IsDefinitiveRejection(ex.Code))
        {
            await ForgetAsync();
            return null;
        }
        catch
        {
            return null; // transient — keep the saved session for the next launch
        }
    }

    /// <summary>
    /// Firebase codes that mean this refresh token will never work again: revoked, expired, or the
    /// account is gone. Everything else, including any bare <c>HTTP nnn</c> we could not parse, is
    /// treated as transient — the cost of guessing wrong in that direction is one extra sign-in, and
    /// in the other direction it is throwing away a valid session because Wi-Fi was slow.
    /// </summary>
    private static bool IsDefinitiveRejection(string code) => code is
        "TOKEN_EXPIRED" or "USER_DISABLED" or "USER_NOT_FOUND" or
        "INVALID_REFRESH_TOKEN" or "MISSING_REFRESH_TOKEN" or "INVALID_GRANT_TYPE";

    /// <summary>
    /// Writes the session to disk <b>if and only if</b> the user has opted in, using the protection
    /// level they chose. Called on every refresh as well as at sign-in, so the stored token never
    /// goes stale.
    /// </summary>
    public async Task<bool> PersistAsync(AuthSession session)
    {
        var settings = UserSettings.Current;
        if (!settings.SessionProtection.Persists())
        {
            // Not opted in: make sure a file from a previous opt-in does not outlive the decision.
            await ForgetAsync();
            return true;
        }

        var saved = new SavedCredentials
        {
            Email = session.Email,
            Uid = session.Uid,
            DisplayName = session.DisplayName,
            RefreshToken = session.RefreshToken,
        };

        if (await CredentialStore.SaveAsync(saved, settings.SessionProtection))
            return true;

        // Hello was chosen and then refused or unavailable. Storing it unprotected instead would be
        // the opposite of the request, so we store nothing — and stop claiming a preference that is
        // not in effect, rather than re-prompting on every refresh for the rest of the session.
        settings.SessionProtection = SessionProtection.None;
        settings.Save();
        return false;
    }

    /// <summary>
    /// Records the level chosen on the sign-in window, then persists at it (or deliberately does
    /// not). This and the session-security dialog are the only writers of the preference, so the
    /// user's choice is the single source of truth for whether anything touches the disk.
    /// </summary>
    public async Task<bool> PersistAsync(AuthSession session, SessionProtection level)
    {
        var settings = UserSettings.Current;
        settings.SessionProtection = level;
        settings.SessionProtectionChosen = true; // an explicit answer, not the default standing in
        settings.Save();

        return await PersistAsync(session);
    }

    /// <summary>Deletes the stored session and any Hello key guarding it.</summary>
    public static Task ForgetAsync() => CredentialStore.ClearAsync();
}
