using System.Text.Json;

namespace RslCompanionUploader;

/// <summary>
/// Strongly-typed application configuration, loaded from <c>appsettings.json</c> that sits
/// next to the executable. All values have sensible production defaults so the app also runs
/// if the file is missing.
/// </summary>
public sealed class AppConfig
{
    public string ApiBaseUrl { get; init; } = "https://api.rslcompanion.com";
    public string FrontendUrl { get; init; } = "https://rslcompanion.com";
    public string FirebaseApiKey { get; init; } = "AIzaSyCHDxSv2WwrZP2obwllWB9KwjyXaqklNog";
    public string FirebaseProjectId { get; init; } = "raid-account-manager";

    /// <summary>
    /// Server-relative path the "Export account" flow posts the extracted ConsolidatedProfile to.
    /// Matches RaidTools.Api's parser sync endpoint (ConsolidatedJsonSyncAdapter).
    /// </summary>
    public string SyncConsolidatedEndpoint { get; init; } = "/api/sync/consolidated/raw";

    /// <summary>
    /// Server-relative path that redeems the one-time handoff code the website puts on the
    /// <c>rslcompanion-extractor://sync?code=…</c> launch URI, returning a Firebase custom token this
    /// app signs in with. Unauthenticated by necessity — it is what establishes the session.
    /// </summary>
    public string HandoffExchangeEndpoint { get; init; } = "/api/extractor/handoff/exchange";

    /// <summary>
    /// Server-relative path the build-certification lookup GETs, with the GameAssembly SHA-256
    /// appended as a path segment. Returns a memory map for a game build this release predates, so a
    /// user who updated Raid first can skip the ~35 s calibration scan. Contract:
    /// <c>docs/build-certification-schema.md</c> / <c>.json</c>.
    /// </summary>
    public string BuildCertificationEndpoint { get; init; } = "/api/extractor/offsets";

    /// <summary>
    /// Server-relative path a newer champion base-stat catalog is GETed from, so
    /// <c>heroes[].baseStats</c> can follow a game rebalance without shipping a build. A copy is
    /// bundled beside the exe, and the downloaded one only replaces it when its <c>generatedAt</c> is
    /// newer; every failure — offline, 404, malformed — falls back to the bundled copy silently,
    /// because the catalog is an enrichment and an export without it is an export missing one
    /// optional property.
    ///
    /// <para><b>Nothing serves this yet</b>, but the path is the one RaidTools will actually use:
    /// its read routes are flat — <c>api/blessing-index</c>, <c>api/hero-progression</c>,
    /// <c>api/champion-stats</c> — and there is no <c>api/metadata/*</c> namespace
    /// (<c>api/admin/metadata</c> is the upload side). A placeholder under one would have 404ed
    /// permanently rather than until the server landed, and every already-installed client would
    /// carry the wrong default. The config key still exists so a change of mind is an
    /// <c>appsettings.json</c> edit rather than a release.</para>
    ///
    /// <para>The response must keep the catalog's own top-level shape — <c>growth</c>,
    /// <c>champions</c>, <c>statKinds</c> and the producer's <c>generatedAt</c> — since that is what
    /// <c>HeroBaseStatsCatalog.Parse</c> validates and what gets written to disk verbatim. Wrapping
    /// fields alongside them (as <c>HeroProgressionController</c> does with <c>isLoaded</c> and
    /// <c>revisionNumber</c>) is fine; nesting the catalog under a property is not.</para>
    /// </summary>
    public string HeroBaseStatsEndpoint { get; init; } = "/api/hero-base-stats";

    /// <summary>
    /// Server-relative path that ends the session everywhere: it blacklists the presented ID token
    /// and calls Firebase Admin's <c>RevokeRefreshTokens</c> for the user. Only reached from "sign out
    /// everywhere", because Firebase revocation is per-user and not per-device — it takes the
    /// browser's session down with this one.
    /// </summary>
    public string LogoutEndpoint { get; init; } = "/api/auth/logout";

    /// <summary>
    /// Server-relative page the app opens in the user's browser to sign in. When a session is already
    /// active there, that page mints a one-time handoff code and launches
    /// <c>rslcompanion-extractor://sync?code=...</c> with it, which this app redeems at
    /// <see cref="HandoffExchangeEndpoint"/>.
    /// </summary>
    public string ConnectExtractorPath { get; init; } = "/connect-extractor";

    /// <summary>
    /// Absolute URL <see cref="Forms.SignInPanel"/> opens in the user's real browser to sign in.
    ///
    /// <para>No <c>?embed=1</c>: the page has an embed mode for being hosted inside the app, and that
    /// approach was abandoned — Google and Microsoft do not complete a consent flow in an embedded
    /// browser. This is a normal browser tab, so it gets the normal page.</para>
    /// </summary>
    public string ConnectExtractorUrl => FrontendUrl + ConnectExtractorPath;

    public static AppConfig Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(path))
            return new AppConfig();

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;

            string Str(JsonElement el, string name, string fallback) =>
                el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString()! : fallback;

            var fb = root.TryGetProperty("Firebase", out var fbEl) ? fbEl : default;
            var ep = root.TryGetProperty("Endpoints", out var epEl) ? epEl : default;
            var def = new AppConfig();

            return new AppConfig
            {
                ApiBaseUrl = Str(root, "ApiBaseUrl", def.ApiBaseUrl).TrimEnd('/'),
                FrontendUrl = Str(root, "FrontendUrl", def.FrontendUrl).TrimEnd('/'),
                FirebaseApiKey = fb.ValueKind == JsonValueKind.Object ? Str(fb, "ApiKey", def.FirebaseApiKey) : def.FirebaseApiKey,
                FirebaseProjectId = fb.ValueKind == JsonValueKind.Object ? Str(fb, "ProjectId", def.FirebaseProjectId) : def.FirebaseProjectId,
                SyncConsolidatedEndpoint = ep.ValueKind == JsonValueKind.Object ? Str(ep, "SyncConsolidated", def.SyncConsolidatedEndpoint) : def.SyncConsolidatedEndpoint,
                HandoffExchangeEndpoint = ep.ValueKind == JsonValueKind.Object ? Str(ep, "HandoffExchange", def.HandoffExchangeEndpoint) : def.HandoffExchangeEndpoint,
                BuildCertificationEndpoint = ep.ValueKind == JsonValueKind.Object ? Str(ep, "BuildCertification", def.BuildCertificationEndpoint) : def.BuildCertificationEndpoint,
                HeroBaseStatsEndpoint = ep.ValueKind == JsonValueKind.Object ? Str(ep, "HeroBaseStats", def.HeroBaseStatsEndpoint) : def.HeroBaseStatsEndpoint,
                LogoutEndpoint = ep.ValueKind == JsonValueKind.Object ? Str(ep, "Logout", def.LogoutEndpoint) : def.LogoutEndpoint,
                ConnectExtractorPath = Str(root, "ConnectExtractorPath", def.ConnectExtractorPath),
            };
        }
        catch
        {
            return new AppConfig();
        }
    }
}
