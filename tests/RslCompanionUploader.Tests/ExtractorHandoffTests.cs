using System.Net;
using RslCompanionUploader.Auth;
using Xunit;

namespace RslCompanionUploader.Tests;

/// <summary>
/// What a user is told when a website launch's code cannot be redeemed — the window forwards these
/// to its notice banner, so each one has to say what to do next. No network: the exchange answers
/// from a canned handler, and every case here fails before Firebase would be called.
/// </summary>
public class ExtractorHandoffTests
{
    private sealed class Canned(params HttpResponseMessage[] replies) : HttpMessageHandler
    {
        private int _next;
        public List<string> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add($"{request.Method} {request.RequestUri} {await request.Content!.ReadAsStringAsync(ct)}");
            return replies[Math.Min(_next++, replies.Length - 1)];
        }
    }

    private static HttpResponseMessage Reply(HttpStatusCode status, string body = "{}") =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private static async Task<(HandoffException Error, Canned Handler)> Redeem(params HttpResponseMessage[] replies)
    {
        var handler = new Canned(replies);
        var http = new HttpClient(handler);
        var handoff = new ExtractorHandoff(http, new AppConfig(), new FirebaseAuthClient(http));
        var launch = ProtocolHandler.TryGetHandoff(
            ["rslcompanion-extractor://sync?code=abc&api=https%3A%2F%2Fapi.rslcompanion.com"])!;
        var error = await Assert.ThrowsAsync<HandoffException>(() => handoff.SignInAsync(launch));
        return (error, handler);
    }

    [Fact]
    public async Task The_code_is_posted_to_the_api_the_link_named()
    {
        var (_, handler) = await Redeem(Reply(HttpStatusCode.Unauthorized));
        Assert.Equal(["POST https://api.rslcompanion.com/api/extractor/handoff/exchange {\"code\":\"abc\"}"], handler.Requests);
    }

    [Fact]
    public async Task Expired_or_used_says_to_launch_again_from_the_site()
    {
        var (error, handler) = await Redeem(Reply(HttpStatusCode.Unauthorized));
        Assert.Contains("launch the extractor again from rslcompanion.com", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(handler.Requests); // single-use: retrying a 401 can only fail again
    }

    [Fact]
    public async Task Forbidden_shows_the_servers_own_message()
    {
        var (error, _) = await Redeem(Reply(HttpStatusCode.Forbidden, "{\"message\":\"Dev access is by invitation.\"}"));
        Assert.Equal("Dev access is by invitation.", error.Message);
    }

    [Fact]
    public async Task Throttled_is_retried_once_then_reported()
    {
        var (error, handler) = await Redeem(Reply(HttpStatusCode.TooManyRequests), Reply(HttpStatusCode.TooManyRequests));
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("Too many sign-in attempts", error.Message);
    }

    [Fact]
    public async Task A_throttle_that_clears_goes_on_to_the_retry_result()
    {
        var (error, handler) = await Redeem(Reply(HttpStatusCode.TooManyRequests), Reply(HttpStatusCode.Unauthorized));
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("already been used or has expired", error.Message);
    }

    [Fact]
    public async Task Unavailable_says_it_is_temporary()
    {
        var (error, _) = await Redeem(Reply(HttpStatusCode.ServiceUnavailable));
        Assert.Contains("temporarily unavailable", error.Message);
    }

    [Fact]
    public async Task A_refused_api_sends_nothing()
    {
        var handler = new Canned(Reply(HttpStatusCode.OK));
        var http = new HttpClient(handler);
        var handoff = new ExtractorHandoff(http, new AppConfig(), new FirebaseAuthClient(http));
        var launch = ProtocolHandler.TryGetHandoff(
            ["rslcompanion-extractor://sync?code=abc&api=https%3A%2F%2Fapi.rslcompanion.com.evil.example"])!;

        await Assert.ThrowsAsync<HandoffException>(() => handoff.SignInAsync(launch));
        Assert.Empty(handler.Requests);
    }
}
