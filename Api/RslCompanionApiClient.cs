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

    private async Task<AuthSession> ValidSessionAsync(CancellationToken ct)
    {
        var session = Session ?? throw new InvalidOperationException("Not signed in.");
        if (session.IsExpiringSoon)
            Session = session = await _auth.RefreshAsync(session, ct);
        return session;
    }

    /// <summary>
    /// Every authenticated call goes to the <b>session's</b> API (<see cref="AuthSession.Target"/>) —
    /// dev or prod, whichever minted the handoff code — so a Bearer token only ever travels back to
    /// the environment that issued it. Server-relative paths only: absolute URLs used to be passed
    /// through untouched, which no caller used and which is exactly how a token would leave its API.
    /// </summary>
    private async Task<HttpRequestMessage> BuildRequestAsync(HttpMethod method, string path, CancellationToken ct)
    {
        if (!path.StartsWith('/'))
            throw new ArgumentException("Expected a server-relative path.", nameof(path));
        var session = await ValidSessionAsync(ct);
        var req = new HttpRequestMessage(method, $"{session.Target.ApiBaseUrl}{path}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.IdToken);
        return req;
    }

    /// <summary>
    /// Whether RaidTools' resolved feature flags (global ▸ group ▸ user, <c>GET /api/features/effective</c>)
    /// have <paramref name="key"/> on for this user on the session's server. An absent key is off:
    /// every key this app asks about is dark by default. Any failure is "off" too — it decides only
    /// whether a menu item is shown, and the server enforces what the item leads to.
    /// </summary>
    public async Task<bool> IsFeatureOnAsync(string key, CancellationToken ct = default)
    {
        try
        {
            using var req = await BuildRequestAsync(HttpMethod.Get, "/api/features/effective", ct);
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return false;
            using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            return doc.RootElement.TryGetProperty("effective", out var eff)
                && eff.ValueKind == System.Text.Json.JsonValueKind.Object
                && eff.TryGetProperty(key, out var on)
                && on.ValueKind == System.Text.Json.JsonValueKind.True;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return false;
        }
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
              + "Please try again later. If it's still happening after a while, use Actions → Check for "
              + "updates: a newer version of this app may be sending to a route this one doesn't know.",
                $"404 from {endpoint}: {Trim(body)}");

        // 403 is a decision about this account on this server (dev without Extractor access), not a
        // fault — so it says who to ask, and is marked so the caller does not count it towards the
        // "maybe you're out of date" check that a run of rejections triggers.
        if (resp.StatusCode == HttpStatusCode.Forbidden)
            return UploadResult.Fail(
                ServerMessage(body)
                ?? $"Your account isn't allowed to upload to {Session!.Target.ApiHost}. Ask an RSL Companion admin for access.",
                $"403 from {endpoint}: {Trim(body)}") with { Forbidden = true };

        if (!resp.IsSuccessStatusCode)
            return UploadResult.Fail(
                "RSL Companion couldn't accept your data. Please try again in a few minutes. If it "
              + "keeps being rejected, use Actions → Check for updates — a rejection that doesn't clear "
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
    /// Admins only: publishes this PC's memory map for a game build, so every player on it gets the map
    /// from <c>GET /api/extractor/offsets/{hash}</c> instead of a local calibration scan. The server
    /// checks the entry names the same build and strips session-local addresses again.
    /// Returns null on success, else the reason in one sentence.
    /// </summary>
    public async Task<string?> PublishBuildMapAsync(string gameAssemblyHash, string offsetsJson, CancellationToken ct = default)
    {
        using var req = await BuildRequestAsync(HttpMethod.Put, $"/api/admin/extractor-offsets/{Uri.EscapeDataString(gameAssemblyHash)}", ct);
        req.Content = new StringContent($"{{\"offsets\":{offsetsJson}}}", System.Text.Encoding.UTF8, "application/json");
        using var resp = await _http.SendAsync(req, ct);
        if (resp.IsSuccessStatusCode) return null;
        var body = await resp.Content.ReadAsStringAsync(ct);
        return resp.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "This account can't publish memory maps.",
            HttpStatusCode.NotFound => "This server doesn't accept memory maps yet.",
            _ => $"Publishing failed ({(int)resp.StatusCode} {resp.ReasonPhrase}). {Trim(body)}",
        };
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

    /// <summary>
    /// Sends feedback about this app to RSL Companion's existing <c>POST /api/feedback</c> — the same
    /// inbox the website's feedback form writes to. <paramref name="source"/> lands in its
    /// <c>pageUrl</c> field, which is how an uploader report is told apart from a website one there.
    /// The server takes 5–2000 characters and a category of <c>bug</c>, <c>feature</c> or
    /// <c>general</c>; the caller is expected to have trimmed to that already.
    ///
    /// <para><paramref name="log"/> travels in its own field, not appended to the message, so it
    /// is not squeezed under the message cap (the server keeps up to 400k characters of it).
    /// <paramref name="context"/> is what this install says about itself — versions, the game
    /// account being played, OS — so a report can be traced to the player and build it came from.
    /// A server older than that field ignores both rather than failing.</para>
    /// </summary>
    public async Task<UploadResult> SubmitFeedbackAsync(string category, string message, string source,
        string? log = null, object? context = null, CancellationToken ct = default)
    {
        try
        {
            using var req = await BuildRequestAsync(HttpMethod.Post, _config.FeedbackEndpoint, ct);
            req.Content = JsonContent.Create(new { category, message, pageUrl = source, log, context });
            using var resp = await _http.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode)
                return UploadResult.Ok("Thanks — your feedback was sent.");

            var body = await resp.Content.ReadAsStringAsync(ct);
            return UploadResult.Fail("RSL Companion couldn't accept that feedback. Please try again in a moment.",
                $"{(int)resp.StatusCode} {resp.ReasonPhrase}: {Trim(body)}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return UploadResult.Fail("Couldn't reach RSL Companion to send your feedback. Check your connection and try again.",
                ex.Message);
        }
    }

    private static string Trim(string s) => s.Length > 500 ? s[..500] + "…" : s;

    /// <summary>The <c>message</c> of a RaidTools error body, when there is one worth showing.</summary>
    internal static string? ServerMessage(string body)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && doc.RootElement.TryGetProperty("message", out var m)
                && m.ValueKind == System.Text.Json.JsonValueKind.String
                && m.GetString() is { Length: > 0 and <= 500 } text
                    ? text
                    : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
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

    /// <summary>
    /// The server refused this account (403), which retrying or updating will not change. It is kept
    /// apart from other failures so a refusal never triggers the "you may be out of date" check.
    /// </summary>
    public bool Forbidden { get; init; }
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
