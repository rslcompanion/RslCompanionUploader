using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace RslCompanionUploader;

public sealed record UpdateInfo(Version Version, string ReleaseUrl);

public enum UpdateCheckStatus { UpdateAvailable, UpToDate, Failed }

public sealed record UpdateCheckResult(UpdateCheckStatus Status, UpdateInfo? Info = null);

/// <summary>
/// Checks GitHub's "latest release" API for a newer version than the one currently running.
/// Never throws — a failed/slow check (offline, rate-limited, GitHub down) reports
/// <see cref="UpdateCheckStatus.Failed"/> rather than blocking or crashing the caller.
/// </summary>
public static class UpdateChecker
{
    private const string LatestReleaseApiUrl =
        "https://api.github.com/repos/rslcompanion/RslCompanionUploader/releases/latest";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    public static async Task<UpdateCheckResult> CheckForUpdateAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApiUrl);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("RslCompanionUploader", CurrentVersion.ToString()));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                return new UpdateCheckResult(UpdateCheckStatus.Failed);

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
            var tag = doc.RootElement.GetProperty("tag_name").GetString();
            if (string.IsNullOrEmpty(tag) || !Version.TryParse(tag.TrimStart('v'), out var latest))
                return new UpdateCheckResult(UpdateCheckStatus.Failed);

            if (latest <= CurrentVersion)
                return new UpdateCheckResult(UpdateCheckStatus.UpToDate);

            var releaseUrl = doc.RootElement.TryGetProperty("html_url", out var h)
                ? h.GetString() ?? "https://get.rslcompanion.com"
                : "https://get.rslcompanion.com";
            return new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, new UpdateInfo(latest, releaseUrl));
        }
        catch
        {
            return new UpdateCheckResult(UpdateCheckStatus.Failed);
        }
    }
}
