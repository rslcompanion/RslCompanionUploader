using RslCompanionUploader.Api;
using Xunit;

namespace RslCompanionUploader.Tests;

/// <summary>
/// Help ▸ Server…'s stored choice, and reading a server's refusal (403) message.
/// </summary>
public class ServerPickerTests
{
    private static readonly AppConfig Config = new();

    [Fact]
    public void No_stored_choice_means_the_built_in_server() =>
        Assert.Same(ApiTarget.Production, ApiTarget.Preferred(Config, null));

    [Fact]
    public void A_stored_dev_choice_is_honoured() =>
        Assert.Same(ApiTarget.Dev, ApiTarget.Preferred(Config, "https://api-dev.rslcompanion.com"));

    [Theory]
    // An edited settings.json is held to the same allow-list as a launch link.
    [InlineData("https://evil.example")]
    [InlineData("https://api.rslcompanion.com.evil.example")]
    [InlineData("http://api-dev.rslcompanion.com")]
    [InlineData("")]
    public void A_stored_value_off_the_allow_list_falls_back_to_the_built_in_server(string stored) =>
        Assert.Same(ApiTarget.Production, ApiTarget.Preferred(Config, stored));

    [Fact]
    public void A_stored_localhost_choice_only_resolves_in_debug()
    {
        var preferred = ApiTarget.Preferred(Config, "https://localhost:7144");
#if DEBUG
        Assert.Same(ApiTarget.Local, preferred);
#else
        Assert.Same(ApiTarget.Production, preferred);
#endif
    }

    [Fact]
    public void The_feature_key_matches_RaidTools() =>
        // RaidTools: ExtractorAccessService.FeatureKey and FeatureDefaults.DefaultOff.
        Assert.Equal("extractor-dev-server", ApiTarget.DevAccessFeature);

    [Theory]
    [InlineData("{\"message\":\"Your account doesn't have access to the Extractor on the dev server.\"}",
                "Your account doesn't have access to the Extractor on the dev server.")]
    [InlineData("{\"message\":\"\"}", null)]
    [InlineData("{\"message\":42}", null)]
    [InlineData("{\"error\":\"x\"}", null)]
    [InlineData("[\"message\"]", null)]
    [InlineData("<html>Forbidden</html>", null)]
    [InlineData("", null)]
    public void A_refusal_message_is_read_only_from_a_well_formed_body(string body, string? expected) =>
        Assert.Equal(expected, RslCompanionApiClient.ServerMessage(body));

    [Fact]
    public void An_overlong_server_message_is_not_shown() =>
        Assert.Null(RslCompanionApiClient.ServerMessage("{\"message\":\"" + new string('x', 501) + "\"}"));
}
