using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Id6Mail.Mcp;

public static class SetupCommand
{
    public const string ServerName = "id6-mail";

    public static int Run(string[] args)
    {
        var command = ReadCommand(args) ?? DefaultCommand();
        if (string.IsNullOrWhiteSpace(command))
        {
            Console.Error.WriteLine("Cannot determine the id6-mail-mcp path; pass --command <path>.");
            return 1;
        }

        var clients = DetectClients();
        if (clients.Count == 0)
        {
            Console.Error.WriteLine("No supported MCP client found (Trae CN or VS Code).");
            return 1;
        }

        // Homebrew runs us with a captured stdout and an empty stdin; only trust a real terminal.
        if (Console.IsInputRedirected)
        {
            Console.Error.WriteLine("Non-interactive session; run `id6-mail-mcp setup` from a terminal to choose clients.");
            return 0;
        }

        var output = OpenPromptWriter();
        JoinForegroundProcessGroup();
        try
        {
            output.WriteLine("Add the id6-mail MCP server to:");
            for (var i = 0; i < clients.Count; i++)
                output.WriteLine($"  {i + 1}) {clients[i].Name}");
            output.Write("Select clients (comma-separated, or 'all'; Enter to skip): ");

            var selected = ParseSelection(Console.ReadLine(), clients.Count);
            if (selected.Count == 0)
            {
                output.WriteLine("Skipped.");
                return 0;
            }

            var payload = BuildPayload(command);
            var failed = false;
            foreach (var index in selected)
            {
                var client = clients[index];
                output.WriteLine($"Configuring {client.Name}...");
                try
                {
                    var startInfo = new ProcessStartInfo(client.Cli) { UseShellExecute = false };
                    startInfo.ArgumentList.Add("--add-mcp");
                    startInfo.ArgumentList.Add(payload);
                    using var process = Process.Start(startInfo);
                    if (process is null)
                    {
                        failed = true;
                        output.WriteLine($"Failed to start {client.Name}.");
                        continue;
                    }

                    process.WaitForExit();
                    if (process.ExitCode != 0)
                    {
                        failed = true;
                        output.WriteLine($"{client.Name} exited with code {process.ExitCode}.");
                    }
                }
                catch (Exception ex)
                {
                    failed = true;
                    output.WriteLine($"Could not configure {client.Name}: {ex.Message}");
                }
            }

            return failed ? 1 : 0;
        }
        catch (IOException)
        {
            Console.Error.WriteLine("Terminal input is unavailable; run `id6-mail-mcp setup` from a terminal.");
            return 0;
        }
        finally
        {
            if (output != Console.Out)
                output.Dispose();
        }
    }

    // Homebrew runs install steps in a background process group, where reading the
    // terminal fails with EIO; joining the foreground group makes it readable again.
    private static void JoinForegroundProcessGroup()
    {
        if (OperatingSystem.IsWindows())
            return;

        var foreground = tcgetpgrp(0);
        if (foreground > 0)
            setpgid(0, foreground);
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int tcgetpgrp(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int setpgid(int pid, int pgid);

    // Writing to /dev/tty keeps the prompt visible when brew captures stdout.
    private static TextWriter OpenPromptWriter()
    {
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                return new StreamWriter(new FileStream("/dev/tty", FileMode.Open, FileAccess.Write)) { AutoFlush = true };
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return Console.Out;
    }

    public static IReadOnlyList<int> ParseSelection(string? input, int count)
    {
        if (string.IsNullOrWhiteSpace(input))
            return [];

        var text = input.Trim();
        if (text.Equals("all", StringComparison.OrdinalIgnoreCase))
            return Enumerable.Range(0, count).ToArray();

        var result = new List<int>();
        foreach (var part in text.Split([',', '，', ' ', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(part, out var number) &&
                number >= 1 &&
                number <= count &&
                !result.Contains(number - 1))
            {
                result.Add(number - 1);
            }
        }

        return result;
    }

    public static string BuildPayload(string command) =>
        JsonSerializer.Serialize(new { name = ServerName, command });

    public static string NormalizeCommand(string path)
    {
        var marker = $"{Path.DirectorySeparatorChar}Cellar{Path.DirectorySeparatorChar}id6-mail-mcp{Path.DirectorySeparatorChar}";
        var index = path.IndexOf(marker, StringComparison.Ordinal);
        if (index <= 0)
            return path;

        return Path.Combine(path[..index], "opt", "id6-mail-mcp", "bin", "id6-mail-mcp");
    }

    private static string? DefaultCommand()
    {
        var path = Environment.ProcessPath;
        return string.IsNullOrEmpty(path) ? null : NormalizeCommand(path);
    }

    private static string? ReadCommand(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--command")
                return args[i + 1];
        }

        return null;
    }

    private static List<Client> DetectClients()
    {
        var clients = new List<Client>();

        if (FindOnPath("code") is { } code)
            clients.Add(new Client("Visual Studio Code", code));

        var trae = FindOnPath("trae-cn") ?? Existing("/Applications/Trae CN.app/Contents/Resources/app/bin/trae-cn");
        if (trae is not null)
            clients.Add(new Client("Trae CN", trae));

        return clients;
    }

    private static string? FindOnPath(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
            return null;

        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, name);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static string? Existing(string path) => File.Exists(path) ? path : null;

    private sealed record Client(string Name, string Cli);
}
