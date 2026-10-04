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
}
