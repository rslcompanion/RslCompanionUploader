using System.Text.Json.Serialization;

namespace RslCompanionUploader.Api;

/// <summary>
/// One owned account's sharing settings, as <c>GET /api/shared-accounts/settings</c> returns them
/// (RaidTools' <c>SharedAccountsController.Describe</c>). Every owned account comes back, shared or
/// not; one that was never shared has everything off.
///
/// <para>Read-only here. The settings are changed on rslcompanion.com, where the owner can also see
/// who holds a pin; this app only shows which grants are on.</para>
/// </summary>
public sealed class AccountSharing
{
    /// <summary>The in-game id — the same number as <see cref="AccountSummary.UserId"/>.</summary>
    [JsonPropertyName("accountId")] public int AccountId { get; set; }

    /// <summary>Anyone signed in may find this account by name.</summary>
    [JsonPropertyName("publicLookup")] public bool PublicLookup { get; set; }

    /// <summary>Members of <see cref="ClanName"/> may find this account by name.</summary>
    [JsonPropertyName("clanLookup")] public bool ClanLookup { get; set; }

    [JsonPropertyName("clanName")] public string? ClanName { get; set; }

    /// <summary>Viewers may edit this account's planners, not only read it.</summary>
    [JsonPropertyName("allowEditing")] public bool AllowEditing { get; set; }

    /// <summary>Non-null only while a share link is live. Never shown: it is the link's secret.</summary>
    [JsonPropertyName("linkToken")] public string? LinkToken { get; set; }

    /// <summary>How many people currently hold a pin on the account.</summary>
    [JsonPropertyName("viewerCount")] public int ViewerCount { get; set; }

    /// <summary>
    /// True when any grant is on. A pin alone doesn't count: pins resolve only through a grant, so
    /// one left behind after every grant went off gives nobody access.
    /// </summary>
    [JsonIgnore]
    public bool IsShared => PublicLookup || ClanLookup || !string.IsNullOrEmpty(LinkToken);
}
