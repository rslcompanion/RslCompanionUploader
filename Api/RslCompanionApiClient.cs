using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using RslCompanionUploader.Auth;

namespace RslCompanionUploader.Api;

/// <summary>
/// Thin client over the RaidTools API. Automatically refreshes the Firebase ID token before each
/// call when it is close to expiry, and attaches it as a Bearer header.
/// </summary>
public sealed class RslCompanionApiClient
{
    private readonly HttpClient _http;
    private readonly AppConfig _config;
    private readonly FirebaseAuthClient _auth;

    /// <summary>
    /// The live session, or <c>null</c> when signed out. Replaced in place whenever the token is
    /// refreshed. The app now opens its main window before authenticating (the user signs in from the
    /// top bar), so the client must exist without a session.
    /// </summary>
    public AuthSession? Session { get; private set; }

    /// <summary>Whether a session is present. Callers must not hit the API endpoints when false.</summary>
    public bool IsAuthenticated => Session is not null;

    public RslCompanionApiClient(HttpClient http, AppConfig config, FirebaseAuthClient auth, AuthSession? session)
    {
        _http = http;
        _config = config;
        _auth = auth;
        Session = session;
    }

    /// <summary>Adopts a freshly obtained session (from the browser sign-in handoff).</summary>
    public void SignIn(AuthSession session) => Session = session;

    /// <summary>Drops the session; subsequent API calls throw until <see cref="SignIn"/> is called.</summary>
    public void SignOut() => Session = null;

    private async Task<string> ValidTokenAsync(CancellationToken ct)
    {
        var session = Session ?? throw new InvalidOperationException("Not signed in.");
        if (session.IsExpiringSoon)
            Session = session = await _auth.RefreshAsync(session, ct);
        return session.IdToken;
    }

