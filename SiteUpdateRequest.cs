namespace RslCompanionUploader;

/// <summary>
/// A website button's request to update the account data ("Update Data", "Sync New Account"), held
/// until the app can do it: signed in, idle, and with Raid's account readable. Kept out of
/// <c>MainForm</c> so the decision can be tested without a window or a game. See
/// <c>MainForm.RequestSiteUpdate</c> for why a launch implies this request.
/// </summary>
internal sealed class SiteUpdateRequest
{
    /// <summary>
    /// How long a request waits for Raid. It is long enough to start the game and log in, and
    /// short enough that an update nobody is still waiting for does not fire an hour later.
    /// </summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    public enum Decision { Nothing, Wait, Run, Expired }

    private DateTime? _requestedAt;

    public bool Pending => _requestedAt is not null;

    public void Request(DateTime now) => _requestedAt = now;

    public void Clear() => _requestedAt = null;

    /// <summary>
    /// What to do now. <see cref="Decision.Run"/> and <see cref="Decision.Expired"/> consume the
    /// request, so a request runs at most once however many triggers ask.
    /// </summary>
    public Decision Evaluate(DateTime now, bool signedIn, bool idle, bool accountReadable)
    {
        if (_requestedAt is not { } at) return Decision.Nothing;
        if (now - at > Window)
        {
            _requestedAt = null;
            return Decision.Expired;
        }
        if (!signedIn || !idle || !accountReadable) return Decision.Wait;

        _requestedAt = null;
        return Decision.Run;
    }
}
