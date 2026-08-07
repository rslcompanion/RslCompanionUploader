using System.Net.Http.Json;
using System.Text.Json;

namespace RslCompanionUploader.Auth;

/// <summary>
/// Talks to Google's Firebase Auth REST API using the same project/apiKey the RaidTools web app
/// uses, so the ID tokens it mints are accepted by <c>api.rslcompanion.com</c> unchanged.
///
/// Sign-in is browser-based: the website mints a one-time handoff code, this app redeems it at the
/// RSL Companion API for a Firebase <b>custom token</b> (see <see cref="ExtractorHandoff"/>), and
/// that is what gets exchanged here for a session of the app's own. So only three endpoints are used:
///   • custom token      → identitytoolkit  accounts:signInWithCustomToken
///   • refresh           → securetoken      /v1/token (grant_type=refresh_token)
///   • lookup (identity) → identitytoolkit  accounts:lookup
/// </summary>
public sealed class FirebaseAuthClient
{
    private readonly HttpClient _http;
    private readonly string _apiKey;

    public FirebaseAuthClient(HttpClient http, string apiKey)
    {
        _http = http;
        _apiKey = apiKey;
    }

    private const string IdentityBase = "https://identitytoolkit.googleapis.com/v1";
    private const string SecureTokenBase = "https://securetoken.googleapis.com/v1";

    /// <summary>
    /// Signs in with a Firebase custom token minted by the RSL Companion API for a redeemed handoff
    /// code, then looks up who it belongs to.
    ///
    /// <para>The tokens that come back are this app's <b>own</b> ID and refresh tokens — the
    /// browser's credentials are never shared, which is the point of the custom-token handshake.</para>
    /// </summary>
    public async Task<AuthSession> SignInWithCustomTokenAsync(string customToken, CancellationToken ct = default)
    {
        var url = $"{IdentityBase}/accounts:signInWithCustomToken?key={_apiKey}";
        using var resp = await _http.PostAsJsonAsync(url, new { token = customToken, returnSecureToken = true }, ct);
        var json = await ReadOrThrowAsync(resp, ct);

        var session = new AuthSession
        {
            IdToken = json.GetProperty("idToken").GetString()!,
            RefreshToken = json.GetProperty("refreshToken").GetString()!,
            ExpiresAtUtc = DateTime.UtcNow.AddSeconds(int.Parse(json.GetProperty("expiresIn").GetString()!)),
        };
        return await EnrichIdentityAsync(session, ct);
    }

    /// <summary>Uses a refresh token to obtain a fresh ID token. Keeps identity fields from the old session.</summary>
    public async Task<AuthSession> RefreshAsync(AuthSession current, CancellationToken ct = default)
    {
        var url = $"{SecureTokenBase}/token?key={_apiKey}";
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = current.RefreshToken,
        });
        using var resp = await _http.PostAsync(url, form, ct);
        var json = await ReadOrThrowAsync(resp, ct);

        var idToken = json.GetProperty("id_token").GetString()!;
        var refresh = json.GetProperty("refresh_token").GetString()!;
        var expiresIn = int.Parse(json.GetProperty("expires_in").GetString()!);

        return new AuthSession
        {
            IdToken = idToken,
            RefreshToken = refresh,
            ExpiresAtUtc = DateTime.UtcNow.AddSeconds(expiresIn),
            Uid = current.Uid,
            Email = current.Email,
            DisplayName = current.DisplayName,
        };
    }

    /// <summary>Fills in uid/email/displayName from accounts:lookup for a session that only has tokens.</summary>
    private async Task<AuthSession> EnrichIdentityAsync(AuthSession session, CancellationToken ct)
    {
        try
        {
            var url = $"{IdentityBase}/accounts:lookup?key={_apiKey}";
            using var resp = await _http.PostAsJsonAsync(url, new { idToken = session.IdToken }, ct);
            if (!resp.IsSuccessStatusCode) return session;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("users", out var users) || users.GetArrayLength() == 0)
                return session;

            var u = users[0];
            return new AuthSession
            {
                IdToken = session.IdToken,
                RefreshToken = session.RefreshToken,
                ExpiresAtUtc = session.ExpiresAtUtc,
                Uid = u.TryGetProperty("localId", out var l) ? l.GetString() : session.Uid,
                Email = u.TryGetProperty("email", out var e) ? e.GetString() : session.Email,
                DisplayName = u.TryGetProperty("displayName", out var d) ? d.GetString() : session.DisplayName,
            };
        }
        catch
        {
            return session; // identity is cosmetic — never fail sign-in over it
        }
    }

    private static async Task<JsonElement> ReadOrThrowAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (resp.IsSuccessStatusCode)
            return JsonDocument.Parse(body).RootElement.Clone();

        // Firebase error shape: { "error": { "message": "INVALID_LOGIN_CREDENTIALS", ... } }
        string message = $"HTTP {(int)resp.StatusCode}";
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err) &&
                err.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
            {
                message = m.GetString()!;
            }
        }
        catch { /* body wasn't JSON */ }

        throw new FirebaseAuthException(Humanize(message), message);
    }

    private static string Humanize(string code) => code switch
    {
        "EMAIL_NOT_FOUND" or "INVALID_PASSWORD" or "INVALID_LOGIN_CREDENTIALS"
            => "Incorrect email or password.",
        "USER_DISABLED" => "This account has been disabled.",
        // The custom-token leg: the API minted a token this Firebase project won't accept, which is a
        // server/config fault rather than anything the user got wrong — say so instead of "sign-in failed".
        "INVALID_CUSTOM_TOKEN" or "CREDENTIAL_MISMATCH" or "TOKEN_EXPIRED"
            => "Sign-in was rejected by Firebase. Launch the Extractor from rslcompanion.com again.",
        "TOO_MANY_ATTEMPTS_TRY_LATER" => "Too many attempts. Please try again later.",
        "MISSING_PASSWORD" => "Please enter your password.",
        "INVALID_EMAIL" => "That email address is not valid.",
        _ when code.StartsWith("HTTP ") => $"Sign-in failed ({code}).",
        _ => $"Sign-in failed: {code}",
    };
}

public sealed class FirebaseAuthException : Exception
{
    public string Code { get; }
    public FirebaseAuthException(string message, string code) : base(message) => Code = code;
}
