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

    /// <summary>Server-relative path the "Upload account resources" button posts to.</summary>
    public string UploadResourcesEndpoint { get; init; } = "/api/profile-import/resources";

    /// <summary>Server-relative path the "Upload champions" button posts to.</summary>
    public string UploadChampionsEndpoint { get; init; } = "/api/profile-import/champions";

    /// <summary>
    /// Server-relative path the "Sync from game" flow posts the extracted ConsolidatedProfile to.
    /// Matches RaidTools.Api's parser sync endpoint (ConsolidatedJsonSyncAdapter).
    /// </summary>
    public string SyncConsolidatedEndpoint { get; init; } = "/api/sync/consolidated/raw";

    /// <summary>
    /// Server-relative page the app opens in the user's browser to sign in. When a session is already
    /// active there, that page should launch <c>rslcompanion-extractor://sync?rt=...</c> to hand the
    /// refresh token back to the app.
    /// </summary>
    public string ConnectExtractorPath { get; init; } = "/connect-extractor";

    /// <summary>Absolute URL <see cref="Forms.BrowserSignInForm"/> opens for browser sign-in.</summary>
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
                UploadResourcesEndpoint = ep.ValueKind == JsonValueKind.Object ? Str(ep, "UploadResources", def.UploadResourcesEndpoint) : def.UploadResourcesEndpoint,
                UploadChampionsEndpoint = ep.ValueKind == JsonValueKind.Object ? Str(ep, "UploadChampions", def.UploadChampionsEndpoint) : def.UploadChampionsEndpoint,
                SyncConsolidatedEndpoint = ep.ValueKind == JsonValueKind.Object ? Str(ep, "SyncConsolidated", def.SyncConsolidatedEndpoint) : def.SyncConsolidatedEndpoint,
                ConnectExtractorPath = Str(root, "ConnectExtractorPath", def.ConnectExtractorPath),
            };
        }
        catch
        {
            return new AppConfig();
        }
    }
}
