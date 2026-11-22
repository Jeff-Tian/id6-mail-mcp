using System.Text.Json;

namespace Id6Mail.Mcp;

public sealed class PendingLogin
{
    public string DeviceCode { get; set; } = "";
    public string UserCode { get; set; } = "";
    public string VerificationUri { get; set; } = "";
    public string? VerificationUriComplete { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public int IntervalSeconds { get; set; } = 5;
}

public sealed class TokenState
{
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public PendingLogin? Pending { get; set; }
}

/// <summary>
/// Caches the device-flow tokens under the user's home directory with 0600 permissions.
/// The token never leaves this process; only id6 API responses are exposed to the agent.
/// </summary>
public sealed class TokenCache(string path)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public TokenState Load()
    {
        try
        {
            if (!File.Exists(path))
            {
                return new TokenState();
            }

            return JsonSerializer.Deserialize<TokenState>(File.ReadAllText(path), JsonOptions) ?? new TokenState();
        }
        catch (Exception) when (File.Exists(path))
        {
            // A corrupt cache must not break the MCP: start over and re-authenticate.
            return new TokenState();
        }
    }

    public void Save(TokenState state)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(state, JsonOptions));

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}

public sealed class LoginRequiredException(PendingLogin pending) : Exception("id6 login required")
{
    public PendingLogin Pending { get; } = pending;
}
