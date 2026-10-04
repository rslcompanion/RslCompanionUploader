using Xunit;
using static RslCompanionUploader.SiteUpdateRequest;

namespace RslCompanionUploader.Tests;

/// <summary>
/// "Update Data" on the site now runs the export once the app can: signed in, idle, and with Raid's
/// account readable. It runs at most once per request, and gives up after the window.
/// </summary>
public class SiteUpdateRequestTests
{
    private static readonly DateTime T0 = new(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Without_a_request_nothing_runs()
    {
        var r = new SiteUpdateRequest();
        Assert.Equal(Decision.Nothing, r.Evaluate(T0, signedIn: true, idle: true, accountReadable: true));
    }

    [Fact]
    public void Ready_at_once_runs_at_once_and_only_once()
    {
        var r = new SiteUpdateRequest();
        r.Request(T0);
        Assert.Equal(Decision.Run, r.Evaluate(T0, true, true, true));
        // The game poll, the end of the export and an account reload all ask again afterwards.
        Assert.Equal(Decision.Nothing, r.Evaluate(T0.AddSeconds(5), true, true, true));
        Assert.False(r.Pending);
    }

    [Theory]
    [InlineData(false, true, true)]  // the session is still being switched
    [InlineData(true, false, true)]  // an export, calibration or account reload is running
    [InlineData(true, true, false)]  // Raid not started, still loading, or signed out in game
    public void Waits_until_everything_is_in_place(bool signedIn, bool idle, bool readable)
    {
        var r = new SiteUpdateRequest();
        r.Request(T0);
        Assert.Equal(Decision.Wait, r.Evaluate(T0.AddMinutes(1), signedIn, idle, readable));
        Assert.True(r.Pending);
        Assert.Equal(Decision.Run, r.Evaluate(T0.AddMinutes(2), true, true, true));
    }

    [Fact]
    public void Gives_up_after_the_window_and_says_so_once()
    {
        var r = new SiteUpdateRequest();
        r.Request(T0);
        Assert.Equal(Decision.Expired, r.Evaluate(T0 + Window + TimeSpan.FromSeconds(1), true, true, true));
        Assert.Equal(Decision.Nothing, r.Evaluate(T0 + Window + TimeSpan.FromSeconds(2), true, true, true));
    }

    [Fact]
    public void Signing_out_drops_the_request()
    {
        var r = new SiteUpdateRequest();
        r.Request(T0);
        r.Clear();
        Assert.Equal(Decision.Nothing, r.Evaluate(T0, true, true, true));
    }

    [Fact]
    public void A_second_press_restarts_the_window()
    {
        var r = new SiteUpdateRequest();
        r.Request(T0);
        r.Request(T0.AddMinutes(9));
        Assert.Equal(Decision.Wait, r.Evaluate(T0.AddMinutes(15), true, true, false));
    }

    [Fact]
    public void The_run_reports_which_account_the_site_asked_for()
    {
        var r = new SiteUpdateRequest();
        r.Request(T0, accountId: 111);
        Assert.Equal(Decision.Wait, r.Evaluate(T0, true, true, false, out var whileWaiting));
        Assert.Null(whileWaiting); // only a run hands it out
        Assert.Equal(Decision.Run, r.Evaluate(T0, true, true, true, out var asked));
        Assert.Equal(111, asked);
    }

    [Fact]
    public void A_newer_press_replaces_the_account_too()
    {
        var r = new SiteUpdateRequest();
        r.Request(T0, accountId: 111);
        r.Request(T0, accountId: null); // e.g. "Sync New Account", which names none
        Assert.Equal(Decision.Run, r.Evaluate(T0, true, true, true, out var asked));
        Assert.Null(asked);
    }

    [Fact]
    public void Asking_for_another_account_informs_in_one_sentence() =>
        // Informs what is about to happen and stops there; what to do next is the user's call.
        Assert.Equal(
            "You asked to update RslCompanion, but Raid is signed in to Magikwolf, so Magikwolf is the account being synced.",
            MismatchNotice(111, "RslCompanion", 95604564, "Magikwolf"));

    [Fact]
    public void An_account_with_no_known_name_is_named_by_its_id() =>
        Assert.Contains("account #111", MismatchNotice(111, null, 95604564, "Magikwolf"));

    [Theory]
    [InlineData(95604564)] // the one being played
    [InlineData(null)]     // the site didn't say (every site build so far)
    public void Nothing_to_say_when_it_matches_or_was_not_named(int? asked) =>
        Assert.Null(MismatchNotice(asked, "Magikwolf", 95604564, "Magikwolf"));
}
