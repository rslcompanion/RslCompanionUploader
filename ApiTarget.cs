namespace RslCompanionUploader;

/// <summary>
/// One RSL Companion environment: the API a session talks to, the site that goes with it, and the
/// Firebase project whose tokens that API accepts. The three travel together because they cannot be
/// mixed — dev has its own database <b>and</b> its own Firebase project, so a custom token minted by
/// <c>api-dev</c> is rejected by the prod project's key, and a prod ID token means nothing to
/// <c>api-dev</c>.
///
/// <para>A session carries its target (<see cref="Auth.AuthSession.Target"/>), which is how "every
/// call after the handoff goes to the API that minted the code" holds without any global state: the
/// upload, the account list, feedback, token refresh and sign-out all read it off the session.</para>
/// </summary>
public sealed record ApiTarget(
    string Name,
    string ApiBaseUrl,
    string FrontendUrl,
    string FirebaseApiKey,
    bool IsProduction)
{
    /// <summary>Host of <see cref="ApiBaseUrl"/>, for the "DEV · api-dev.rslcompanion.com" badge.</summary>
    public string ApiHost => new Uri(ApiBaseUrl).Authority;

    public static readonly ApiTarget Production = new(
        "Production",
        "https://api.rslcompanion.com",
        "https://rslcompanion.com",
        "AIzaSyCHDxSv2WwrZP2obwllWB9KwjyXaqklNog",
        IsProduction: true);

    /// <summary>RaidTools' dev environment — Firebase project <c>rslcompanion-dev</c>.</summary>
    public static readonly ApiTarget Dev = new(
        "Dev",
        "https://api-dev.rslcompanion.com",
        "https://dev.rslcompanion.com",
        "AIzaSyChFNZada-PF2tTSUrZ7fR6gdKGbMQnSdg",
        IsProduction: false);

    /// <summary>
    /// A developer's local RaidTools API. Its frontend <c>environment.ts</c> signs in against the
    /// prod Firebase project, so the key is prod's. Only reachable in a Debug build.
    /// </summary>
    public static readonly ApiTarget Local = new(
        "Local",
        "https://localhost:7144",
        "http://localhost:4200",
        Production.FirebaseApiKey,
        IsProduction: false);

    /// <summary>
    /// The targets a launch URI may name. Everything else is refused. Compiled per build flavour, so
    /// a Release binary does not even contain an allow-list entry for localhost.
    /// </summary>
    public static IReadOnlyList<ApiTarget> Allowed { get; } =
#if DEBUG
        [Production, Dev, Local];
#else
        [Production, Dev];
#endif

    /// <summary>
    /// The built-in target — what a launch without <c>api</c> (every site build before 2026-09-30)
    /// and a session saved by an older version both use. It is <see cref="Production"/> unless
    /// <c>appsettings.json</c> overrides the URLs, which only a developer does.
    /// </summary>
    public static ApiTarget BuiltIn(AppConfig config) =>
        config.ApiBaseUrl == Production.ApiBaseUrl
        && config.FrontendUrl == Production.FrontendUrl
        && config.FirebaseApiKey == Production.FirebaseApiKey
            ? Production
            : new ApiTarget("Configured", config.ApiBaseUrl, config.FrontendUrl, config.FirebaseApiKey,
                            IsProduction: config.ApiBaseUrl == Production.ApiBaseUrl);

    /// <summary>
    /// Where the in-app Sign In button goes: the server Help ▸ Server… picked, if it is still on the
    /// allow-list, else the built-in one. A stored value that no longer resolves (a Debug build's
    /// localhost read by a Release build) falls back silently. It named nothing that could be used
    /// anyway, and the picker is where it gets changed.
    /// </summary>
    public static ApiTarget Preferred(AppConfig config, string? storedApiBaseUrl) =>
        TryResolve(storedApiBaseUrl) ?? BuiltIn(config);

    /// <summary>
    /// The feature key RaidTools grants per group for the Extractor on a non-prod server. On dev it is
    /// what the server enforces for sign-in and upload; on prod it is what shows Help ▸ Server….
    /// </summary>
    public const string DevAccessFeature = "extractor-dev-server";

    /// <summary>
    /// Maps an <c>api</c> value to an allowed target, or null. The value is <b>parsed</b> and its
    /// scheme, host and port compared exactly — never a prefix or substring test, which is what would
    /// let <c>https://api.rslcompanion.com.evil.example</c> or
    /// <c>https://api.rslcompanion.com@evil.example</c> through.
    ///
    /// <para>Beyond the origin, the value must be a bare origin: no user-info, no query, no fragment,
    /// and no path other than a single trailing <c>/</c> (which is normalised away). Anything else is
    /// refused rather than trimmed — the site sends <c>environment.apiUrl</c> verbatim, which is always
    /// a bare origin, so an extra part is either a bug worth hearing about or a link someone built by
    /// hand.</para>
    /// </summary>
    public static ApiTarget? TryResolve(string? api)
    {
        if (string.IsNullOrWhiteSpace(api)) return null;
        if (api.Trim() != api) return null; // no whitespace games; the site never pads the value
        if (!Uri.TryCreate(api, UriKind.Absolute, out var uri)) return null;
        if (uri.UserInfo.Length > 0) return null;
        if (uri.Query.Length > 0 || uri.Fragment.Length > 0) return null;
        if (uri.AbsolutePath != "/") return null;
        // Uri treats a backslash as a separator and quietly rewrites it; the canonical form must be
        // what was sent, less at most the one trailing slash.
        var canonical = uri.GetLeftPart(UriPartial.Authority);
        if (api != canonical && api != canonical + "/") return null;

        foreach (var target in Allowed)
        {
            var allowed = new Uri(target.ApiBaseUrl);
            if (uri.Scheme == allowed.Scheme
                && string.Equals(uri.IdnHost, allowed.IdnHost, StringComparison.OrdinalIgnoreCase)
                && uri.Port == allowed.Port)
                return target;
        }

        return null;
    }
}