    private async Task<HttpRequestMessage> BuildRequestAsync(HttpMethod method, string pathOrUrl, CancellationToken ct)
    {
        var url = pathOrUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? pathOrUrl
            : $"{_config.ApiBaseUrl}{pathOrUrl}";
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await ValidTokenAsync(ct));
        return req;
    }

    /// <summary>Fetches the accounts linked to the signed-in user (dropdown source).</summary>
    public async Task<List<AccountSummary>> GetAccountsAsync(CancellationToken ct = default)
    {
        using var req = await BuildRequestAsync(HttpMethod.Get, "/api/accounts", ct);
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var accounts = await resp.Content.ReadFromJsonAsync<List<AccountSummary>>(cancellationToken: ct);
        return accounts ?? new List<AccountSummary>();
    }

    /// <summary>
    /// POSTs a fully-formed <c>ConsolidatedProfile</c> JSON (produced by the extraction engine) to
    /// the parser sync endpoint. The profile carries its own in-game <c>accountId</c>, so the server
    /// routes it without a selected account. The Firebase ID token is still attached as a Bearer.
    /// </summary>
    public async Task<UploadResult> UploadConsolidatedAsync(string consolidatedJson, CancellationToken ct = default)
    {
        var endpoint = _config.SyncConsolidatedEndpoint;
        using var req = await BuildRequestAsync(HttpMethod.Post, endpoint, ct);
        req.Content = new StringContent(consolidatedJson, Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        // A 404 is called out separately because it means something different from a failure: the
        // endpoint isn't deployed, which is a server-side state the user can do nothing about and
        // must not read as "your export is broken".
        if (resp.StatusCode == HttpStatusCode.NotFound)
            return UploadResult.Fail(
                "RSL Companion isn't accepting uploads at the moment — nothing is wrong on your side. "
              + "Please try again later. If it's still happening after a while, use Help → Check for "
              + "updates: a newer version of this app may be sending to a route this one doesn't know.",
                $"404 from {endpoint}: {Trim(body)}");

        if (!resp.IsSuccessStatusCode)
            return UploadResult.Fail(
                "RSL Companion couldn't accept your data. Please try again in a few minutes. If it "
              + "keeps being rejected, use Help → Check for updates — a rejection that doesn't clear "
              + "on its own is usually this app sending something the server has moved on from.",
                $"{(int)resp.StatusCode} {resp.ReasonPhrase}: {Trim(body)}");

        return UploadResult.Ok(
            "Done — your account is up to date on RSL Companion.",
            $"{(int)resp.StatusCode} {resp.ReasonPhrase}: {Trim(body)}");
    }

    /// <summary>
    /// Asks the server whether it has a memory map for <paramref name="gameAssemblyHash"/> — the game
    /// build the user is running, which this release predates. A 404 is the normal "not published
    /// yet" answer, not a failure, so it is a distinct outcome rather than an exception.
    ///
    /// Returns the response body verbatim; parsing it is the caller's job, because the offsets blob
    /// inside is the extraction engine's catalog entry and only the engine defines its shape.
    /// Contract: <c>docs/build-certification-schema.md</c> / <c>.json</c>.
    /// </summary>
    public async Task<CertificationResult> GetCertifiedBuildAsync(
        string gameAssemblyHash, string? gameVersion, string uploaderVersion, CancellationToken ct = default)
    {
        var path = $"{_config.BuildCertificationEndpoint}/{Uri.EscapeDataString(gameAssemblyHash)}" +
                   $"?uploaderVersion={Uri.EscapeDataString(uploaderVersion)}" +
                   (string.IsNullOrWhiteSpace(gameVersion) ? "" : $"&gameVersion={Uri.EscapeDataString(gameVersion)}");

        using var req = await BuildRequestAsync(HttpMethod.Get, path, ct);
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (resp.StatusCode == HttpStatusCode.NotFound)
            return CertificationResult.NotPublished();

        if (!resp.IsSuccessStatusCode)
            return CertificationResult.Fail($"Compatibility check failed ({(int)resp.StatusCode} {resp.ReasonPhrase}). {Trim(body)}");

        return CertificationResult.Found(body);
    }

    /// <summary>
    /// Fetches the champion base-stat catalog RSL Companion currently publishes, so
    /// <c>heroes[].baseStats</c> can follow a game rebalance without shipping a build.
    ///
    /// <para>Returns the body verbatim; validating and installing it is
    /// <see cref="HeroBaseStatsUpdate"/>'s job, because the shape is the extraction engine's and only
    /// the engine defines it. A 404 is the normal answer while nothing serves this endpoint yet, so it
    /// is a distinct outcome rather than a failure.</para>
    ///
    /// <para>Sends the <c>generatedAt</c> this PC already holds as <c>since</c>, so a server that
    /// wants to can answer 304/404 rather than shipping 2.2 MB the client would then discard. The
    /// client re-checks anyway — the parameter is an optimisation, never the guard.</para>
    /// </summary>
    public async Task<CertificationResult> GetHeroBaseStatsAsync(
        DateTimeOffset? since, CancellationToken ct = default)
    {
        var path = _config.HeroBaseStatsEndpoint +
                   (since is { } s ? $"?since={Uri.EscapeDataString(s.UtcDateTime.ToString("O"))}" : "");

        using var req = await BuildRequestAsync(HttpMethod.Get, path, ct);
        using var resp = await _http.SendAsync(req, ct);

        if (resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NotModified)
            return CertificationResult.NotPublished();

        if (!resp.IsSuccessStatusCode)
            return CertificationResult.Fail(
                $"Base-stat catalog lookup failed ({(int)resp.StatusCode} {resp.ReasonPhrase}).");

        return CertificationResult.Found(await resp.Content.ReadAsStringAsync(ct));
    }

    /// <summary>
    /// Ends the session server-side: the API blacklists the ID token we present and asks Firebase to
    /// revoke the user's refresh tokens, so the copy saved on this disk stops working immediately
    /// rather than whenever it happens to be noticed.
    ///
    /// <para><b>Firebase revocation is per-user, not per-device.</b> There is no way to revoke just
    /// this install's token, so this also signs the user out of their browser. That is why it backs
    /// an explicit "sign out everywhere" and never a plain sign-out.</para>
    ///
    /// <para>Best-effort by design: a failure here must not strand the user in a signed-in UI, so the
    /// caller clears local state regardless and this reports only whether the server agreed.</para>
    /// </summary>
    public async Task<bool> RevokeSessionAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = await BuildRequestAsync(HttpMethod.Post, _config.LogoutEndpoint, ct);
            req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            using var resp = await _http.SendAsync(req, ct);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false; // offline, or the token already expired — local sign-out still proceeds
        }
    }

    private static string Trim(string s) => s.Length > 500 ? s[..500] + "…" : s;
}

/// <summary>
/// The outcome of a sync. <paramref name="Message"/> is what the user is told; <paramref name="Detail"/>
/// is the status line and response body behind it, which belongs in the activity log's diagnostic
/// level rather than in front of a player who only wants to know whether it worked.
/// </summary>
public readonly record struct UploadResult(bool Success, string Message, string? Detail = null)
{
    public static UploadResult Ok(string message, string? detail = null) => new(true, message, detail);
    public static UploadResult Fail(string message, string? detail = null) => new(false, message, detail);
}

public enum CertificationStatus
{
    /// <summary>The server published a mapping for this build.</summary>
    Found,
    /// <summary>No mapping yet — the expected answer for a game update we haven't mapped.</summary>
    NotPublished,
    /// <summary>The lookup itself failed (offline, server error).</summary>
    Failed,
}

public readonly record struct CertificationResult(CertificationStatus Status, string? Body, string? Error)
{
    public static CertificationResult Found(string body) => new(CertificationStatus.Found, body, null);
    public static CertificationResult NotPublished() => new(CertificationStatus.NotPublished, null, null);
    public static CertificationResult Fail(string error) => new(CertificationStatus.Failed, null, error);
}
