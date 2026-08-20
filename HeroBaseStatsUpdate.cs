#if EXTRACTION
using NewParserOpus.StaticData;

namespace RslCompanionUploader;

/// <summary>
/// Installs a newer champion base-stat catalog published by RSL Companion, so
/// <c>heroes[].baseStats</c> can follow a game rebalance without shipping a build.
///
/// <para>Same shape as <see cref="BuildCertification"/>, and for the same reason: the served blob is
/// written into the user's own copy under <c>%LOCALAPPDATA%\RslCompanion\</c> — which
/// <c>HeroBaseStatsCatalog.Load</c> already prefers over the bundled one — so a downloaded catalog
/// needs no other plumbing to take effect, and the install directory (not guaranteed writable if the
/// user redirected it) is never touched.</para>
///
/// <para><b>Nothing serves this endpoint yet.</b> The client half exists so the server half is a
/// deployment rather than a release; until then the lookup 404s, which lands on
/// <see cref="Outcome.NotPublished"/> and changes nothing.</para>
/// </summary>
public static class HeroBaseStatsUpdate
{
    public enum Outcome
    {
        /// <summary>A newer catalog was validated and written; the next export uses it.</summary>
        Installed,
        /// <summary>The server has nothing newer than what this PC already holds.</summary>
        NotPublished,
        /// <summary>Served, but unreadable or incomplete — discarded, not written.</summary>
        Rejected,
        /// <summary>Validated but the write failed (disk, permissions).</summary>
        Failed,
    }

    public readonly record struct Result(Outcome Outcome, string Message);

    /// <summary>
    /// Validates <paramref name="responseBody"/> and, when it is genuinely newer and genuinely
    /// usable, replaces the local catalog with it.
    ///
    /// <para>Validation runs the payload through <see cref="HeroBaseStatsCatalog.Parse"/> — the same
    /// code that would have to read the file afterwards — rather than through a second opinion on
    /// what "valid" means, which would be free to drift. A catalog that parses but carries no growth
    /// model or no champions is rejected there, so a served file can never leave this PC worse off
    /// than the bundled copy it would have replaced.</para>
    ///
    /// <para><b>The file is replaced whole, never merged.</b> Two catalogs can be cut from different
    /// game builds, and mixing one's champion stats with the other's growth table produces plausible
    /// numbers that are quietly wrong — the opposite of the per-field merge that is right for the
    /// offset catalog, where both entries describe the same build.</para>
    /// </summary>
    public static Result Apply(string responseBody)
    {
        HeroBaseStatsCatalog? served;
        try
        {
            served = HeroBaseStatsCatalog.Parse(responseBody, sourcePath: null);
        }
        catch (Exception ex)
        {
            return new(Outcome.Rejected, $"the champion stats RSL Companion sent weren't readable ({ex.Message}).");
        }

        if (served is null)
            return new(Outcome.Rejected, "the champion stats RSL Companion sent were incomplete.");

        // "Newer" is the served file's own generatedAt against whichever catalog is currently in
        // effect — which may be the bundled one. Without a stamp there is no way to order the two, and
        // overwriting a known-good catalog with an unorderable one is not a trade worth making.
        if (served.GeneratedAt is not { } servedAt)
            return new(Outcome.Rejected, "the champion stats RSL Companion sent weren't dated.");

        if (HeroBaseStatsCatalog.EffectiveGeneratedAt() is { } localAt && servedAt <= localAt)
            return new(Outcome.NotPublished, "your champion stats are already up to date.");

        try
        {
            var path = HeroBaseStatsCatalog.LocalPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, responseBody);
        }
        catch (Exception ex)
        {
            return new(Outcome.Failed, $"the champion stats couldn't be saved ({ex.Message}).");
        }

        var label = served.GameVersion is { Length: > 0 } v ? $"Raid {v}" : "the latest";
        return new(Outcome.Installed,
            $"champion stats for {label} are ready ({served.Count:N0} champion variants).");
    }
}
#endif
