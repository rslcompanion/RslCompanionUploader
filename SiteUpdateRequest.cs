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
    private int? _accountId;

    public bool Pending => _requestedAt is not null;

    /// <summary>
    /// Records a request. <paramref name="accountId"/> is the in-game id of the card that asked, when
    /// the site said (null otherwise); a later request replaces an earlier one whole.
    /// </summary>
    public void Request(DateTime now, int? accountId = null)
    {
        _requestedAt = now;
        _accountId = accountId;
    }

    public void Clear()
    {
        _requestedAt = null;
        _accountId = null;
    }

    /// <summary>
    /// What to do now. <see cref="Decision.Run"/> and <see cref="Decision.Expired"/> consume the
    /// request, so a request runs at most once however many triggers ask.
    /// </summary>
    public Decision Evaluate(DateTime now, bool signedIn, bool idle, bool accountReadable) =>
        Evaluate(now, signedIn, idle, accountReadable, out _);

    /// <inheritdoc cref="Evaluate(DateTime, bool, bool, bool)"/>
    /// <param name="requestedAccountId">On <see cref="Decision.Run"/>, the account the site asked for, if it said.</param>
    public Decision Evaluate(DateTime now, bool signedIn, bool idle, bool accountReadable, out int? requestedAccountId)
    {
        requestedAccountId = null;
        if (_requestedAt is not { } at) return Decision.Nothing;
        if (now - at > Window)
        {
            Clear();
            return Decision.Expired;
        }
        if (!signedIn || !idle || !accountReadable) return Decision.Wait;

        requestedAccountId = _accountId;
        Clear();
        return Decision.Run;
    }

    /// <summary>
    /// The sentence to show when the site asked for one account and Raid is on another, or null
    /// when there is nothing to say: the site didn't name one, or it named the one being played.
    /// The update still runs. The app can only read the running game, and blocking would leave the
    /// user with nothing synced and the same switch to make anyway.
    /// </summary>
    public static string? MismatchNotice(int? requestedId, string? requestedName, int liveId, string liveName)
    {
        if (requestedId is not int asked || asked == liveId) return null;
        var wanted = string.IsNullOrWhiteSpace(requestedName) ? $"account #{asked}" : requestedName;
        return $"You asked to update {wanted}, but Raid is signed in to {liveName}, so {liveName} is the "
             + $"account being synced. To update {wanted}, switch to it in Raid and press Update user data.";
    }
}
