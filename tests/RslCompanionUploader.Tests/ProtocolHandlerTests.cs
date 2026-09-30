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
}
