using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace RslCompanionUploader;

/// <summary>
/// A newer release than the one running.
///
/// <para><paramref name="ReleaseUrl"/> is the human-readable release page — the fallback for when
/// there is nothing to install automatically (a release with no installer asset, an MSIX build, or a
/// download that failed). <paramref name="InstallerUrl"/> is the setup exe itself, which is what the
/// update banner actually uses: the user asked for a new version, not for a GitHub page.</para>
/// </summary>
public sealed record UpdateInfo(
    Version Version,
    string ReleaseUrl,
    string? InstallerUrl = null,
    string? InstallerName = null,
    long InstallerSize = 0,
    string? ChecksumUrl = null);

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

    /// <summary>
    /// Where someone finishes an update by hand. It resolves to the release's unversioned installer,
    /// so it is one download rather than the release page's six assets to choose between — which is
    /// what the update banner offers as a link when the automatic download can't complete.
    /// </summary>
    public const string DownloadPageUrl = "https://get.rslcompanion.com";

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
                ? h.GetString() ?? DownloadPageUrl
                : DownloadPageUrl;

            var assets = ReadAssets(doc.RootElement);
            var installer = PickInstaller(assets, latest);
            var checksum = installer is null
                ? null
                : assets.FirstOrDefault(a => a.Name.Equals(installer.Name + ".sha256", StringComparison.OrdinalIgnoreCase));

            return new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, new UpdateInfo(
                latest,
                releaseUrl,
                installer?.Url,
                installer?.Name,
                installer?.Size ?? 0,
                checksum?.Url));
        }
        catch
        {
            return new UpdateCheckResult(UpdateCheckStatus.Failed);
        }
    }

    private sealed record Asset(string Name, string Url, long Size);

    private static List<Asset> ReadAssets(JsonElement release)
    {
        var list = new List<Asset>();
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var a in assets.EnumerateArray())
        {
            var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
            var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(url)) continue;
            var size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var bytes) ? bytes : 0;
            list.Add(new Asset(name, url, size));
        }
        return list;
    }

    /// <summary>
    /// The Inno installer out of a release's assets.
    ///
    /// <para>Every release carries the same setup exe twice — version-stamped
    /// (<c>…-Setup-1.2.0.exe</c>) and unversioned (<c>…-Setup.exe</c>, the target of
    /// get.rslcompanion.com). The stamped one is preferred because it is the name the published
    /// <c>.sha256</c> and the release notes refer to. The <c>.msix</c> is deliberately never picked:
    /// it is signed with a self-signed certificate and cannot install itself onto a machine that
    /// hasn't already trusted it.</para>
    /// </summary>
    private static Asset? PickInstaller(List<Asset> assets, Version version) =>
        assets.FirstOrDefault(a => a.Name.EndsWith($"-Setup-{version}.exe", StringComparison.OrdinalIgnoreCase))
        ?? assets.FirstOrDefault(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                                   && a.Name.Contains("Setup", StringComparison.OrdinalIgnoreCase));
}
