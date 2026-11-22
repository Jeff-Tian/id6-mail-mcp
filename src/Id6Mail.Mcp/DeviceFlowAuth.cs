using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Id6Mail.Mcp;

/// <summary>
/// Public-client device flow against id6. No client secret is used: the first call starts a
/// device authorization and surfaces the user code; the next call after the user finishes in the
/// browser exchanges the device code (or refreshes) and caches the tokens locally.
/// </summary>
public sealed class DeviceFlowAuth(
    HttpClient http,
    Id6Settings settings,
    TokenCache cache,
    ILogger<DeviceFlowAuth> logger,
    Action<string>? openBrowser = null)
{
    private static readonly TimeSpan RefreshWindow = TimeSpan.FromSeconds(60);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Action<string> _openBrowser = openBrowser ?? OpenBrowser;

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = cache.Load();

            if (!string.IsNullOrEmpty(state.AccessToken) && state.ExpiresAt > DateTimeOffset.UtcNow.Add(RefreshWindow))
            {
                return state.AccessToken;
            }

            if (!string.IsNullOrEmpty(state.RefreshToken))
            {
                var refreshed = await RefreshAsync(state.RefreshToken, cancellationToken);
                if (refreshed is not null)
                {
                    cache.Save(refreshed);
                    return refreshed.AccessToken!;
                }

                logger.LogWarning("id6 refresh token rejected; a new device login is required");
                state = new TokenState();
            }

            if (state.Pending is { } pending && pending.ExpiresAt > DateTimeOffset.UtcNow)
            {
                var (tokens, stillPending) = await PollAsync(pending, cancellationToken);
                if (tokens is not null)
                {
                    cache.Save(tokens);
                    return tokens.AccessToken!;
                }

                if (stillPending is not null)
                {
                    cache.Save(new TokenState { Pending = stillPending });
                    throw new LoginRequiredException(stillPending);
                }
            }

            var started = await StartAsync(cancellationToken);
            cache.Save(new TokenState { Pending = started });
            _openBrowser(started.VerificationUriComplete ?? started.VerificationUri);
            throw new LoginRequiredException(started);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<TokenState?> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var response = await http.PostAsync(
            $"{settings.BaseUrl}/connect/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"] = settings.ClientId,
            }),
            cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            logger.LogWarning("id6 token refresh failed: status={Status} body={Body}", (int)response.StatusCode, body);
            return null;
        }

        using var json = JsonDocument.Parse(body);
        var accessToken = GetString(json.RootElement, "access_token");
        if (string.IsNullOrEmpty(accessToken))
        {
            return null;
        }

        return new TokenState
        {
            AccessToken = accessToken,
            // Duende may omit refresh_token when rotation is disabled; keep the old one.
            RefreshToken = GetString(json.RootElement, "refresh_token") ?? refreshToken,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(GetInt(json.RootElement, "expires_in", 3600)),
        };
    }

    private async Task<PendingLogin> StartAsync(CancellationToken cancellationToken)
    {
        var response = await http.PostAsync(
            $"{settings.BaseUrl}/connect/deviceauthorization",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = settings.ClientId,
                ["scope"] = settings.Scope,
            }),
            cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new InvalidOperationException(
                $"id6 device authorization failed ({(int)response.StatusCode}): {Truncate(body, 500)}");
        }

        using var json = JsonDocument.Parse(body);
        return new PendingLogin
        {
            DeviceCode = GetString(json.RootElement, "device_code") ?? throw InvalidResponse("device_code"),
            UserCode = GetString(json.RootElement, "user_code") ?? throw InvalidResponse("user_code"),
            VerificationUri = GetString(json.RootElement, "verification_uri") ?? $"{settings.BaseUrl}/device",
            VerificationUriComplete = GetString(json.RootElement, "verification_uri_complete"),
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(GetInt(json.RootElement, "expires_in", 600)),
            IntervalSeconds = GetInt(json.RootElement, "interval", 5),
        };
    }

    private async Task<(TokenState? Tokens, PendingLogin? Pending)> PollAsync(
        PendingLogin pending,
        CancellationToken cancellationToken)
    {
        var response = await http.PostAsync(
            $"{settings.BaseUrl}/connect/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                ["device_code"] = pending.DeviceCode,
                ["client_id"] = settings.ClientId,
            }),
            cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var json = JsonDocument.Parse(body);

        if (response.StatusCode == HttpStatusCode.OK)
        {
            var accessToken = GetString(json.RootElement, "access_token")
                ?? throw InvalidResponse("access_token");
            return (new TokenState
            {
                AccessToken = accessToken,
                RefreshToken = GetString(json.RootElement, "refresh_token"),
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(GetInt(json.RootElement, "expires_in", 3600)),
            }, null);
        }

        var error = GetString(json.RootElement, "error");
        switch (error)
        {
            case "authorization_pending":
                return (null, pending);
            case "slow_down":
                pending.IntervalSeconds += 5;
                return (null, pending);
            default:
                // expired_token / access_denied / anything else: forget the code and start over.
                logger.LogInformation("id6 device flow ended with {Error}", error);
                return (null, null);
        }
    }

    private static void OpenBrowser(string url)
    {
        try
        {
            var (fileName, arguments) = OperatingSystem.IsMacOS()
                ? ("open", url)
                : OperatingSystem.IsWindows()
                    ? ("cmd", $"/c start \"\" \"{url}\"")
                    : ("xdg-open", url);
            Process.Start(new ProcessStartInfo(fileName, arguments) { UseShellExecute = false })?.Dispose();
        }
        catch (Exception)
        {
            // The tool result already contains the URL; opening the browser is best effort.
        }
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int GetInt(JsonElement element, string property, int fallback) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt32(out var result) ? result : fallback;

    private static InvalidOperationException InvalidResponse(string property) =>
        new($"id6 response is missing {property}");

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
