using System.Net;
using System.Text;
using Id6Mail.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Id6Mail.Mcp.Tests;

public class Id6MailClientTests : IDisposable
{
    private readonly string _cachePath = Path.Combine(Path.GetTempPath(), $"id6-mail-mcp-client-{Guid.NewGuid():N}", "token.json");

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public StubHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private Id6MailClient CreateClient(StubHandler handler)
    {
        var settings = new Id6Settings("https://id6.test", _cachePath);
        var cache = new TokenCache(_cachePath);
        cache.Save(new TokenState { AccessToken = "cached-at", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) });
        var http = new HttpClient(handler);
        var auth = new DeviceFlowAuth(http, settings, cache, NullLogger<DeviceFlowAuth>.Instance);
        return new Id6MailClient(http, auth, settings);
    }

    [Fact]
    public async Task Search_SendsBearerAndClampedTop()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"messages":[]}""");
        var client = CreateClient(handler);

        var body = await client.SearchAsync("repo notes", 500, default);

        Assert.Equal("""{"messages":[]}""", body);
        Assert.Equal("https://id6.test/api/v1/agent/mail/search?q=repo%20notes&top=50", handler.LastRequest!.RequestUri!.OriginalString);
        Assert.Equal("Bearer", handler.LastRequest.Headers.Authorization!.Scheme);
        Assert.Equal("cached-at", handler.LastRequest.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task Get_EscapesMessageId()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"id":"m/1"}""");
        var client = CreateClient(handler);

        await client.GetAsync("m/1", default);

        Assert.Equal("https://id6.test/api/v1/agent/mail/m%2F1", handler.LastRequest!.RequestUri!.OriginalString);
    }

    [Fact]
    public async Task Conflict_IsReportedAsOutlookNotConnected()
    {
        var handler = new StubHandler(HttpStatusCode.Conflict, """{"error":"mail_not_connected"}""");
        var client = CreateClient(handler);

        var body = await client.SearchAsync("repo", 25, default);

        Assert.Contains("outlook_not_connected", body);
        Assert.Contains("https://id6.test/outlook", body);
        Assert.Contains("id6 user", body);
    }

    [Fact]
    public async Task OtherErrors_ThrowWithStatus()
    {
        var handler = new StubHandler(HttpStatusCode.Forbidden, """{"error":"insufficient_scope"}""");
        var client = CreateClient(handler);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.SearchAsync("repo", 25, default));

        Assert.Contains("403", ex.Message);
    }

    public void Dispose()
    {
        var directory = Path.GetDirectoryName(_cachePath)!;
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
