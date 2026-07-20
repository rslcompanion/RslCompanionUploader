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

    /// <summary>The live session. Replaced in place whenever the token is refreshed.</summary>
    public AuthSession Session { get; private set; }

    public RslCompanionApiClient(HttpClient http, AppConfig config, FirebaseAuthClient auth, AuthSession session)
    {
        _http = http;
        _config = config;
        _auth = auth;
        Session = session;
    }

    private async Task<string> ValidTokenAsync(CancellationToken ct)
    {
        if (Session.IsExpiringSoon)
            Session = await _auth.RefreshAsync(Session, ct);
        return Session.IdToken;
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
        using var req = await BuildRequestAsync(HttpMethod.Post, _config.SyncConsolidatedEndpoint, ct);
        req.Content = new StringContent(consolidatedJson, Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (resp.StatusCode == HttpStatusCode.NotFound)
            return UploadResult.Fail($"Endpoint not found (404): {_config.SyncConsolidatedEndpoint}\nThe server may not have this endpoint deployed yet.");

        if (!resp.IsSuccessStatusCode)
            return UploadResult.Fail($"Sync failed ({(int)resp.StatusCode} {resp.ReasonPhrase}).\n{Trim(body)}");

        return UploadResult.Ok($"Synced to RSL Companion ({(int)resp.StatusCode}).\n{Trim(body)}");
    }

    private static string Trim(string s) => s.Length > 500 ? s[..500] + "…" : s;
}

public readonly record struct UploadResult(bool Success, string Message)
{
    public static UploadResult Ok(string message) => new(true, message);
    public static UploadResult Fail(string message) => new(false, message);
}
