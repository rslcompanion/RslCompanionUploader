namespace RslCompanionUploader.Auth;

/// <summary>
/// The result of a successful sign-in: a Firebase ID token (the Bearer sent to the RaidTools API),
/// its refresh token, and enough identity to show who is logged in.
/// </summary>
public sealed class AuthSession
{
    public required string IdToken { get; set; }
    public required string RefreshToken { get; set; }
    public required DateTime ExpiresAtUtc { get; set; }
    public string? Uid { get; init; }
    public string? Email { get; init; }
    public string? DisplayName { get; init; }

    /// <summary>True when the ID token is within two minutes of expiry (refresh before using it).</summary>
    public bool IsExpiringSoon => DateTime.UtcNow >= ExpiresAtUtc.AddMinutes(-2);

    /// <summary>
    /// Whether the ID token carries RSL Companion's <c>role: "admin"</c> custom claim — the same claim
    /// the API's <c>AdminOnly</c> policy checks. Read from the token itself, so it follows the claim
    /// across refreshes without a round trip.
    ///
    /// <para>This gates <b>visibility only</b> (the activity log's diagnostic lines), never access:
    /// the token is not verified here, and nothing it unlocks is more than what this process already
    /// holds in memory. The server remains the authority for anything admin-only.</para>
    /// </summary>
    public bool IsAdmin
    {
        get
        {
            var token = IdToken;
            if (!ReferenceEquals(token, _adminFor)) { _admin = ReadAdminClaim(token); _adminFor = token; }
            return _admin;
        }
    }

    private string? _adminFor;
    private bool _admin;

    private static bool ReadAdminClaim(string jwt)
    {
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length < 2) return false;
            var b64 = parts[1].Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            using var doc = System.Text.Json.JsonDocument.Parse(Convert.FromBase64String(b64));
            return doc.RootElement.TryGetProperty("role", out var role)
                && role.ValueKind == System.Text.Json.JsonValueKind.String
                && role.GetString() == "admin";
        }
        catch
        {
            return false; // unreadable token → not an admin; the API will say what it thinks
        }
    }
}
