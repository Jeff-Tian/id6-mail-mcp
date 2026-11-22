using Id6Mail.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

var builder = Host.CreateApplicationBuilder(args);

// stdout carries the MCP JSON-RPC stream; any stray log line on stdout corrupts the session.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

var settings = new Id6Settings();
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(new TokenCache(settings.CachePath));
builder.Services.AddSingleton(_ => new HttpClient { Timeout = TimeSpan.FromSeconds(60) });
builder.Services.AddSingleton(sp => new DeviceFlowAuth(
    sp.GetRequiredService<HttpClient>(),
    sp.GetRequiredService<Id6Settings>(),
    sp.GetRequiredService<TokenCache>(),
    sp.GetRequiredService<ILogger<DeviceFlowAuth>>()));
builder.Services.AddSingleton<Id6MailClient>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
