using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SolidWorksMcp.Core;

namespace SolidWorksMcp.Server.Hosting;

/// <summary>Everything the host needs; shared by Program and the catalog test.</summary>
public static class ServerSetup
{
    public static string Version =>
        typeof(ServerSetup).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public static void Configure(IHostApplicationBuilder builder, bool fileLog = true)
    {
        var options = SwOptions.FromEnvironment();
        var level = Enum.TryParse<LogLevel>(Environment.GetEnvironmentVariable("SWMCP_LOG_LEVEL"), true, out var l) ? l : LogLevel.Information;

        builder.Logging.ClearProviders();
        // stderr only (stdout is the JSON-RPC channel); warnings and above, so client logs stay quiet.
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.AddFilter<Microsoft.Extensions.Logging.Console.ConsoleLoggerProvider>(null, LogLevel.Warning);
        if (fileLog)
            builder.Logging.AddProvider(new FileLoggerProvider(Path.Combine(options.DataDir, "logs"), level));
        builder.Logging.AddFilter("ModelContextProtocol", LogLevel.Warning); // the SDK logs every request at Information
        builder.Logging.SetMinimumLevel(LogLevel.Trace);

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(sp => new SwSession(sp.GetRequiredService<SwOptions>(), sp.GetRequiredService<ILoggerFactory>()));

        var sets = Toolsets.Selected();
        bool readOnly = Toolsets.ReadOnlyMode;
        builder.Services
            .AddMcpServer(o =>
            {
                o.ServerInfo = new Implementation { Name = "solidworks-mcp", Title = "SOLIDWORKS", Version = Version };
                o.ServerInstructions = Instructions.Text;
            })
            .WithStdioServerTransport()
            .WithToolsFromAssembly(typeof(ServerSetup).Assembly, McpJson.Options)
            .WithRequestFilters(f =>
            {
                f.AddListToolsFilter(Toolsets.ListFilter(sets, readOnly));
                f.AddCallToolFilter(Toolsets.CallFilter(sets, readOnly));
                f.AddCallToolFilter(ToolErrorFilter.Create());
            });
        // After the SDK has collected the tools (they are singletons): compact their schemas once.
        builder.Services.PostConfigure<McpServerOptions>(o =>
        {
            foreach (var t in o.ToolCollection ?? []) SchemaCompactor.Apply(t.ProtocolTool);
        });
    }

    /// <summary>The tools as the server lists them (after <see cref="SchemaCompactor"/>).</summary>
    public static List<Tool> ListedTools(IServiceProvider services) =>
        services.GetRequiredService<Microsoft.Extensions.Options.IOptions<McpServerOptions>>().Value.ToolCollection?
            .Select(t => t.ProtocolTool).ToList() ?? [];
}
