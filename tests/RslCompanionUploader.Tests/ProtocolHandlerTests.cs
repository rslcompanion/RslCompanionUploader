using Xunit;

namespace RslCompanionUploader.Tests;

/// <summary>
/// Parsing the <c>rslcompanion-extractor://sync?code=…[&amp;api=…]</c> launch, and deciding which
/// environment it is redeemed at.
/// </summary>
public class ProtocolHandlerTests
{
    private static readonly AppConfig Config = new();

    private static HandoffLaunch? Parse(string uri) => ProtocolHandler.TryGetHandoff([uri]);

    [Fact]
    public void Without_api_the_built_in_prod_api_is_used()
    {
        var launch = Parse("rslcompanion-extractor://sync?code=abc123")!;
        Assert.Equal("abc123", launch.Code);
        Assert.Null(launch.Api);
        Assert.Same(ApiTarget.Production, launch.ResolveTarget(Config));
    }

    [Theory]
    [InlineData("rslcompanion-extractor://sync?code=abc123&api=https%3A%2F%2Fapi-dev.rslcompanion.com")]
    [InlineData("rslcompanion-extractor://sync?code=abc123&api=https://api-dev.rslcompanion.com")] // unencoded
    [InlineData("rslcompanion-extractor://sync?api=https%3A%2F%2Fapi-dev.rslcompanion.com&code=abc123")] // order
    [InlineData("rslcompanion-extractor://sync?code=abc123&api=https%3A%2F%2Fapi-dev.rslcompanion.com%2F")]
    public void Dev_api_is_read_and_resolved(string uri)
    {
        var launch = Parse(uri)!;
        Assert.Equal("abc123", launch.Code);
        Assert.Same(ApiTarget.Dev, launch.ResolveTarget(Config));
    }

    [Theory]
    [InlineData("rslcompanion-extractor://sync?code=abc123&api=https%3A%2F%2Fapi.rslcompanion.com")]
    [InlineData("rslcompanion-extractor://sync?code=abc123&api=https://api.rslcompanion.com")]
    public void Prod_api_resolves_to_prod(string uri) =>
        Assert.Same(ApiTarget.Production, Parse(uri)!.ResolveTarget(Config));

    [Theory]
    [InlineData("https%3A%2F%2Fapi.rslcompanion.com.evil.example")]
    [InlineData("https%3A%2F%2Fapi.rslcompanion.com%40evil.example")]
    [InlineData("https://api.rslcompanion.com@evil.example")]
    [InlineData("http%3A%2F%2Fapi.rslcompanion.com")]
    [InlineData("https%3A%2F%2Fapi.rslcompanion.com%2Fapi")]
    [InlineData("https%3A%2F%2Fevil.example")]
    [InlineData("")] // present but empty is not absent
    public void A_foreign_api_refuses_the_launch_instead_of_falling_back(string api)
    {
        var launch = Parse($"rslcompanion-extractor://sync?code=abc123&api={api}")!;
        Assert.NotNull(launch.Api);
        Assert.Null(launch.ResolveTarget(Config));
        Assert.Contains("nothing was sent", launch.RefusalMessage);
    }

    [Fact]
    public void An_encoded_ampersand_cannot_smuggle_a_second_parameter()
    {
        // The '&' is inside the api value, so it is one parameter whose value is not an origin.
        var launch = Parse("rslcompanion-extractor://sync?code=abc123&api=https%3A%2F%2Fapi.rslcompanion.com%26api%3Dhttps%3A%2F%2Fevil.example")!;
        Assert.Null(launch.ResolveTarget(Config));
    }

    [Theory]
    [InlineData("rslcompanion-extractor://sync?code=abc&api=https%3A%2F%2Fapi.rslcompanion.com&api=https%3A%2F%2Fevil.example")]
    [InlineData("rslcompanion-extractor://sync?code=abc&api=https%3A%2F%2Fevil.example&api=https%3A%2F%2Fapi.rslcompanion.com")]
    [InlineData("rslcompanion-extractor://sync?code=abc&api=https%3A%2F%2Fapi.rslcompanion.com&api=https%3A%2F%2Fapi.rslcompanion.com")]
    public void A_repeated_api_is_refused(string uri)
    {
        var launch = Parse(uri)!;
        Assert.True(launch.ApiAmbiguous);
        Assert.Null(launch.ResolveTarget(Config));
    }

