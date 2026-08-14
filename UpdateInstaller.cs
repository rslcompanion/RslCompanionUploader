using System.Diagnostics;
using System.Security.Cryptography;

namespace RslCompanionUploader;

/// <summary>
/// Downloads a release's installer and hands the update over to it.
///
/// <para>The update banner used to open the GitHub release page in a browser, which left the user to
/// find the right asset among the installer, the unversioned copy, two checksums, an MSIX and a
/// certificate — and then to run it themselves. The banner now does the whole thing: download,
/// verify, run the installer silently, exit so it can replace the files, and let it start the new
/// build back up (see <c>installer/setup.iss</c>'s <c>/relaunch=1</c> [Run] entry).</para>
///
/// <para>Nothing here decides <i>what</i> to download: the URL always comes from the GitHub releases
/// API response for this repo (<see cref="UpdateChecker"/>), never from anything the user or the page
/// supplies, and the bytes are checked against the <c>.sha256</c> the same release publishes before
/// the file is allowed to become an executable path.</para>
/// </summary>
public static class UpdateInstaller
{
    // Its own client rather than UpdateChecker's: that one has a 5-second timeout, which is right for
    // a metadata probe and far too short for a ~70 MB installer on a slow line.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(15) };

    /// <summary>Where the installer is staged. Per-user temp — this is scratch, not something to keep.</summary>
    private static string StagingDir =>
        Path.Combine(Path.GetTempPath(), "RslCompanionUploader", "update");

    /// <summary>
    /// Downloads the release installer, verifies it, and returns its path.
    ///
    /// <para><paramref name="progress"/> receives 0–100 whenever the percentage changes (not per
    /// chunk — that would be thousands of UI pushes for one download). A release whose asset carries
    /// no <c>Content-Length</c> simply reports nothing rather than a made-up number.</para>
    /// </summary>
    public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<int>? progress, CancellationToken ct)
    {
        if (info.InstallerUrl is null || info.InstallerName is null)
            throw new InvalidOperationException("This release publishes no installer to download.");

        Directory.CreateDirectory(StagingDir);
        ClearStagingDir();

        var target = Path.Combine(StagingDir, info.InstallerName);
        // Downloaded under a .part name and only renamed once the checksum passes, so an interrupted
        // or corrupted download can never be left sitting there as a runnable .exe.
        var partial = target + ".part";

        using (var response = await Http.GetAsync(info.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? info.InstallerSize;

            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None);

            var buffer = new byte[81920];
            long received = 0;
            int lastPercent = -1, read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
                received += read;
                if (total <= 0) continue;
                var percent = (int)Math.Min(100, received * 100 / total);
                if (percent == lastPercent) continue;
                lastPercent = percent;
                progress?.Report(percent);
            }
        }

        await VerifyChecksumAsync(partial, info.ChecksumUrl, ct);

        await FinalizeAsync(partial, target, ct);
        return target;
    }

    /// <summary>
    /// Renames the verified download into place, retrying briefly.
    ///
    /// <para><b>A plain <see cref="File.Move(string, string, bool)"/> here fails intermittently, and it
    /// was caught doing it.</b> Antivirus opens a freshly written executable to scan it the moment the
    /// handle closes — which is exactly when this runs — and the rename comes back
    /// <see cref="UnauthorizedAccessException"/>. The scan is over in well under a second, but the
    /// single attempt turned a completed, checksum-verified 41 MB download into "couldn't download the
    /// update", sending the user to the browser to fetch the same bytes again.</para>
    ///
    /// <para>So: retry with a widening gap, ~9 s in total. Anything still refusing after that is a
    /// real failure (no permission, no disk) and is thrown to the caller, which does have a fallback.</para>
    /// </summary>
    private static async Task FinalizeAsync(string partial, string target, CancellationToken ct)
    {
        const int attempts = 10;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(partial, target, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException && attempt < attempts)
            {
                await Task.Delay(200 * attempt, ct);
            }
        }
    }

    /// <summary>
    /// Starts the downloaded installer and returns immediately. <b>The caller must exit the app right
    /// after</b> — the installer is replacing the files this process is running from.
    ///
    /// <para><c>/SILENT</c> shows a progress window but no wizard: by this point the user has said
    /// they want this version, so a wizard is a step with no decision left in it.
    /// <c>/NORESTARTAPPLICATIONS</c> keeps the restart manager from launching the app back up on its
    /// own — without it, a <paramref name="relaunch"/> install would produce two windows.</para>
    ///
    /// <para><paramref name="relaunch"/> distinguishes the two ways an update gets applied. True is
    /// "restart now", clicked deliberately: the app should come back. False is the app being applied
    /// on the way out, because the user quit — bringing the window back up would be reopening an app
    /// they just closed.</para>
    /// </summary>
    public static void Launch(string installerPath, bool relaunch) =>
        Process.Start(new ProcessStartInfo(installerPath)
        {
            UseShellExecute = true,
            Arguments = "/SILENT /CLOSEAPPLICATIONS /NORESTARTAPPLICATIONS /NORESTART"
                      + (relaunch ? " /relaunch=1" : ""),
        });

    /// <summary>
    /// Checks the download against the <c>.sha256</c> published beside it in the same release.
    ///
    /// <para>A checksum that cannot be fetched or parsed is not fatal — it leaves us exactly where a
    /// manual browser download would be, and both files come from the same TLS-protected origin. A
    /// checksum that is fetched and <i>disagrees</i> is fatal: the file is deleted rather than run.</para>
    /// </summary>
    private static async Task VerifyChecksumAsync(string path, string? checksumUrl, CancellationToken ct)
    {
        if (checksumUrl is null) return;

        string expected;
        try
        {
            // Published sha256sum-style: "<hex> *<filename>".
            var text = await Http.GetStringAsync(checksumUrl, ct);
            expected = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return;
        }

        if (expected.Length != 64) return;

        await using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
        if (string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) return;

        await stream.DisposeAsync();
        TryDelete(path);
        throw new InvalidOperationException(
            $"the download didn't match the checksum published with the release (expected {expected.ToLowerInvariant()}, got {actual.ToLowerInvariant()})");
    }

    /// <summary>Drops whatever a previous update attempt left behind, so temp doesn't collect installers.</summary>
    private static void ClearStagingDir()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(StagingDir)) TryDelete(file);
        }
        catch
        {
            // Best effort — a locked leftover is not a reason to refuse the update.
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }
}
