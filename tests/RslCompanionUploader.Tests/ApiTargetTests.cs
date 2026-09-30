using Xunit;

namespace RslCompanionUploader.Tests;

/// <summary>
/// The <c>api</c> allow-list. Any web page can open <c>rslcompanion-extractor://</c>, so this is
/// the only thing standing between a hostile link and the user's account export.
/// </summary>
public class ApiTargetTests
{
    [Theory]
    [InlineData("https://api.rslcompanion.com")]
    [InlineData("https://api.rslcompanion.com/")] // one trailing slash is normalised away
    public void Prod_is_allowed(string api) =>
        Assert.Same(ApiTarget.Production, ApiTarget.TryResolve(api));

    [Theory]
    [InlineData("https://api-dev.rslcompanion.com")]
    [InlineData("https://api-dev.rslcompanion.com/")]
    public void Dev_is_allowed(string api) =>
        Assert.Same(ApiTarget.Dev, ApiTarget.TryResolve(api));

    [Fact]
    public void Dev_carries_its_own_firebase_project_and_site()
    {
        var dev = ApiTarget.TryResolve("https://api-dev.rslcompanion.com")!;
        Assert.False(dev.IsProduction);
        Assert.Equal("https://dev.rslcompanion.com", dev.FrontendUrl);
        Assert.NotEqual(ApiTarget.Production.FirebaseApiKey, dev.FirebaseApiKey);
        Assert.Equal("api-dev.rslcompanion.com", dev.ApiHost);
    }

    [Theory]
    // Suffix / prefix / lookalike hosts — what a string prefix or contains check would let through.
    [InlineData("https://api.rslcompanion.com.evil.example")]
    [InlineData("https://api.rslcompanion.com.evil.example/")]
    [InlineData("https://evil.example/api.rslcompanion.com")]
    [InlineData("https://evilapi.rslcompanion.com")]
    [InlineData("https://xapi-dev.rslcompanion.com")]
    [InlineData("https://rslcompanion.com")]
    [InlineData("https://api.rslcompanion.co")]
    [InlineData("https://api.rslcompanion.com.")] // trailing-dot FQDN: not the origin the site sends
    // Wrong scheme or port.
    [InlineData("http://api.rslcompanion.com")]
    [InlineData("http://api-dev.rslcompanion.com")]
    [InlineData("https://api.rslcompanion.com:8443")]
    [InlineData("https://api.rslcompanion.com:443")] // not the form the site sends; refused, not guessed at
    [InlineData("ftp://api.rslcompanion.com")]
    [InlineData("rslcompanion-extractor://api.rslcompanion.com")]
    // User-info tricks: the real host is after the '@'.
    [InlineData("https://api.rslcompanion.com@evil.example")]
    [InlineData("https://api.rslcompanion.com:443@evil.example")]
    [InlineData("https://user:pass@api.rslcompanion.com")]
    [InlineData("https://evil.example\\@api.rslcompanion.com")]
    [InlineData("https://evil.example\\.api.rslcompanion.com")]
    // Anything past the origin.
    [InlineData("https://api.rslcompanion.com/api")]
    [InlineData("https://api.rslcompanion.com//")]
    [InlineData("https://api.rslcompanion.com/..")]
    [InlineData("https://api.rslcompanion.com/%2e%2e/evil")]
    [InlineData("https://api.rslcompanion.com?x=1")]
    [InlineData("https://api.rslcompanion.com#frag")]
    [InlineData("https://api.rslcompanion.com\\")]
    // Case and whitespace: never what the site sends, and not worth canonicalising.
    [InlineData("https://API.rslcompanion.com")]
    [InlineData("HTTPS://api.rslcompanion.com")]
    [InlineData(" https://api.rslcompanion.com")]
    [InlineData("https://api.rslcompanion.com ")]
    [InlineData("https://api.rslcompanion.com\t")]
    // Not a URL at all.
    [InlineData("api.rslcompanion.com")]
    [InlineData("//api.rslcompanion.com")]
    [InlineData("javascript:alert(1)")]
    [InlineData("")]
    [InlineData("   ")]
    public void Anything_else_is_refused(string api) =>
        Assert.Null(ApiTarget.TryResolve(api));

    [Fact]
    public void Null_is_not_a_target() => Assert.Null(ApiTarget.TryResolve(null));

    [Fact]
    public void Localhost_is_allowed_only_in_debug_builds()
    {
        var resolved = ApiTarget.TryResolve("https://localhost:7144");
#if DEBUG
        Assert.Same(ApiTarget.Local, resolved);
        Assert.Equal(ApiTarget.Production.FirebaseApiKey, resolved!.FirebaseApiKey);
#else
        Assert.Null(resolved);
#endif
        // Never another port, never plain http, never an IP spelling — even in Debug.
        Assert.Null(ApiTarget.TryResolve("https://localhost:7145"));
        Assert.Null(ApiTarget.TryResolve("http://localhost:7144"));
        Assert.Null(ApiTarget.TryResolve("https://localhost"));
        Assert.Null(ApiTarget.TryResolve("https://127.0.0.1:7144"));
    }

    [Fact]
    public void Allow_list_is_exactly_prod_and_dev_plus_localhost_in_debug()
    {
#if DEBUG
        Assert.Equal([ApiTarget.Production, ApiTarget.Dev, ApiTarget.Local], ApiTarget.Allowed);
#else
        Assert.Equal([ApiTarget.Production, ApiTarget.Dev], ApiTarget.Allowed);
#endif
    }

    [Fact]
    public void Default_config_is_prod() =>
        Assert.Same(ApiTarget.Production, ApiTarget.BuiltIn(new AppConfig()));
}