    [Fact]
    public void Unknown_parameters_are_ignored() =>
        Assert.Same(ApiTarget.Dev,
            Parse("rslcompanion-extractor://sync?utm=x&code=abc&v=2&api=https%3A%2F%2Fapi-dev.rslcompanion.com&z")!
                .ResolveTarget(Config));

    [Theory]
    [InlineData("rslcompanion-extractor://ping")]
    [InlineData("rslcompanion-extractor://ping?api=https%3A%2F%2Fapi-dev.rslcompanion.com")]
    [InlineData("rslcompanion-extractor://sync")]
    [InlineData("rslcompanion-extractor://sync?code=")]
    [InlineData("rslcompanion-extractor://sync?code=%20&api=https%3A%2F%2Fapi-dev.rslcompanion.com")]
    [InlineData("rslcompanion-extractor://sync?rt=legacy-refresh-token")]
    public void No_code_means_no_sign_in(string uri) => Assert.Null(Parse(uri));

    [Fact]
    public void Non_protocol_args_are_ignored()
    {
        Assert.Null(ProtocolHandler.TryGetHandoff([]));
        Assert.Null(ProtocolHandler.TryGetHandoff(["--something", "https://rslcompanion.com/?code=abc"]));
        Assert.Equal("abc", ProtocolHandler.TryGetHandoff(["--x", "RSLCOMPANION-EXTRACTOR://sync?code=abc"])!.Code);
    }

    [Fact]
    public void The_first_code_wins_as_it_always_has() =>
        Assert.Equal("first", Parse("rslcompanion-extractor://sync?code=first&code=second")!.Code);

    [Theory]
    [InlineData("rslcompanion-extractor://sync?code=abc&account=95604564", 95604564)]
    [InlineData("rslcompanion-extractor://sync?code=abc&api=https%3A%2F%2Fapi.rslcompanion.com&account=95604564", 95604564)]
    [InlineData("rslcompanion-extractor://sync?account=95604564&code=abc", 95604564)]
    [InlineData("rslcompanion-extractor://sync?code=abc&account=junk&account=7", 7)] // first *valid* wins
    public void The_card_that_asked_is_read_from_account(string uri, int expected) =>
        Assert.Equal(expected, Parse(uri)!.AccountId);

    [Theory]
    [InlineData("rslcompanion-extractor://sync?code=abc")]                 // every site build so far
    [InlineData("rslcompanion-extractor://sync?code=abc&account=")]
    [InlineData("rslcompanion-extractor://sync?code=abc&account=-5")]
    [InlineData("rslcompanion-extractor://sync?code=abc&account=0")]
    [InlineData("rslcompanion-extractor://sync?code=abc&account=1e3")]
    [InlineData("rslcompanion-extractor://sync?code=abc&account=99999999999")]
    public void A_missing_or_unusable_account_is_simply_absent(string uri)
    {
        var launch = Parse(uri)!;
        Assert.Equal("abc", launch.Code); // never costs the sign-in
        Assert.Null(launch.AccountId);
    }

    // Only a site button that uploads asks for an upload. /connect-extractor sends a bare sign-in link once
    // the user is signed in on the site, and that must never start one.
    [Theory]
    [InlineData("rslcompanion-extractor://sync?code=abc&intent=update", true)]
    [InlineData("rslcompanion-extractor://sync?code=abc&intent=UPDATE", true)]
    [InlineData("rslcompanion-extractor://sync?code=abc&account=95604564", true)] // "Update Data" before intent existed
    [InlineData("rslcompanion-extractor://sync?code=abc&account=95604564&intent=update", true)]
    [InlineData("rslcompanion-extractor://sync?code=abc", false)]                  // /connect-extractor
    [InlineData("rslcompanion-extractor://sync?code=abc&api=https%3A%2F%2Fapi.rslcompanion.com", false)]
    [InlineData("rslcompanion-extractor://sync?code=abc&intent=signin", false)]
    [InlineData("rslcompanion-extractor://sync?code=abc&intent=", false)]
    [InlineData("rslcompanion-extractor://sync?code=abc&account=junk", false)]
    public void Only_an_upload_button_requests_an_upload(string uri, bool expected) =>
        Assert.Equal(expected, Parse(uri)!.RequestsUpdate);

    [Fact]
    public void An_account_never_affects_where_the_code_is_redeemed() =>
        Assert.Same(ApiTarget.Production,
            Parse("rslcompanion-extractor://sync?code=abc&account=95604564")!.ResolveTarget(Config));

    [Fact]
    public void An_account_without_a_code_is_still_not_a_sign_in() =>
        Assert.Null(Parse("rslcompanion-extractor://sync?account=95604564"));
}
