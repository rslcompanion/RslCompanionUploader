namespace RslCompanionUploader.Auth;

/// <summary>Tokens pulled out of a WebView2 login session's Firebase IndexedDB store.</summary>
public sealed class HarvestedTokens
{
    public required string IdToken { get; init; }
    public required string RefreshToken { get; init; }
    public required DateTime ExpiresAtUtc { get; init; }
    public string? Uid { get; init; }
    public string? Email { get; init; }
    public string? DisplayName { get; init; }
}
