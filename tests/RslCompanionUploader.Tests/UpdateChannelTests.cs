using System.Text.Json;
using Xunit;

namespace RslCompanionUploader.Tests;

/// <summary>
/// Dev builds are tagged <c>v1.2.0-dev.N</c> and published as GitHub pre-releases. Production
/// installs read <c>/releases/latest</c>, which never returns one; the dev channel reads the list.
/// These pin the ordering and the pick that the dev channel depends on.
/// </summary>
public class UpdateChannelTests
{
    private static ReleaseVersion V(string s)
    {
        Assert.True(ReleaseVersion.TryParse(s, out var v), s);
        return v;
    }

    [Theory]
    [InlineData("1.40.0-dev.1", "1.40.0-dev.2")]
    [InlineData("1.40.0-dev.9", "1.40.0-dev.10")]  // numerically, not as text
    [InlineData("1.40.0-dev.2", "1.40.0")]         // the release outranks its own pre-releases
    [InlineData("1.40.0", "1.41.0-dev.1")]
    [InlineData("1.39.0", "1.40.0-dev.1")]
    [InlineData("1.40.0-dev", "1.40.0-dev.1")]
    [InlineData("1.9.0", "1.10.0")]
    public void Ordering(string older, string newer)
    {
        Assert.True(V(older) < V(newer));
        Assert.True(V(newer) > V(older));
    }

    [Theory]
    [InlineData("v1.40.0", "1.40.0", false)]
    [InlineData("1.40.0-dev.1", "1.40.0-dev.1", true)]
    [InlineData("v1.40.0-dev.1+3bd9cac", "1.40.0-dev.1", true)] // informational version carries the commit
    [InlineData("1.40.0+3bd9cac", "1.40.0", false)]
    public void Parse(string text, string expected, bool prerelease)
    {
        var v = V(text);
        Assert.Equal(expected, v.ToString());
        Assert.Equal(prerelease, v.IsPrerelease);
    }

    [Theory]
    [InlineData("1.40")]
    [InlineData("1.40.0-")]
    [InlineData("latest")]
    [InlineData("")]
    public void Rejects(string text) => Assert.False(ReleaseVersion.TryParse(text, out _));

    [Fact]
    public void A_build_with_and_without_a_revision_is_the_same_version()
    {
        // The exe is stamped 1.40.0.0; the tag says 1.40.0. Those must not read as an update.
        Assert.Equal(0, V("1.40.0.0").CompareTo(V("1.40.0")));
    }

    private static JsonElement Releases(string json) => JsonDocument.Parse(json).RootElement;

    private const string List = """
        [
          { "tag_name": "v1.40.0-dev.2", "prerelease": true,  "draft": false, "html_url": "u2",
            "assets": [ { "name": "RslCompanionAccountDataExtractor-Setup-1.40.0-dev.2.exe", "browser_download_url": "https://x/setup-dev2.exe", "size": 5 },
                        { "name": "RslCompanionAccountDataExtractor-Setup-1.40.0-dev.2.exe.sha256", "browser_download_url": "https://x/setup-dev2.exe.sha256", "size": 1 },
                        { "name": "RslCompanionAccountDataExtractor-Setup.exe", "browser_download_url": "https://x/setup.exe", "size": 5 } ] },
          { "tag_name": "v1.41.0-dev.1", "prerelease": true,  "draft": true,  "html_url": "draft" },
          { "tag_name": "v1.39.0",       "prerelease": false, "draft": false, "html_url": "u0" },
          { "tag_name": "nightly",       "prerelease": true,  "draft": false, "html_url": "junk" },
          { "tag_name": "v1.40.0-dev.1", "prerelease": true,  "draft": false, "html_url": "u1" }
        ]
        """;

    [Fact]
    public void The_dev_channel_takes_the_newest_published_release_by_version()
    {
        var newest = UpdateChecker.PickNewestRelease(Releases(List))!.Value;
        Assert.Equal("v1.40.0-dev.2", newest.GetProperty("tag_name").GetString()); // the draft is skipped
    }

    [Fact]
    public void A_prod_install_on_the_dev_channel_is_offered_the_dev_build_with_its_own_installer()
    {
        var newest = UpdateChecker.PickNewestRelease(Releases(List))!.Value;
        var result = UpdateChecker.Evaluate(newest, V("1.39.0"));

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Equal("1.40.0-dev.2", result.Info!.Version.ToString());
        Assert.Equal("https://x/setup-dev2.exe", result.Info.InstallerUrl);
        Assert.Equal("https://x/setup-dev2.exe.sha256", result.Info.ChecksumUrl);
    }

    [Fact]
    public void Running_the_newest_dev_build_is_up_to_date()
    {
        var newest = UpdateChecker.PickNewestRelease(Releases(List))!.Value;
        Assert.Equal(UpdateCheckStatus.UpToDate, UpdateChecker.Evaluate(newest, V("1.40.0-dev.2")).Status);
    }

    [Fact]
    public void A_dev_build_is_offered_the_production_release_it_led_up_to()
    {
        var prod = Releases("""{ "tag_name": "v1.40.0", "html_url": "u", "assets": [] }""");
        var result = UpdateChecker.Evaluate(prod, V("1.40.0-dev.2"));
        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Equal("1.40.0", result.Info!.Version.ToString());
    }

    [Fact]
    public void A_prod_install_is_up_to_date_against_its_own_tag()
    {
        var prod = Releases("""{ "tag_name": "v1.39.0", "html_url": "u", "assets": [] }""");
        Assert.Equal(UpdateCheckStatus.UpToDate, UpdateChecker.Evaluate(prod, V("1.39.0")).Status);
    }
}
