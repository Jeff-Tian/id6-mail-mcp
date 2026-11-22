public sealed class Id6Settings
{
    public const string DefaultBaseUrl = "https://id6.fly.dev";

    public Id6Settings(string? baseUrl = null, string? cachePath = null)
    {
        BaseUrl = (baseUrl ?? Environment.GetEnvironmentVariable("ID6_BASE_URL") ?? DefaultBaseUrl).TrimEnd('/');
        CachePath = cachePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".id6-mail-mcp",
            "token.json");
    }

    public string BaseUrl { get; }

    public string CachePath { get; }

    public string ClientId => "id6-mail-mcp";

    public string Scope => "openid profile email mail.read IdentityServerApi";
}
