using System.Text.Json;
using Id6Mail.Mcp;

namespace Id6Mail.Mcp.Tests;

public class SetupCommandTests
{
    [Fact]
    public void ParseSelection_CommaSeparated()
    {
        Assert.Equal([0, 1], SetupCommand.ParseSelection("1,2", 2));
    }

    [Fact]
    public void ParseSelection_All()
    {
        Assert.Equal([0, 1], SetupCommand.ParseSelection("all", 2));
    }

    [Fact]
    public void ParseSelection_SingleAndSpaced()
    {
        Assert.Equal([1], SetupCommand.ParseSelection(" 2 ", 2));
    }

    [Fact]
    public void ParseSelection_ChineseComma()
    {
        Assert.Equal([0, 1], SetupCommand.ParseSelection("1，2", 2));
    }

    [Fact]
    public void ParseSelection_Deduplicates()
    {
        Assert.Equal([0], SetupCommand.ParseSelection("1,1", 2));
    }

    [Fact]
    public void ParseSelection_EmptySkips()
    {
        Assert.Empty(SetupCommand.ParseSelection(null, 2));
        Assert.Empty(SetupCommand.ParseSelection("  ", 2));
    }

    [Fact]
    public void ParseSelection_OutOfRangeIgnored()
    {
        Assert.Empty(SetupCommand.ParseSelection("3", 2));
    }

    [Fact]
    public void BuildPayload_UsesServerNameAndCommand()
    {
        using var json = JsonDocument.Parse(SetupCommand.BuildPayload("/opt/homebrew/bin/id6-mail-mcp"));
        var root = json.RootElement;

        Assert.Equal("id6-mail", root.GetProperty("name").GetString());
        Assert.Equal("/opt/homebrew/bin/id6-mail-mcp", root.GetProperty("command").GetString());
    }

    [Fact]
    public void NormalizeCommand_MapsCellarToOptPrefix()
    {
        Assert.Equal(
            "/opt/homebrew/opt/id6-mail-mcp/bin/id6-mail-mcp",
            SetupCommand.NormalizeCommand("/opt/homebrew/Cellar/id6-mail-mcp/0.2.0/libexec/id6-mail-mcp"));
    }

    [Fact]
    public void NormalizeCommand_LeavesStablePathsAlone()
    {
        Assert.Equal(
            "/opt/homebrew/opt/id6-mail-mcp/bin/id6-mail-mcp",
            SetupCommand.NormalizeCommand("/opt/homebrew/opt/id6-mail-mcp/bin/id6-mail-mcp"));
        Assert.Equal(
            "/Users/me/.dotnet/tools/id6-mail-mcp",
            SetupCommand.NormalizeCommand("/Users/me/.dotnet/tools/id6-mail-mcp"));
    }
}
