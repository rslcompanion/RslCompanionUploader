#if EXTRACTION
using System.Text.Json;
using System.Text.Json.Nodes;
using NewParserOpus.Il2Cpp;

namespace RslCompanionUploader;

/// <summary>
/// Applies a server-published memory map for a game build this release predates, so a user who
/// updated Raid before we shipped a matching version skips the ~35 s calibration scan.
///
/// The offsets blob is written into the user's own catalog
/// (<c>%LOCALAPPDATA%\RslCompanion\calibrated-offsets.json</c>) rather than a file of its own: that
/// is the same catalog self-calibration writes and <c>KnownOffsets.TryResolve</c> already reads, so
/// a downloaded map needs no other plumbing to take effect. It is merged as raw JSON — the entry's
/// shape is the extraction engine's, and re-declaring it here would be a second definition free to
/// drift from the one that matters.
/// </summary>
public static class BuildCertification
{
    public enum Outcome { Applied, NotPublished, NeedsNewerUploader, Failed }

    public readonly record struct Result(Outcome Outcome, string Message);

    /// <summary>
    /// Validates <paramref name="responseBody"/> against the running build and, when it fits, folds
    /// its offsets into the local catalog.
    ///
    /// The hash is re-checked against the build we asked about rather than trusted: a mapping for the
    /// wrong build does not fail loudly, it produces silently wrong reads — the same failure mode the
    /// engine's hash-keyed catalog exists to prevent.
    /// </summary>
    public static Result Apply(string responseBody, string gameAssemblyHash, string uploaderVersion)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(responseBody);
        }
        catch (JsonException ex)
        {
            return new(Outcome.Failed, $"the server's response wasn't readable ({ex.Message}).");
        }

        if (root is not JsonObject obj)
            return new(Outcome.Failed, "the server's response wasn't readable.");

        var servedHash = obj["gameAssemblyHash"]?.GetValue<string>();
        if (!string.Equals(servedHash, gameAssemblyHash, StringComparison.OrdinalIgnoreCase))
            return new(Outcome.Failed, "the server returned a map for a different game build.");

        if (obj["offsets"] is not JsonObject offsets)
            return new(Outcome.NotPublished, "the server has no map for this game version yet.");

        // The compatibility half of the check: a map can exist while describing fields this engine
        // can't read, in which case updating the app is the fix and applying the map is not.
        if (obj["minUploaderVersion"]?.GetValue<string>() is string min && IsOlder(uploaderVersion, min))
            return new(Outcome.NeedsNewerUploader,
                $"this game version needs uploader {min} or newer — you're on {uploaderVersion}.");

        try
        {
            MergeIntoLocalCatalog(gameAssemblyHash, offsets);
        }
        catch (Exception ex)
        {
            return new(Outcome.Failed, $"the map couldn't be saved ({ex.Message}).");
        }

        var label = obj["gameVersion"]?.GetValue<string>();
        return new(Outcome.Applied, string.IsNullOrWhiteSpace(label)
            ? "this game version is now certified on this PC."
            : $"Raid {label} is now certified on this PC.");
    }

    /// <summary>
    /// Whether this PC already holds a map for the build — certified or self-calibrated. Reads the
    /// catalog file directly rather than going through <c>KnownOffsets.TryResolve</c>, which loads
    /// and logs; this runs on every game-state transition and only needs to know if the key is there.
    /// </summary>
    public static bool HasLocalMap(string gameAssemblyHash)
    {
        try
        {
            var path = KnownOffsets.LocalCatalogPath;
            if (!File.Exists(path)) return false;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty("builds", out var builds)
                && builds.EnumerateObject().Any(b => b.NameEquals(gameAssemblyHash));
        }
        catch
        {
            return false;
        }
    }

    private static void MergeIntoLocalCatalog(string gameAssemblyHash, JsonObject offsets)
    {
        var path = KnownOffsets.LocalCatalogPath;

        JsonObject catalog;
        try
        {
            catalog = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject()
                                        : new JsonObject();
        }
        catch (JsonException)
        {
            catalog = new JsonObject();
        }

        if (catalog["builds"] is not JsonObject builds)
        {
            builds = new JsonObject();
            catalog["builds"] = builds;
        }

        // Replace rather than merge per field: unlike two calibrations of the same build, a published
        // map is authoritative for every field it carries.
        builds[gameAssemblyHash] = offsets.DeepClone();

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, catalog.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// True when <paramref name="version"/> is behind <paramref name="minimum"/>. Anything that
    /// doesn't parse is treated as new enough — refusing to certify over an unreadable version string
    /// would strand the user on a scan for no gain.
    /// </summary>
    private static bool IsOlder(string version, string minimum)
        => Version.TryParse(version, out var v) && Version.TryParse(minimum, out var m) && v < m;
}
#endif
