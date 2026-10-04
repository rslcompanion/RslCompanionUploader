using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace RslCompanionUploader;

/// <summary>
/// A newer release than the one running.
///
/// <para><paramref name="InstallerUrl"/> is the setup exe itself, which is what the update banner
/// actually uses: the user asked for a new version, not for a GitHub page.</para>
///
/// <para><paramref name="ReleaseUrl"/> is the human-readable release page, kept for diagnostics and
/// <b>never shown to a user</b>. Every manual way out points at <see cref="DownloadPageUrl"/>
/// instead — one file rather than a release page's six assets, one of which is a self-signed
/// <c>.msix</c> that cannot install on a machine that has not already trusted the certificate.</para>
/// </summary>
public sealed record UpdateInfo(
    ReleaseVersion Version,
    string ReleaseUrl,
    string? InstallerUrl = null,
    string? InstallerName = null,
    long InstallerSize = 0,
    string? ChecksumUrl = null);

public enum UpdateCheckStatus { UpdateAvailable, UpToDate, Failed }

public sealed record UpdateCheckResult(UpdateCheckStatus Status, UpdateInfo? Info = null);

/// <summary>
/// Checks GitHub for a newer release than the one currently running: the "latest release" API for
/// production installs, the release list (pre-releases included) on the dev channel.
/// Never throws — a failed/slow check (offline, rate-limited, GitHub down) reports
/// <see cref="UpdateCheckStatus.Failed"/> rather than blocking or crashing the caller.
/// </summary>
public static class UpdateChecker
{
    private const string LatestReleaseApiUrl =
        "https://api.github.com/repos/rslcompanion/RslCompanionUploader/releases/latest";

    /// <summary>The release list, pre-releases included — only read on the dev channel.</summary>
    private const string ReleasesApiUrl =
        "https://api.github.com/repos/rslcompanion/RslCompanionUploader/releases?per_page=30";

    // 10 s, not 5: the check that matters most runs seconds after launch, against a connection that
    // may still be coming up, and a cold DNS + TLS handshake to api.github.com can eat most of a
    // five-second budget on its own. Timing out there reports "no internet" to someone who has it —
    // and on the background poll that answer is invisible, so the session simply never learns a
    // release exists. Nothing waits on this call but a background poll and one menu item.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>
    /// Where someone finishes an update by hand. It resolves to the release's unversioned installer,
    /// so it is one download rather than the release page's six assets to choose between — which is
    /// what the update banner offers as a link when the automatic download can't complete.
    /// </summary>
    public const string DownloadPageUrl = "https://get.rslcompanion.com";

    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    /// <summary>
    /// Asks GitHub whether a newer release than the running one exists.
    ///
    /// <para><paramref name="includePrereleases"/> picks the channel. <b>False is what every
    /// production user gets</b>: <c>/releases/latest</c>, which GitHub never answers with a
    /// pre-release — that is what keeps a <c>v1.2.0-dev.1</c> build away from people who did not ask
    /// for one (the release workflow publishes every labelled tag as a pre-release, never "latest").
    /// True reads the release list and takes the newest non-draft one, pre-release or not, so a dev
    /// install is offered the next dev build and, when it ships, the production release.</para>
    /// </summary>
    public static async Task<UpdateCheckResult> CheckForUpdateAsync(bool includePrereleases = false)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, includePrereleases ? ReleasesApiUrl : LatestReleaseApiUrl);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("RslCompanionUploader", CurrentVersion.ToString()));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                return new UpdateCheckResult(UpdateCheckStatus.Failed);

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
            var release = includePrereleases ? PickNewestRelease(doc.RootElement) : doc.RootElement;
            return release is { } r
                ? Evaluate(r, ReleaseVersion.Current)
                : new UpdateCheckResult(UpdateCheckStatus.Failed);
        }
        catch
        {
            return new UpdateCheckResult(UpdateCheckStatus.Failed);
        }
    }

    /// <summary>
    /// The newest release in a <c>/releases</c> list by version (not by date — a hotfix to an older
    /// line can be published after a newer dev build). Drafts and tags that don't parse are skipped.
    /// </summary>
    internal static JsonElement? PickNewestRelease(JsonElement releases)
    {
        if (releases.ValueKind != JsonValueKind.Array) return null;

        JsonElement? best = null;
        ReleaseVersion? bestVersion = null;
        foreach (var release in releases.EnumerateArray())
        {
            if (release.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) continue;
            var tag = release.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            if (!ReleaseVersion.TryParse(tag, out var v)) continue;
            if (bestVersion is null || v > bestVersion)
            {
                best = release;
                bestVersion = v;
            }
        }
        return best;
    }

    /// <summary>Compares one release against the running build and picks its installer.</summary>
    internal static UpdateCheckResult Evaluate(JsonElement release, ReleaseVersion current)
    {
        var tag = release.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
        if (!ReleaseVersion.TryParse(tag, out var latest))
            return new UpdateCheckResult(UpdateCheckStatus.Failed);

        if (latest <= current)
            return new UpdateCheckResult(UpdateCheckStatus.UpToDate);

        var releaseUrl = release.TryGetProperty("html_url", out var h)
            ? h.GetString() ?? DownloadPageUrl
            : DownloadPageUrl;

        var assets = ReadAssets(release);
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
    private static Asset? PickInstaller(List<Asset> assets, ReleaseVersion version) =>
        assets.FirstOrDefault(a => a.Name.EndsWith($"-Setup-{version}.exe", StringComparison.OrdinalIgnoreCase))
        ?? assets.FirstOrDefault(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                                   && a.Name.Contains("Setup", StringComparison.OrdinalIgnoreCase));
}
