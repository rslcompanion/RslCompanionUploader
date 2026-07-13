using System.Text.Json.Serialization;

namespace RslCompanionUploader.Api;

/// <summary>
/// Mirrors the objects returned by <c>GET /api/accounts</c> (see RaidApiControllers.GetAccounts).
/// Only the fields the dropdown needs are mapped; the rest are ignored.
/// </summary>
public sealed class AccountSummary
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("userId")] public int UserId { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("clanName")] public string? ClanName { get; set; }
    [JsonPropertyName("heroCount")] public int HeroCount { get; set; }
    [JsonPropertyName("artifactCount")] public int ArtifactCount { get; set; }
    [JsonPropertyName("lastSyncMethod")] public string? LastSyncMethod { get; set; }

    /// <summary>Text shown in the dropdown.</summary>
    public override string ToString()
    {
        var label = string.IsNullOrWhiteSpace(Name) ? $"Account {UserId}" : Name;
        if (!string.IsNullOrWhiteSpace(ClanName))
            label += $"  [{ClanName}]";
        return $"{label}  —  {HeroCount} heroes, {ArtifactCount} gear  (#{UserId})";
    }
}
