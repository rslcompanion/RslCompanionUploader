using System.Text.Json.Serialization;

namespace RslCompanionUploader.Api;

/// <summary>
/// Mirrors the objects returned by <c>GET /api/accounts</c> (see RaidApiControllers.GetAccounts).
/// Only the fields the dropdown needs are mapped; the rest are ignored.
/// </summary>
public sealed class AccountSummary
{
    [JsonPropertyName("id")] public int Id { get; set; }

    /// <summary>
    /// The account's numeric in-game user id — the "handle" identity (distinct from the signed-in
    /// uploader). The server derives it from the extracted accountId, so it's what we match a running
    /// game account against, and it's the profileId the per-account upload endpoints expect.
    /// </summary>
    [JsonPropertyName("userId")] public int UserId { get; set; }

    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("clanName")] public string? ClanName { get; set; }
    [JsonPropertyName("heroCount")] public int HeroCount { get; set; }

    /// <summary>
    /// Server-side count of the account's <b>gear</b> — slots 1–6, and nothing else.
    ///
    /// <para>This comment used to say the count spanned gear and accessories both, and label it
    /// "artifacts, never gear" on that basis. That stopped being true at schema 10, when the payload
    /// split into two arrays: the server now counts them apart
    /// (<c>ArtifactCount = dto.Artifacts.Count</c>, <c>AccessoryCount = dto.Accessories.Count</c> in
    /// RaidTools' SyncManager). The halves are comparable in size — 2,851 gear against 2,969
    /// accessories on the reference account (docs/export-schema.md) — so showing this one alone
    /// under-reports a vault by roughly half.</para>
    /// </summary>
    [JsonPropertyName("artifactCount")] public int ArtifactCount { get; set; }

    /// <summary>
    /// Rings, amulets and banners — slots 7–9, the game's second gear counter.
    ///
    /// <para>Nullable so an API that doesn't send the field renders as nothing rather than as a
    /// confident "0 accessories". The server has counted these since schema 10, but a snapshot
    /// imported before that carries a real 0, which is a lie that corrects itself on the next sync.</para>
    /// </summary>
    [JsonPropertyName("accessoryCount")] public int? AccessoryCount { get; set; }
    [JsonPropertyName("lastSyncMethod")] public string? LastSyncMethod { get; set; }

    /// <summary>
    /// True for an account somebody else owns and shared with this user: the list carries their
    /// pins beside the user's own imports (RaidTools <c>docs/account-sharing.md</c>). The user can
    /// only unlink it (drop the pin), never delete it. An account the user also imported themselves
    /// comes back once, as their own copy.
    /// </summary>
    [JsonPropertyName("shared")] public bool Shared { get; set; }

    /// <summary>How a shared account reached this user: <c>clan</c>, <c>public</c> or <c>link</c>. Null when owned.</summary>
    [JsonPropertyName("shareOrigin")] public string? ShareOrigin { get; set; }

    /// <summary>When this account was last synced. Returned by <c>GET /api/accounts</c>.</summary>
    [JsonPropertyName("lastSyncDate")] public DateTimeOffset? LastSyncDate { get; set; }

    /// <summary>Text shown in the dropdown.</summary>
    public override string ToString()
    {
        var label = string.IsNullOrWhiteSpace(Name) ? $"Account {UserId}" : Name;
        if (!string.IsNullOrWhiteSpace(ClanName))
            label += $"  [{ClanName}]";
        var pieces = $"{ArtifactCount} gear";
        if (AccessoryCount is int accessories) pieces += $", {accessories} accessories";
        return $"{label}  —  {HeroCount} heroes, {pieces}  (#{UserId})";
    }
}
