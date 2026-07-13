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
}
