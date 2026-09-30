using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace RslCompanionUploader.Auth;

/// <summary>
/// Completes the browser → desktop sign-in handshake: redeems the one-time code the site put on the
/// <c>rslcompanion-extractor://sync?code=…</c> launch URI, and signs in with what comes back.
///
/// <para>Two legs, one call:</para>
/// <list type="number">
///   <item><description><c>POST /api/extractor/handoff/exchange</c> with <c>{ code }</c> →
///   <c>{ customToken }</c>. Deliberately unauthenticated: the app has no session yet, which is what
///   the handshake is for. The server throttles it per-IP and the code itself is the only
///   authority.</description></item>
///   <item><description>Firebase <c>signInWithCustomToken</c>, which gives this app its own ID and
///   refresh tokens.</description></item>
/// </list>
///
/// <para>The code is single-use and lives about 60 seconds, so every failure is terminal for that
/// launch — there is nothing to retry with except a fresh launch from the site. The one exception is
/// the per-IP throttle, which is transient and is retried once here.</para>
///
/// <para>Contract: <c>docs/extractor-handoff.md</c> in the RaidTools repo.</para>
/// </summary>
public sealed class ExtractorHandoff
{
    private readonly HttpClient _http;
    private readonly AppConfig _config;
    private readonly FirebaseAuthClient _auth;

    /// <summary>How long to wait out a 429 before the single retry the contract asks for.</summary>
    private static readonly TimeSpan ThrottleBackoff = TimeSpan.FromSeconds(2);

    public ExtractorHandoff(HttpClient http, AppConfig config, FirebaseAuthClient auth)
    {
        _http = http;
        _config = config;
        _auth = auth;
    }

    /// <summary>
    /// Redeems a launch's code at the environment it names and returns the resulting signed-in
    /// session, which carries that environment for the rest of its life. Throws
    /// <see cref="HandoffException"/> with a message meant for the user — including when the launch
    /// names an API that is not allow-listed, in which case <b>nothing is sent anywhere</b>; every
    /// other failure comes out of <see cref="FirebaseAuthClient"/> already humanized.
    /// </summary>
    internal async Task<AuthSession> SignInAsync(HandoffLaunch launch, CancellationToken ct = default)
    {
        var target = launch.ResolveTarget(_config) ?? throw new HandoffException(launch.RefusalMessage);
        var customToken = await ExchangeAsync(launch.Code, target, retryOnThrottle: true, ct);
        return await _auth.SignInWithCustomTokenAsync(customToken, target, ct);
    }

    private async Task<string> ExchangeAsync(string code, ApiTarget target, bool retryOnThrottle, CancellationToken ct)
    {
        var url = $"{target.ApiBaseUrl}{_config.HandoffExchangeEndpoint}";
        using var resp = await _http.PostAsJsonAsync(url, new { code }, ct);

        if (resp.StatusCode == HttpStatusCode.TooManyRequests && retryOnThrottle)
        {
            await Task.Delay(ThrottleBackoff, ct);
            return await ExchangeAsync(code, target, retryOnThrottle: false, ct);
        }

        // 403: this server does not let this account use the Extractor (dev without access). The
        // server's own message says who to ask; relaunching would only be refused again.
        if (resp.StatusCode == HttpStatusCode.Forbidden)
            throw new HandoffException(
                Api.RslCompanionApiClient.ServerMessage(await resp.Content.ReadAsStringAsync(ct))
                ?? $"Your account doesn't have access to {target.ApiHost}. Ask an RSL Companion admin for access.");

        if (!resp.IsSuccessStatusCode)
            throw new HandoffException(Describe(resp.StatusCode));

        var body = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("customToken", out var t) || t.GetString() is not string token || token.Length == 0)
            throw new HandoffException("RSL Companion returned an unexpected sign-in response. Please try again.");

        return token;
    }

    /// <summary>
    /// The failures the contract names. Note 401 covers unknown, already-redeemed and expired alike —
    /// the server answers all three identically on purpose, so that a caller cannot probe for live
    /// codes, and there is nothing more specific to tell the user than "launch it again".
    /// </summary>
    private static string Describe(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized =>
            "This sign-in link has already been used or has expired. Click Sign In again to get a new one.",
        HttpStatusCode.TooManyRequests =>
            "Too many sign-in attempts from this network. Wait a moment, then try again.",
        HttpStatusCode.ServiceUnavailable =>
            "RSL Companion sign-in is temporarily unavailable. Please try again shortly.",
        _ => $"Sign-in failed ({(int)status}). Please try again.",
    };
}

/// <summary>A handoff-exchange failure whose message is already fit to show the user.</summary>
public sealed class HandoffException : Exception
{
    public HandoffException(string message) : base(message) { }
}
