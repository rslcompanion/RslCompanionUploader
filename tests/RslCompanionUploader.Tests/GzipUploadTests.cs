using System.IO.Compression;
using System.Net;
using System.Text;
using RslCompanionUploader.Api;
using RslCompanionUploader.Auth;
using Xunit;

namespace RslCompanionUploader.Tests;

/// <summary>
/// The consolidated upload goes gzip-compressed (1.48), and falls back to plain JSON against a server that
/// can't read gzip yet: one resend, then plain for the rest of the session. The server's own refusals are
/// never resent. No network: the server answers from a canned handler.
///
/// <para>The plain-upload memory is per server and static, so each test uses its own server: production is
/// never marked plain here, and only the fallback test marks dev.</para>
/// </summary>
public class GzipUploadTests
{
    private const string Payload = """{"accountId":"95604564","account":{"name":"Magikwolf"}}""";

    private sealed record Seen(string? Encoding, string Body);

    private sealed class Canned(params Func<HttpResponseMessage>[] replies) : HttpMessageHandler
    {
        private int _next;
        public List<Seen> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            byte[] raw = await request.Content!.ReadAsByteArrayAsync(ct);
            string? encoding = request.Content.Headers.ContentEncoding.FirstOrDefault();
            if (encoding == "gzip")
            {
                using var gz = new GZipStream(new MemoryStream(raw), CompressionMode.Decompress);
                using var plain = new MemoryStream();
                gz.CopyTo(plain);
                raw = plain.ToArray();
            }
            Requests.Add(new Seen(encoding, Encoding.UTF8.GetString(raw)));
            return replies[Math.Min(_next++, replies.Length - 1)]();
        }
    }

    private static HttpResponseMessage Reply(HttpStatusCode status, string body, string mediaType = "application/json") =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };

    private static RslCompanionApiClient Client(Canned handler, ApiTarget target)
    {
        var http = new HttpClient(handler);
        var session = new AuthSession
        {
            IdToken = "id",
            RefreshToken = "refresh",
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1),
            Target = target,
        };
        return new RslCompanionApiClient(http, new AppConfig(), new FirebaseAuthClient(http), session);
    }

    [Fact]
    public async Task The_payload_goes_gzip_compressed()
    {
        var handler = new Canned(() => Reply(HttpStatusCode.OK, """{"message":"Imported successfully"}"""));
        var result = await Client(handler, ApiTarget.Production).UploadConsolidatedAsync(Payload);

        Assert.True(result.Success);
        var only = Assert.Single(handler.Requests);
        Assert.Equal("gzip", only.Encoding);
        Assert.Equal(Payload, only.Body);
    }

    [Fact]
    public async Task A_server_that_cannot_read_gzip_gets_it_plain_and_keeps_getting_it_plain()
    {
        var handler = new Canned(
            () => Reply(HttpStatusCode.BadRequest, """{"title":"One or more validation errors occurred."}""", "application/problem+json"),
            () => Reply(HttpStatusCode.OK, """{"message":"Imported successfully"}"""));
        var client = Client(handler, ApiTarget.Dev);

        Assert.True((await client.UploadConsolidatedAsync(Payload)).Success);
        Assert.True((await client.UploadConsolidatedAsync(Payload)).Success);

        Assert.Equal(["gzip", null, null], handler.Requests.Select(r => r.Encoding));
        Assert.All(handler.Requests, r => Assert.Equal(Payload, r.Body));
    }

    [Fact]
    public async Task The_servers_own_refusal_is_not_resent()
    {
        var handler = new Canned(() => Reply(HttpStatusCode.BadRequest, """{"message":"That export could not be imported."}"""));
        var result = await Client(handler, ApiTarget.Production).UploadConsolidatedAsync(Payload);

        Assert.False(result.Success);
        Assert.Equal("gzip", Assert.Single(handler.Requests).Encoding);
    }
}
