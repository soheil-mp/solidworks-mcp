using System.Globalization;
using Microsoft.Extensions.Hosting;
using SolidWorksMcp.Server.Hosting;

// stdout is the JSON-RPC channel. The stdio transport writes to the raw stdout stream, so redirecting Console.Out only
// catches stray Console.Write* calls (they land on stderr instead of corrupting the protocol).
Console.SetOut(Console.Error);

// Decimal-comma locales must never leak into numbers or messages.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;

if (args.Contains("--version"))
{
    Console.Error.WriteLine($"solidworks-mcp {ServerSetup.Version}");
    return 0;
}
if (args.Contains("--check-catalog"))
    return CatalogCheck.Run();
if (Array.IndexOf(args, "--tools-md") is int mdAt and >= 0)
{
    // solidworks-mcp --tools-md docs/tools.md   (UTF-8 file; the console code page would mangle characters)
    string target = mdAt + 1 < args.Length ? args[mdAt + 1] : "tools.md";
    File.WriteAllText(target, CatalogCheck.Markdown(), new System.Text.UTF8Encoding(false));
    Console.Error.WriteLine($"Wrote {Path.GetFullPath(target)}");
    return 0;
}

// Empty builder: no appsettings.json / environment / command-line configuration from the client's working directory,
// and no default logging providers (the default console logger writes to stdout).
var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { Args = args });
ServerSetup.Configure(builder);
await builder.Build().RunAsync();
return 0;
