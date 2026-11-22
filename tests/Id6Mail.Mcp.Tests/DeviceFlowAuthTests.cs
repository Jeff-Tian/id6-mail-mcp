using System.Net;
using System.Text;
using System.Text.Json;
using Id6Mail.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Id6Mail.Mcp.Tests;

public class DeviceFlowAuthTests : IDisposable
{
    private readonly string _cachePath = Path.Combine(Path.GetTempPath(), $"id6-mail-mcp-test-{Guid.NewGuid():N}", "token.json");

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new();

        public List<(string Url, string Body)> Calls { get; } = [];

        public StubHandler Enqueue(HttpStatusCode status, string body)
        {
            _responses.Enqueue(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
            return this;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add((request.RequestUri!.ToString(), request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            return _responses.Dequeue();
        }
    }

    private DeviceFlowAuth CreateAuth(
        StubHandler handler,
        out Id6Settings settings,
        out TokenCache cache,
        Action<string>? openBrowser = null)
    {
        settings = new Id6Settings("https://id6.test", _cachePath);
        cache = new TokenCache(_cachePath);
        return new DeviceFlowAuth(
            new HttpClient(handler),
            settings,
            cache,
            NullLogger<DeviceFlowAuth>.Instance,
            openBrowser ?? (_ => { }));
    }

    private static string Form(string body, string key)
    {
        var pair = body.Split('&').Select(p => p.Split('=', 2)).FirstOrDefault(p => p[0] == key);
        return pair is null ? "" : Uri.UnescapeDataString(pair[1].Replace('+', ' '));
    }

    [Fact]
    public async Task FreshCachedToken_IsReturnedWithoutHttp()
    {
        var handler = new StubHandler();
        var auth = CreateAuth(handler, out _, out var cache);
        cache.Save(new TokenState { AccessToken = "cached-at", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) });

        var token = await auth.GetAccessTokenAsync(default);

        Assert.Equal("cached-at", token);
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task ExpiredToken_IsRefreshedAndReplaced()
    {
        var handler = new StubHandler().Enqueue(HttpStatusCode.OK,
            """{"access_token":"new-at","refresh_token":"new-rt","expires_in":3600}""");
        var auth = CreateAuth(handler, out _, out var cache);
        cache.Save(new TokenState
        {
            AccessToken = "old-at",
            RefreshToken = "old-rt",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        });

        var token = await auth.GetAccessTokenAsync(default);

        Assert.Equal("new-at", token);
        var (url, body) = Assert.Single(handler.Calls);
        Assert.Equal("https://id6.test/connect/token", url);
        Assert.Equal("refresh_token", Form(body, "grant_type"));
        Assert.Equal("old-rt", Form(body, "refresh_token"));
        Assert.Equal("id6-mail-mcp", Form(body, "client_id"));
        Assert.DoesNotContain("client_secret", body);

        var saved = cache.Load();
        Assert.Equal("new-at", saved.AccessToken);
        Assert.Equal("new-rt", saved.RefreshToken);
        Assert.Null(saved.Pending);
    }

    [Fact]
    public async Task NoTokens_StartsDeviceFlowAndAsksForLogin()
    {
        var handler = new StubHandler().Enqueue(HttpStatusCode.OK,
            """
            {"device_code":"dc-1","user_code":"ABCD-EFGH","verification_uri":"https://id6.test/device",
             "verification_uri_complete":"https://id6.test/device?userCode=ABCD-EFGH","expires_in":600,"interval":5}
            """);
        var opened = new List<string>();
        var auth = CreateAuth(handler, out _, out var cache, opened.Add);

        var ex = await Assert.ThrowsAsync<LoginRequiredException>(() => auth.GetAccessTokenAsync(default));

        Assert.Equal("ABCD-EFGH", ex.Pending.UserCode);
        Assert.Equal("https://id6.test/device?userCode=ABCD-EFGH", Assert.Single(opened));
        var (url, body) = Assert.Single(handler.Calls);
        Assert.Equal("https://id6.test/connect/deviceauthorization", url);
        Assert.Equal("id6-mail-mcp", Form(body, "client_id"));
        Assert.Contains("mail.read", Form(body, "scope"));
        Assert.Equal("dc-1", cache.Load().Pending!.DeviceCode);
    }

    [Fact]
    public async Task PendingDeviceFlow_KeepsAskingForLoginWhilePending()
    {
        var handler = new StubHandler().Enqueue(HttpStatusCode.BadRequest, """{"error":"authorization_pending"}""");
        var auth = CreateAuth(handler, out _, out var cache);
        cache.Save(new TokenState
        {
            Pending = new PendingLogin
            {
                DeviceCode = "dc-1",
                UserCode = "ABCD-EFGH",
                VerificationUri = "https://id6.test/device",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            },
        });

        var ex = await Assert.ThrowsAsync<LoginRequiredException>(() => auth.GetAccessTokenAsync(default));

        Assert.Equal("ABCD-EFGH", ex.Pending.UserCode);
        var (url, body) = Assert.Single(handler.Calls);
        Assert.Equal("https://id6.test/connect/token", url);
        Assert.Equal("urn:ietf:params:oauth:grant-type:device_code", Form(body, "grant_type"));
        Assert.Equal("dc-1", Form(body, "device_code"));
    }

    [Fact]
    public async Task PendingDeviceFlow_SuccessCachesTokens()
    {
        var handler = new StubHandler().Enqueue(HttpStatusCode.OK,
            """{"access_token":"at","refresh_token":"rt","expires_in":3600,"token_type":"Bearer"}""");
        var auth = CreateAuth(handler, out _, out var cache);
        cache.Save(new TokenState
        {
            Pending = new PendingLogin
            {
                DeviceCode = "dc-1",
                UserCode = "ABCD-EFGH",
                VerificationUri = "https://id6.test/device",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            },
        });

        var token = await auth.GetAccessTokenAsync(default);

        Assert.Equal("at", token);
        var saved = cache.Load();
        Assert.Equal("at", saved.AccessToken);
        Assert.Equal("rt", saved.RefreshToken);
        Assert.Null(saved.Pending);
    }

    [Fact]
    public async Task ExpiredPendingCode_RestartsDeviceFlow()
    {
        var handler = new StubHandler().Enqueue(HttpStatusCode.OK,
            """{"device_code":"dc-2","user_code":"NEW-CODE","verification_uri":"https://id6.test/device","expires_in":600}""");
        var auth = CreateAuth(handler, out _, out var cache);
        cache.Save(new TokenState
        {
            Pending = new PendingLogin
            {
                DeviceCode = "dc-old",
                UserCode = "OLD-CODE",
                VerificationUri = "https://id6.test/device",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            },
        });

        var ex = await Assert.ThrowsAsync<LoginRequiredException>(() => auth.GetAccessTokenAsync(default));

        Assert.Equal("NEW-CODE", ex.Pending.UserCode);
        Assert.Equal("https://id6.test/connect/deviceauthorization", Assert.Single(handler.Calls).Url);
    }

    [Fact]
    public async Task TokenCache_UsesOwnerOnlyPermissions()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var cache = new TokenCache(_cachePath);
        cache.Save(new TokenState { AccessToken = "secret-at", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) });

        var mode = File.GetUnixFileMode(_cachePath);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
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
