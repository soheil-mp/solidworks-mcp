# MCP C# SDK 2.3.0: developer notes (verified)

Verified 2026-10-22 on the development machine: .NET SDK 10.0.402, `ModelContextProtocol` 2.3.0 (with
`ModelContextProtocol.Core` 2.3.0 and `Microsoft.Extensions.AI.Abstractions` 10.11.0), `Microsoft.Extensions.Hosting`
10.0.13, `net10.0-windows`. Method: the SDK XML docs, then throwaway probe servers driven by a Python stdio JSON-RPC
client. Both protocol styles were exercised: `initialize` (revision 2025-06-18) and `server/discover` with per-request
`_meta` (revision 2026-07-28).

- **[V]** means verified by a probe. **[D]** means taken from documentation and not re-tested: the Claude API "Define
  tools" page and the Claude Code MCP page (code.claude.com/docs/en/mcp), both read on 2026-10-22.
- Every code block in §1–§4 and §9 was compiled with `TreatWarningsAsErrors=true` and exercised end to end. The one
  exception is the 3-line xUnit wrapper in §9.

---

## 0. What this changes in DESIGN.md

| # | Finding | Consequence | DESIGN |
|---|---|---|---|
| 1 | The SDK prefixes `McpException` text with `An error occurred invoking '<tool>': `. Every other exception, `SwException` included, becomes the bare `An error occurred invoking '<tool>'.` with no detail. [V] | Don't convert to `McpException`. One `AddCallToolFilter` maps exceptions to `CallToolResult { IsError = true }` with the code as a true prefix (§4). | §3.4, §6.2 |
| 2 | Argument-binding failures (missing, wrong type, bad enum) also give the generic message. Unknown or misspelled argument names are **silently ignored**, so the default is used. `null` for an optional `double`/`bool` is an error. [V] | The same filter pre-validates names, treats `null` as "use the default", and names the failing argument (§4). | §3.4, §4 |
| 3 | Tool calls run **concurrently** on MTA thread-pool threads. A quick call is answered while two blocking calls run. [V] Claude Code moves calls running longer than 2 min to a background task, and Claude keeps issuing calls. [D] | Confirms the §3.2 design: every COM call goes through the STA queue, `dialog_*`/`sw_status` can bypass it, and the queue needs `BUSY` semantics. The STA thread **must** be `IsBackground = true` (§2). | §3.2 |
| 4 | Tool-class instances are created **per call** and disposed after each call. Registering the class with `AddSingleton` changes nothing. [V] | State lives only in DI singletons (`SwSession`). Services that tool classes create (§7.1) must be stateless. Never dispose injected singletons in a tool's `Dispose`. | §3.1, §7.1 |
| 5 | Only explicitly assigned annotation properties are emitted. `ReadOnly = true` alone yields `{readOnlyHint:true}`. Clients assume `destructive` and `openWorld` are true when absent. [V] | Set all four hints on every tool, `OpenWorld = false` included. Assert it in a catalog test (§9). | §4, §6.2 |
| 6 | Enums are strings with C# names (`"SketchSegment"`) by default. [V] | DESIGN uses snake_case (`"sketch_segment"`, `"through_all"`): use a global converter with `Insert(0)`, not `Add` (§1, `McpJson`). | §3.7, §4.5 |
| 7 | Duplicate tool names are **silently dropped** (the first registration wins). An invalid name crashes the server at startup. [V] | A catalog check is needed before parallel area work starts (§9). | §7.1 |
| 8 | A `NaN`/`Infinity` anywhere in a result throws during serialization, and the whole result is lost (generic error). [V] | Core maps non-finite doubles to `null`. Don't enable `AllowNamedFloatingPointLiterals` (§3). | §3.9, §3.10 |
| 9 | This machine's culture is `en-NL`: `$"{1.5}"` renders `1,5` on tool threads. [V] JSON itself is invariant. | Set invariant culture at startup (§1). | L12 |
| 10 | The default host console logger writes to **stdout**, and `Console.WriteLine` in a tool lands on stdout. [V] | Log to stderr only, and add `Console.SetOut(Console.Error)` (safe, because the transport uses the raw stdout stream) (§8). | §3, §8 |
| 11 | On stdin EOF the SDK waits for in-flight calls and does **not** cancel their tokens. [V] On Windows, Claude Code closes stdin and then kills the server's **process tree** after 300 ms. [D] | Preferences changed by an interrupted call are only restored through the §3.8 journal. Launch SLDWORKS.exe through COM activation, never with `Process.Start`: a child process dies with our tree, unsaved work included. This is reasoned, not tested. | §3.3, §3.8 |
| 12 | Claude Code cuts tool descriptions and server instructions at 4,096 chars. Text results over 50,000 chars are saved to a file unless the tool sets `_meta["anthropic/maxResultSizeChars"]`. `isError` text over about 11,000 chars keeps only its first and last 5,000. Images count toward `MAX_MCP_OUTPUT_TOKENS` (25k by default). [D] | `face_list`/`edge_list` need `limit` and filters plus the meta key. Keep error text short and cap candidate lists. `vba_run` can force a human approval with `anthropic/requiresUserInteraction` (§5). | §4.3, §4.14, §9 |
| 13 | The SDK analyzer (an XML-doc → `[Description]` source generator) is blocked by Application Control, which produces warning CS8034. That warning is an error under `TreatWarningsAsErrors`, and it makes descriptions machine-dependent. [V] | Remove the analyzer with an MSBuild target (verified). `ExcludeAssets` doesn't work (§1). | §9 risks |
| 14 | Structured output doubles the payload and reshapes output across protocol revisions. [V] | Keep `UseStructuredContent` off in v1 (§7). | §4 |

---

## 1. Host setup [V]

**csproj** (`SolidWorksMcp.Server.csproj`): add the analyzer-removal target.

```xml
<!-- ModelContextProtocol.Core ships an analyzer (XML-doc -> [Description] source generator, MCP001/MCP002).
     Application Control blocks it on some machines (warning CS8034, an error under TreatWarningsAsErrors), which also
     makes descriptions machine-dependent. All tools use explicit [Description], so remove it everywhere.
     ExcludeAssets="analyzers" on a direct Core reference does NOT remove it (verified). -->
<Target Name="DropMcpSdkAnalyzer" BeforeTargets="CoreCompile">
  <ItemGroup>
    <Analyzer Remove="@(Analyzer)" Condition="'%(Filename)' == 'ModelContextProtocol.Analyzers'" />
  </ItemGroup>
</Target>
```

**Program.cs**

```csharp
using System.Globalization;
using Microsoft.Extensions.Hosting;
using SolidWorksMcp.Server;

// stdout is the JSON-RPC channel. The stdio transport writes to the raw stdout stream, so redirecting
// Console.Out only catches stray Console.Write* calls (verified: protocol unaffected, stray text lands on stderr).
Console.SetOut(Console.Error);

// This machine runs en-NL: without this, $"{1.5}" renders "1,5" in every message (verified on tool threads).
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;

if (args.Contains("--check-catalog"))
    return CatalogCheck.Run();

// Empty builder: no appsettings.json/env/command-line configuration read from the client's working directory,
// and no default logging providers (the default console logger writes to stdout and corrupts the protocol).
var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { Args = args });
ServerSetup.Configure(builder);
await builder.Build().RunAsync();
return 0;
```

**ServerSetup.cs**

```csharp
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace SolidWorksMcp.Server;

/// <summary>Everything the host needs; shared by Program and the catalog test.</summary>
public static class ServerSetup
{
    public const string Instructions = """
        Drives the user's running SOLIDWORKS 2025. All lengths are mm, angles degrees, mass kg.
        Start with sw_status. Select geometry with EntityRef objects ({"id":"E12"} from face_list/edge_list,
        or {"type":"face","at":[x,y,z]}). Errors start with a code (ENTITY_NOT_FOUND, TIMEOUT, ...) and a hint.
        """;

    public static void Configure(IHostApplicationBuilder builder)
    {
        builder.Logging.ClearProviders(); // no-op on the empty builder, essential on Host.CreateApplicationBuilder
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace); // every level -> stderr
        builder.Logging.AddFilter("ModelContextProtocol", LogLevel.Warning); // SDK logs every request at Information

        builder.Services.AddSingleton<SwSession>(); // created lazily on first use, shared by all calls

        builder.Services
            .AddMcpServer(o =>
            {
                o.ServerInfo = new Implementation { Name = "solidworks-mcp", Title = "SOLIDWORKS", Version = Version };
                o.ServerInstructions = Instructions;
            })
            .WithStdioServerTransport()
            // Pass the assembly explicitly: the default is the *calling* assembly.
            .WithToolsFromAssembly(typeof(ServerSetup).Assembly, McpJson.Options)
            .WithRequestFilters(f => f.AddCallToolFilter(ToolErrorFilter.Create()));
    }

    // Without ServerInfo the SDK reports the assembly name and the 4-part AssemblyVersion ("0.1.0.0").
    private static string Version =>
        typeof(ServerSetup).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+')[0] ?? "0.0.0";
}
```

**McpJson.cs**: one options instance for argument binding, input schemas and result serialization. The SDK uses the
options passed to `WithToolsFromAssembly` for all three.

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol;

namespace SolidWorksMcp.Server;

/// <summary>Serializer options for tool arguments, input schemas and tool results (one instance, read-only).</summary>
public static class McpJson
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        // Copy the SDK defaults: Web (camelCase, case-insensitive properties), WhenWritingNull,
        // AllowReadingFromString, a JsonStringEnumConverter, and the SDK's type-info resolver chain.
        var o = new JsonSerializerOptions(McpJsonUtilities.DefaultOptions);

        // Enums as snake_case strings ("face", "sketch_segment", "through_all") in schemas, inputs and outputs.
        // Insert(0): the defaults already contain a JsonStringEnumConverter that would win over Add().
        // allowIntegerValues:false: otherwise "kind": 1 is silently accepted.
        o.Converters.Insert(0, new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false));

        // A missing member of a positional record (e.g. z of Vec3) becomes an error instead of a silent 0.
        o.RespectRequiredConstructorParameters = true;

        // null for a non-nullable member of a DTO becomes an error instead of slipping through.
        o.RespectNullableAnnotations = true;

        // Do NOT add JsonNumberHandling.AllowNamedFloatingPointLiterals: it rewrites every number in every
        // input schema into an anyOf string/number/"NaN" union (+22% tools/list). Map NaN/Infinity to null instead.
        o.MakeReadOnly();
        return o;
    }
}
```

What the client receives [V]:
- **`initialize`** (2025-06-18): `serverInfo {name,title,version}`, `instructions`, and
  `capabilities {"logging":{}, "tools":{"listChanged":true}}`. The server echoes the requested revision when it supports
  it. Supported revisions are 2024-11-05, 2025-03-26, 2025-06-18, 2025-11-25 and 2026-07-28. [D: SDK docs]
- **`server/discover`** (2026-07-28): `supportedVersions:["2026-07-28"]`, `capabilities` (no `logging`),
  `instructions`, `ttlMs:0`, `cacheScope:"private"`, and `_meta["io.modelcontextprotocol/serverInfo"]`. **Every** result
  on this revision carries that `_meta` serverInfo, about 110 bytes. Claude Code's v2 runtime asks stdio servers for
  2026-07-28 and uses it when they support it [D], so expect this mode in practice.
- **Without `ServerInfo`** the server reports `{name: <AssemblyName>, version: "0.1.0.0"}`, with no title and no
  instructions.
- Claude Code loads only tool names and server instructions up front, because tool search is on by default, and it
  cuts the instructions at 4,096 chars. [D] The instructions therefore have to say what the server is for, so that Claude
  searches for its tools.
- `Host.CreateEmptyApplicationBuilder` works with the SDK: DI, tools and a clean exit 0.02 s after stdin closes.

---

## 2. Tool classes, DI and lifetimes [V]

- `WithToolsFromAssembly(assembly, options)` registers every `[McpServerTool]` method, static or instance, of every
  `[McpServerToolType]` class. `WithTools<T>()` does not accept a static class (CS0718); use `WithTools([typeof(T)])`
  instead.
- **Instance tool classes**: a new instance is built for every call, with constructor injection from the request scope.
  If the class implements `IDisposable`, it is disposed after every call. This holds even if the class itself is
  registered with `AddSingleton`; that registration is ignored.
- **Lifetimes**: a singleton is the same object for every call. A scoped service is new per call and disposed when the
  call ends (`ScopeRequests` defaults to `true`). A transient is new on every resolution.
- **Parameters that never appear in the schema** and are bound by the SDK: `CancellationToken`,
  `IProgress<ProgressNotificationValue>`, `RequestContext<CallToolRequestParams>` (with `Params.Name`,
  `Params.ProgressToken`, `Services`, `Server`), `McpServer` (with `ClientInfo` and `NegotiatedProtocolVersion`),
  `IServiceProvider` (the request scope), and **any parameter whose type is registered in DI** (for example `SwSession`
  or `ILogger<T>`). Don't register DTO types in DI, or they silently disappear from the schema.
- **Threads**: tools run on thread-pool **MTA** threads, several at a time. COM objects must never be touched in a tool
  method. Queue the work onto the STA thread instead.
- A singleton's constructor runs on first resolution, which is inside the first tool call that needs it. Keep it
  trivial and connect lazily (§3.3). If it throws, that call gets an `INTERNAL` error.

<details><summary><b>SwSession.cs</b>: stand-in for Core's session and STA dispatcher (verified)</summary>

```csharp
using System.Collections.Concurrent;

namespace SolidWorksMcp.Server;

/// <summary>Error raised by Core; the CallTool filter turns it into "CODE: message Hint: hint".</summary>
public sealed class SwException(string code, string message, string? hint = null) : Exception(message)
{
    public string Code { get; } = code;
    public string? Hint { get; } = hint;
}

/// <summary>
/// Stand-in for Core's SwSession/StaDispatcher. Tool methods run on MTA thread-pool threads, several at once,
/// so every COM call has to be queued onto this one STA thread.
/// </summary>
public sealed class SwSession : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();

    public SwSession()
    {
        var sta = new Thread(() => { foreach (var work in _queue.GetConsumingEnumerable()) work(); })
        {
            IsBackground = true, // a COM call stuck behind a modal dialog must not keep the process alive at exit
            Name = "sw-sta",
        };
        sta.SetApartmentState(ApartmentState.STA);
        sta.Start();
    }

    public async Task<T> RunAsync<T>(string operation, Func<T> func, TimeSpan timeout, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(() =>
        {
            try { tcs.TrySetResult(func()); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        }, ct);
        try
        {
            return await tcs.Task.WaitAsync(timeout, ct);
        }
        catch (TimeoutException)
        {
            throw new SwException("TIMEOUT", $"'{operation}' did not finish within {timeout.TotalSeconds:0} s.",
                "Call dialog_list: a modal dialog may be blocking SOLIDWORKS.");
        }
    }

    public void Dispose() => _queue.CompleteAdding(); // the host disposes singletons at shutdown
}
```
</details>

Measured: with a foreground thread asleep, the process stayed alive 14.7 s after stdin closed. With
`IsBackground = true` it exited in 0.03 s.

<details><summary><b>Tools/DemoTools.cs</b>: template for an area's tool class (verified)</summary>

```csharp
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace SolidWorksMcp.Server.Tools;

public enum EntityKind { Face, Edge, Vertex, Plane, Axis, Sketch, Feature, Body, Component, SketchSegment, SketchPoint }

/// <summary>EntityRef (DESIGN 3.7). Positional parameters with defaults are optional members.</summary>
public sealed record EntityRef(
    [Description("Persistent id from face_list/edge_list/vertex_list, e.g. \"E12\".")] string? Id = null,
    [Description("Entity type; required together with 'at' or 'name'.")] EntityKind? Type = null,
    [property: Length(2, 3)][Description("Pick point in mm: [x, y, z] (model space) or [x, y] (sketch).")] double[]? At = null,
    [Description("Name, e.g. \"Front Plane\" or \"Boss-Extrude1\".")] string? Name = null);

public sealed record Verified(double VolumeDeltaMm3, int FaceCountDelta);

public sealed record FeatureResult(string Doc, string Feature, Verified Verified, string[] Warnings);

public sealed record FaceInfo(string Id, string SurfaceType, double AreaMm2, double[] PickPointMm);

[McpServerToolType]
public sealed class DemoTools(SwSession session) // new instance per call; SwSession is the singleton
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    private static readonly byte[] Png1x1 = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=");

    [McpServerTool(Name = "feature_fillet", Title = "Fillet edges",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Adds a constant-radius fillet to edges of the active part and verifies the volume decreased.")]
    public Task<FeatureResult> FeatureFillet(
        [Description("Edges to round, e.g. [{\"id\":\"E3\"}] or [{\"type\":\"edge\",\"at\":[0,0,10]}].")] EntityRef[] edges,
        [Description("Fillet radius in mm (> 0).")] double radiusMm,
        [Description("Also round tangent-connected edges.")] bool tangentPropagation = true,
        CancellationToken cancellationToken = default)
    {
        if (radiusMm <= 0)
            throw new SwException("INVALID_ARGUMENT", $"radiusMm must be > 0 (got {radiusMm}).");
        if (edges.Length == 0)
            throw new SwException("INVALID_ARGUMENT", "edges is empty.", "Pass at least one EntityRef.");
        return session.RunAsync("feature_fillet", () =>
        {
            if (edges[0].Id == "E404")
                throw new SwException("ENTITY_NOT_FOUND", "no edge with id E404.", "Re-run edge_list.");
            return new FeatureResult("Part1", "Fillet1", new Verified(-536.5, 1), []);
        }, Timeout, cancellationToken);
    }

    [McpServerTool(Name = "face_list", Title = "List faces",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [McpMeta("anthropic/maxResultSizeChars", 200000)] // Claude Code otherwise moves results > 50,000 chars to a file
    [Description("Lists faces of the active part with ids, surface type, area and a pick point.")]
    public Task<FaceInfo[]> FaceList(
        [Description("Maximum number of faces to return.")][Range(1, 2000)] int limit = 200,
        CancellationToken cancellationToken = default) =>
        session.RunAsync("face_list", () => new[] { new FaceInfo("F1", "plane", 6000, [50, 30, 20]) }.Take(limit).ToArray(),
            Timeout, cancellationToken);

    [McpServerTool(Name = "screenshot", Title = "Screenshot",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Returns a PNG of the current SOLIDWORKS view inline, plus the view metadata as JSON.")]
    public async Task<CallToolResult> Screenshot(
        [Description("Longest image edge in pixels.")][Range(64, 2048)] int maxSizePx = 1024,
        CancellationToken cancellationToken = default)
    {
        byte[] png = await session.RunAsync("screenshot", () => Png1x1, Timeout, cancellationToken);
        return new CallToolResult
        {
            Content =
            [
                new TextContentBlock { Text = JsonSerializer.Serialize(new { view = "isometric", maxSizePx }, McpJson.Options) },
                ImageContentBlock.FromBytes(png, "image/png"),
            ],
        };
    }

    [McpServerTool(Name = "vba_run", Title = "Run VBA",
        ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [McpMeta("anthropic/requiresUserInteraction", true)] // Claude Code prompts on every call, even in bypass modes
    [Description("Runs VBA code inside SOLIDWORKS and returns its captured output.")]
    public string VbaRun([Description("VBA statements; wrapped in Sub main.")] string code) =>
        throw new McpException("NOT_SUPPORTED: vba_run is disabled in this build.");

    [McpServerTool(Name = "sim_static", Title = "Static study",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Runs a linear static study and reports progress while meshing and solving.")]
    public async Task<string> SimStatic(
        IProgress<ProgressNotificationValue> progress, // not in the schema; no-op unless the client sent a progressToken
        CancellationToken cancellationToken,           // not in the schema; cancelled by notifications/cancelled
        [Description("Mesh element size in mm.")] double meshSizeMm = 5)
    {
        string[] steps = ["mesh", "solve", "results"];
        for (int i = 0; i < steps.Length; i++)
        {
            await Task.Delay(100, cancellationToken);
            progress.Report(new ProgressNotificationValue { Progress = i + 1, Total = steps.Length, Message = steps[i] });
        }
        return $"done (mesh {meshSizeMm} mm)";
    }
}
```
</details>

---

## 3. Parameters and the generated JSON Schema [V]

The schema the SDK generated for `feature_fillet` above, with snake_case enums:

```json
{"type":"object","properties":{
  "edges":{"description":"Edges to round, ...","type":"array","items":{"type":"object","properties":{
     "id":{"description":"Persistent id ...","type":["string","null"],"default":null},
     "type":{"description":"Entity type; ...","type":["string","null"],
             "enum":["face","edge","vertex","plane","axis","sketch","feature","body","component","sketch_segment","sketch_point",null],"default":null},
     "at":{"description":"Pick point in mm ...","type":["array","null"],"items":{"type":"number"},"default":null,"minItems":2,"maxItems":3},
     "name":{"description":"Name, ...","type":["string","null"],"default":null}}}},
  "radiusMm":{"description":"Fillet radius in mm (> 0).","type":"number"},
  "tangentPropagation":{"description":"Also round tangent-connected edges.","type":"boolean","default":true}},
 "required":["edges","radiusMm"]}
```

| Topic | Behaviour |
|---|---|
| Parameter names | The C# parameter name **verbatim**, with no naming policy (`DepthMM2` stays `DepthMM2`). Argument lookup is **case-sensitive**: `DepthMm` does not bind to `depthMm`. |
| DTO property names | camelCase (Web policy). `[JsonPropertyName]` is respected. Nested keys bind **case-insensitively** (`"ID"`, `"AT"` work). |
| Unknown names | Unknown top-level arguments and unknown nested keys are **silently ignored** (`UnmappedMemberHandling.Skip`). |
| `required` (top level) | Every parameter **without a C# default**, nullable or not: `string? x` and `double? y` without a default are required. |
| `required` (nested) | Comes from the C# `required` keyword, `[Required]`, or a positional-record parameter without a default. A plain non-nullable property is **not** required. |
| Nullability | Top-level NRT is honoured in the schema (`string` versus `["string","null"]`). Nested reference types are **always** `["string","null"]` / `["object","null"]`; `RespectNullableAnnotations` changes binding, not the schema. |
| Positional records | A missing member such as `z` of `Vec3(X,Y,Z)` is silently `0` unless `RespectRequiredConstructorParameters = true`, which makes it an error. |
| Arrays | `double[]` gives `{"type":"array","items":{"type":"number"}}`. `double[][]` and `List<T>` work. A scalar, or `"[1,2,3]"` as a string, is a binding error. |
| Enums | By default: strings with the C# names, input case-insensitive, **integers accepted** (`1` becomes `Edge`), snake input rejected. With `McpJson`: snake_case everywhere, PascalCase input still accepted, integers rejected. `[JsonStringEnumMemberName("x")]` is honoured, and the C# name is then rejected. The global options converter **overrides** a type-level `[JsonConverter]`. |
| Defaults | Emitted as `"default"`: an enum default appears as its string, and a null default as `"default":null`. |
| `[Description]` | On the method it becomes the tool description; a tool without one gets `"description":""`. On parameters and DTO properties it is used directly, and on positional-record parameters it works **without** a `property:` target. |
| Data annotations | `[Range]` emits `minimum`/`maximum` and `[Length]` emits `minItems`/`maxItems` (use `property:` on records). They are **not enforced**: `count=0` and `[1,2]` were accepted. Validate in code. |
| Numbers | `"12.5"` binds to `double` (`AllowReadingFromString`), but `"true"` does not bind to `bool`. `AllowNamedFloatingPointLiterals` rewrites every number schema into `anyOf[{type:[string,number],pattern},{enum:[NaN,Infinity,-Infinity]}]`, so don't set it. |
| `null` values | `null` for `double x = 1.5` is a binding error. `null` for a non-nullable `string` parameter is passed into the method. The filter in §4 handles both. |
| Text encoding | Output JSON escapes non-ASCII (`\u00D8`) and is therefore pure ASCII. Raw UTF-8 input decodes correctly (`ü Ø ² –` round-trip). The console code page doesn't matter. |

---

## 4. Results and errors [V]

| Tool returns | Wire result |
|---|---|
| POCO, record, anonymous type, `Task<T>` | One text block holding **compact** JSON, with camelCase properties, nulls omitted and enums as strings, using the tool's serializer options. |
| `string` | One text block, verbatim. |
| `null`, `void` | `"content": []` |
| `JsonNode` / `JsonElement` | One text block holding that JSON. |
| `ContentBlock`, `IEnumerable<ContentBlock>` | Exactly as given. |
| `CallToolResult` | Verbatim. `IsError` is `bool?` and `StructuredContent` is `JsonElement?`. |

Images: `ImageContentBlock.FromBytes(pngBytes, "image/png")` produces `{"type":"image","data":"<base64>","mimeType":"image/png"}`.
`ImageContentBlock.Data` is a `ReadOnlyMemory<byte>` holding base64 UTF-8, so don't assign `Convert.ToBase64String`
output to it. Claude Code shows PNG/JPEG/GIF/WebP inline (possibly scaled down) and also saves the original file. [D]

| Situation | Without a filter | With `ToolErrorFilter` |
|---|---|---|
| `throw new McpException("ENTITY_NOT_FOUND: …")` | `isError`, `An error occurred invoking 'x': ENTITY_NOT_FOUND: …` (the message of an inner exception is dropped) | the message verbatim |
| `SwException` or any other exception | `isError`, `An error occurred invoking 'x'.` (the detail appears only in the stderr log) | `CODE: message Hint: hint` / `INTERNAL: Type: message` |
| `McpProtocolException(msg, code)` | a **JSON-RPC error** `{code,message}`, not a tool result. Never throw it from tools. | rethrown |
| unknown tool name | JSON-RPC error `-32602 Unknown tool: 'x'` (the filter runs with `MatchedPrimitive == null`) | rethrown |
| missing required argument | generic message | `INVALID_ARGUMENT: missing required argument(s): depthMm.` |
| wrong type or bad enum | generic message | `INVALID_ARGUMENT: argument 'edges[0].type' is invalid: got "edg"; expected {"type":["string","null"],"enum":["face","edge",…]}.` |
| misspelled or miscased argument | **ignored**, so the default is used | `INVALID_ARGUMENT: unknown argument 'radius_mm' for tool 'feature_fillet'. Did you mean 'radiusMm'? Valid arguments: edges, radiusMm, tangentPropagation.` |
| `null` for an optional argument | binding error for value types | treated as "use the default" |
| `null` for a required non-nullable argument | `null` reaches the method (`string`) | `INVALID_ARGUMENT: argument 'edges' must not be null (expected array).` |
| `NaN` in the result | generic message, and the result is lost | `INTERNAL: ArgumentException: .NET number values such as positive and negative infinity cannot be written …` |
| client sent `notifications/cancelled` | no response is sent (per spec) | rethrown, still no response |

The filter receives every exception raw: from the tool, from argument binding and from result serialization. That makes
it the single place to implement §3.4.

<details><summary><b>ToolErrorFilter.cs</b> (verified)</summary>

```csharp
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace SolidWorksMcp.Server;

/// <summary>
/// One CallTool filter around every tool call (it also runs for unknown tool names, with MatchedPrimitive == null).
/// Exceptions thrown by a tool, by argument binding and by result serialization all reach it unconverted.
/// Without it the model gets "An error occurred invoking '&lt;tool&gt;'." for everything except McpException.
/// </summary>
public static class ToolErrorFilter
{
    public static McpRequestFilter<CallToolRequestParams, CallToolResult> Create() => next => async (ctx, ct) =>
    {
        var tool = ctx.MatchedPrimitive as McpServerTool;
        var args = ctx.Params?.Arguments ?? new Dictionary<string, JsonElement>();
        if (tool is not null)
        {
            DropNullsForDefaultedParameters(tool, args);
            if (CheckArgumentNames(tool, args) is { } problem)
                return Error("INVALID_ARGUMENT", problem);
        }

        try
        {
            return await next(ctx, ct);
        }
        catch (McpProtocolException)
        {
            throw; // e.g. "Unknown tool": stays a JSON-RPC error
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // the client sent notifications/cancelled; the SDK sends no response
        }
        catch (SwException ex)
        {
            return Error(ex.Code, ex.Message, ex.Hint);
        }
        catch (McpException ex)
        {
            return Text(ex.Message); // no "An error occurred invoking ..." prefix
        }
        catch (JsonException ex) when (tool is not null)
        {
            return Error("INVALID_ARGUMENT", DescribeBindingError(tool, args) ?? ex.Message);
        }
        catch (Exception ex)
        {
            ctx.Services?.GetService<ILoggerFactory>()?.CreateLogger(typeof(ToolErrorFilter))
                .LogError(ex, "Tool {Tool} failed", ctx.Params?.Name);
            return Error("INTERNAL", $"{ex.GetType().Name}: {ex.Message}");
        }
    };

    private static CallToolResult Error(string code, string message, string? hint = null) =>
        Text($"{code}: {message}" + (hint is null ? "" : $" Hint: {hint}"));

    private static CallToolResult Text(string text) =>
        new() { IsError = true, Content = [new TextContentBlock { Text = text }] };

    /// <summary>Models often send null for an optional argument: treat it as "use the declared default".</summary>
    private static void DropNullsForDefaultedParameters(McpServerTool tool, IDictionary<string, JsonElement> args)
    {
        if (args.IsReadOnly || tool.Metadata.OfType<MethodInfo>().FirstOrDefault() is not { } method) return;
        foreach (var p in method.GetParameters())
            if (p.HasDefaultValue && p.Name is { } n && args.TryGetValue(n, out var v) && v.ValueKind == JsonValueKind.Null)
                args.Remove(n);
    }

    /// <summary>Unknown (typo'd) names are silently ignored by the SDK; null reaches non-nullable parameters.</summary>
    private static string? CheckArgumentNames(McpServerTool tool, IDictionary<string, JsonElement> args)
    {
        var schema = tool.ProtocolTool.InputSchema;
        var props = schema.TryGetProperty("properties", out var p) && p.ValueKind == JsonValueKind.Object ? p : default;
        var known = props.ValueKind == JsonValueKind.Object ? props.EnumerateObject().Select(x => x.Name).ToList() : [];

        foreach (var (key, value) in args)
        {
            if (!known.Contains(key, StringComparer.Ordinal))
            {
                var near = known.FirstOrDefault(k =>
                    string.Equals(k.Replace("_", ""), key.Replace("_", ""), StringComparison.OrdinalIgnoreCase));
                return $"unknown argument '{key}' for tool '{tool.ProtocolTool.Name}'." +
                       (near is null ? "" : $" Did you mean '{near}'?") + $" Valid arguments: {string.Join(", ", known)}.";
            }
            if (value.ValueKind == JsonValueKind.Null && props.GetProperty(key).TryGetProperty("type", out var type) &&
                type.ValueKind == JsonValueKind.String)
                return $"argument '{key}' must not be null (expected {type.GetString()}).";
        }

        if (schema.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array)
        {
            var missing = req.EnumerateArray().Select(r => r.GetString()!).Where(r => !args.ContainsKey(r)).ToList();
            if (missing.Count > 0) return $"missing required argument(s): {string.Join(", ", missing)}.";
        }
        return null;
    }

    /// <summary>System.Text.Json does not say which parameter failed; re-bind each one to find out.</summary>
    private static string? DescribeBindingError(McpServerTool tool, IDictionary<string, JsonElement> args)
    {
        if (tool.Metadata.OfType<MethodInfo>().FirstOrDefault() is not { } method) return null;
        var props = tool.ProtocolTool.InputSchema.GetProperty("properties");
        foreach (var p in method.GetParameters())
        {
            if (p.Name is null || !args.TryGetValue(p.Name, out var value) || !props.TryGetProperty(p.Name, out var schema))
                continue;
            try
            {
                JsonSerializer.Deserialize(value, p.ParameterType, McpJson.Options);
            }
            catch (JsonException ex)
            {
                var (got, expected, path) = Locate(value, schema, ex.Path);
                return $"argument '{p.Name}{path}' is invalid: got {Clip(got.GetRawText(), 120)}; expected {Describe(expected)}.";
            }
        }
        return null;
    }

    /// <summary>Walks a "$[0].type"-style JsonException path through the value and its schema together.</summary>
    private static (JsonElement Value, JsonElement Schema, string Path) Locate(JsonElement value, JsonElement schema, string? path)
    {
        var shown = "";
        foreach (Match m in Regex.Matches(path ?? "", @"\[(\d+)\]|\.([^.\[]+)"))
        {
            if (m.Groups[1].Success && value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var items)
                && int.Parse(m.Groups[1].Value) is var i && i < value.GetArrayLength())
                (value, schema) = (value[i], items);
            else if (m.Groups[2].Success && value.ValueKind == JsonValueKind.Object
                     && value.TryGetProperty(m.Groups[2].Value, out var v) && schema.TryGetProperty("properties", out var ps)
                     && ps.TryGetProperty(m.Groups[2].Value, out var s))
                (value, schema) = (v, s);
            else
                break;
            shown += m.Value;
        }
        return (value, schema, shown);
    }

    private static readonly JsonSerializerOptions Readable = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static string Describe(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object) return schema.GetRawText();
        var node = JsonNode.Parse(schema.GetRawText())!.AsObject();
        node.Remove("description");
        node.Remove("default");
        return Clip(node.ToJsonString(Readable), 300);
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "...";
}
```
</details>

Notes:
- The filter mutates `ctx.Params.Arguments` (verified mutable). `ProtocolTool.InputSchema` and `Metadata[0]` (the
  `MethodInfo`) are available in the filter through `ctx.MatchedPrimitive as McpServerTool`.
- When the filter catches an exception the SDK logs nothing, so the filter logs `INTERNAL` itself. Exceptions it rethrows
  are logged by the SDK at Error, with a stack trace.
- Keep error text well below Claude Code's ~11,000-char truncation. In `ENTITY_AMBIGUOUS`, list a few candidates
  rather than all of them. [D]

---

## 5. Annotations and `_meta` [V]

| Attribute | Emitted in `tools/list` |
|---|---|
| none of `ReadOnly`/`Destructive`/`Idempotent`/`OpenWorld`/`Title` | no `annotations` key at all |
| `ReadOnly = true` | `{"readOnlyHint":true}` only |
| `Title = "Fillet edges"` | top-level `"title"` **and** `annotations.title` |
| all four set | all four hints, exactly as set |

- The attribute's getters default to `Destructive = true`, `OpenWorld = true`, `Idempotent = false` and
  `ReadOnly = false`, but a value is **emitted only when it is assigned**. Clients assume `destructiveHint` and
  `openWorldHint` are true when they are absent. SOLIDWORKS is a closed world, so set `OpenWorld = false` everywhere.
- `[McpMeta(name, value)]` comes in `string`, `double` and `bool` overloads and can be repeated. Each one becomes a key in
  the tool's `_meta`. Claude Code reads three keys [D]:
  - `"anthropic/maxResultSizeChars"`, a number up to 500,000. It raises the 50,000-char threshold at which a text
    result is moved to a file. Use it on `face_list`, `edge_list` and `feature_tree`, and still provide `limit`/filters.
  - `"anthropic/requiresUserInteraction"`, `true`. Claude Code then prompts on every call, even in bypass modes.
    Candidates are `vba_run` and `macro_run` (§4.14, §9 risk).
  - `"anthropic/alwaysLoad"`, `true`. The tool loads up front instead of through tool search. Candidates are `sw_status`
    and `model_info`.

---

## 6. Progress, cancellation, concurrency and shutdown [V]

- **Progress**: `progress.Report(new ProgressNotificationValue { Progress = i, Total = n, Message = "solve" })`
  produces `notifications/progress {progressToken, progress, total, message}`, but **only** when the request carried
  `_meta.progressToken`. Without a token, reports are silently dropped. `Progress` and `Total` are `float`. To send
  progress from elsewhere, for example the dispatcher, take `McpServer server` and
  `RequestContext<CallToolRequestParams> ctx` as tool parameters and call
  `server.NotifyProgressAsync(ctx.Params!.ProgressToken!.Value, value)`. That method is declared on the base class
  `McpSession` (signature verified, call not exercised).
- **Cancellation**: `notifications/cancelled {requestId}` cancels the tool's `CancellationToken` immediately, and **no
  response** is sent for that request. The behaviour is identical on 2025-06-18 and 2026-07-28. The COM call itself can't
  be aborted (§3.2).
- **Concurrency**: requests are dispatched concurrently. Two 1.5 s blocking calls both finished at 1.52 s on different
  threads, and a third quick call was answered at once. This makes §3.2's queue-bypassing `dialog_*` tools possible, and
  it means `BUSY` must be designed in: queue with a short bounded wait, then return `BUSY` with the current operation.
- **stdin EOF**: an idle server exits in 0.03 s. With a call in flight, the SDK **waits** for the call and does not cancel
  its token. An 8 s call delayed exit by 7.5 s. Requests the server hasn't started reading when EOF arrives are
  **dropped**: writing `initialize` + `tools/call` and closing stdin at once produced no response at all (3 of 3 runs),
  while waiting 0.5 s before closing produced both. A piped canned request file therefore returns nothing. Keep stdin
  open until the responses arrive, as `mcp_smoke.py` already does.
- **Claude Code** [D]:
  - Wall-clock limit per call: `MCP_TOOL_TIMEOUT` (default about 28 h) or a per-server `"timeout"` in `.mcp.json`.
    Progress does not extend it.
  - **Idle timeout of 30 min** for stdio when there is neither a response nor progress
    (`CLAUDE_CODE_MCP_TOOL_IDLE_TIMEOUT`). `sim_static` must report progress if it can run that long.
  - Main-conversation calls that run longer than 2 min move to a background task: Claude continues working, and `/tasks`
    shows the latest progress.
  - On Windows, at exit, Claude Code closes stdin and then kills the process tree 300 ms later. In-flight work is lost:
    the §3.8 journal must restore preferences at the next start (or first connect).

---

## 7. Structured content and `outputSchema` [V], not advisable for v1

- `[McpServerTool(UseStructuredContent = true)]` adds `outputSchema`, generated from the return type, to `tools/list`.
  Each result then carries `structuredContent` **and** the same JSON again as a text block, so the payload doubles.
- For a non-object return such as `double[]` on revisions below 2026-07-28, the SDK wraps the schema as
  `{"type":"object","properties":{"result":…},"required":["result"]}` and the value as `{"result":[1,2,3]}`. On
  2026-07-28 both are natural (`{"type":"array",…}` and `[1,2,3]`), so the result shape depends on the negotiated
  revision.
- When a tool returns `CallToolResult`, `OutputSchemaType = typeof(Dto)` sets the schema, and you must set
  `StructuredContent = JsonSerializer.SerializeToElement(dto, McpJson.Options)` on every successful result yourself.
  The MCP spec requires conforming structured results whenever a schema is declared, and says clients SHOULD validate
  them. [D]
- The Claude Code docs describe text and image results only. Claude reads the text JSON, so structured output adds
  tokens and failure modes without a benefit. Keep `UseStructuredContent` off and return compact JSON text.

---

## 8. stdout, stderr and logging [V]

- Without `ClearProviders()`, `Host.CreateApplicationBuilder`'s console logger writes `info: Microsoft.Hosting.Lifetime…
  Application started…` and every SDK log line to **stdout**, in the middle of the JSON-RPC stream. The probe client
  tolerated it; a strict client may not. Fix it with §1 (empty builder, or `ClearProviders()`, plus
  `AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace)`).
- `Console.WriteLine` inside a tool emits a raw non-JSON line on stdout. `Console.SetOut(Console.Error)` at startup sends
  it to stderr, and the protocol keeps working (the transport doesn't use `Console.Out`). Interop libraries and the
  prototype's `--probe` output then also go to stderr.
- At `Information`, the SDK logs every request (`request handler called/completed`, `"x" completed. IsError = …`) and
  every tool exception at `Error` with a stack trace. `AddFilter("ModelContextProtocol", LogLevel.Warning)` removes the
  per-request noise.
- MCP client logging (`McpServer.AsClientLoggerProvider()`, `LoggingLevel`, `logging/setLevel`, `notifications/message`)
  is `[Obsolete]` MCP9005, because Logging is deprecated in 2026-07-28. Messages flow only after the client sets a
  level. Don't use it.
- `SWMCP_LOG_FILE` (§8): `Microsoft.Extensions.Logging` has no file provider, so it needs a small custom
  `ILoggerProvider`.

---

## 9. Catalog check [V]

`solidworks-mcp --check-catalog` (handled in Program.cs, §1) builds the host without starting the transport and checks
the invariants that the SDK does not enforce. `host.Services.GetServices<McpServerTool>()` returns **every**
registration, duplicates included, with schemas exactly as served (DI parameters already removed). The served
`ToolCollection` keeps only the first of each name.

<details><summary><b>CatalogCheck.cs</b> (verified: prints <code>CATALOG: ok</code>, exit code 0)</summary>

```csharp
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Server;

namespace SolidWorksMcp.Server;

/// <summary>Tool-catalog invariants (DESIGN 4 conventions, 6.2). The body of Problems() works unchanged in an xUnit test.</summary>
public static class CatalogCheck
{
    private const int ClientPrefix = 17; // "mcp__solidworks__"; Claude Code drops tools whose full name exceeds 128

    public static List<string> Problems()
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        ServerSetup.Configure(builder);
        using var host = builder.Build(); // building does not start the stdio transport

        // Every registration, duplicates included (the served ToolCollection silently keeps only the first).
        var tools = host.Services.GetServices<McpServerTool>().Select(t => t.ProtocolTool).ToList();
        var problems = new List<string>();
        foreach (var g in tools.GroupBy(t => t.Name).Where(g => g.Count() > 1))
            problems.Add($"duplicate tool name '{g.Key}' x{g.Count()}");

        foreach (var t in tools)
        {
            if (!Regex.IsMatch(t.Name, "^[a-z][a-z0-9_]*$") || t.Name.Length > 128 - ClientPrefix)
                problems.Add($"{t.Name}: name must be snake_case and at most {128 - ClientPrefix} chars");
            if (string.IsNullOrWhiteSpace(t.Description))
                problems.Add($"{t.Name}: missing [Description] (the SDK then sends \"\")");
            else if (t.Description.Length > 4096)
                problems.Add($"{t.Name}: description over 4,096 chars is cut by Claude Code");

            var a = t.Annotations; // only explicitly assigned attribute properties are emitted
            if (a?.ReadOnlyHint is null || a.OpenWorldHint is null || a.DestructiveHint is null || a.IdempotentHint is null)
                problems.Add($"{t.Name}: set ReadOnly, Destructive, Idempotent and OpenWorld explicitly");

            foreach (var p in t.InputSchema.GetProperty("properties").EnumerateObject())
                if (!Regex.IsMatch(p.Name, "^[a-z][A-Za-z0-9]{0,63}$"))
                    problems.Add($"{t.Name}.{p.Name}: parameter names are camelCase ASCII, at most 64 chars");
        }
        return problems;
    }

    public static int Run()
    {
        var problems = Problems();
        foreach (var p in problems) Console.Error.WriteLine("CATALOG: " + p);
        Console.Error.WriteLine(problems.Count == 0 ? "CATALOG: ok" : $"CATALOG: {problems.Count} problem(s)");
        return problems.Count == 0 ? 0 : 1;
    }
}
```
</details>

As an xUnit v3 test (not compiled here, because xUnit isn't in the local cache). The test project needs a
`ProjectReference` to the Server project; referencing an exe project is fine.

```csharp
public class CatalogTests
{
    [Fact]
    public void Tool_catalog_follows_conventions() => Assert.Empty(SolidWorksMcp.Server.CatalogCheck.Problems());
}
```

---

## 10. Limits and naming rules

| Item | SDK 2.3.0 [V] | Claude API [D] | Claude Code [D] |
|---|---|---|---|
| Tool name | `^[A-Za-z0-9_.-]{1,128}\z`. An invalid name throws `ArgumentException` at host start, and the process dies before `initialize`. | `^[a-zA-Z0-9_-]{1,128}$`, so **no dots** | Full name `mcp__<server>__<tool>`; a tool whose full name is over 128 chars is omitted. Plugin servers use `mcp__plugin_<plugin>_<server>__<tool>`, with characters outside `[A-Za-z0-9_-]` replaced by `_`. |
| Name when `Name` is unset | snake_case of the method name (`GetModelInfo` → `get_model_info`) | | |
| Duplicate names | silently dropped; the first registration wins | | |
| Tool description | no limit (20,000 chars accepted) | | cut at 4,096 chars when loaded up front, or 16,384 through tool search (`CLAUDE_CODE_MAX_MCP_DESCRIPTION_LENGTH`) |
| Server instructions | no limit | | cut at 4,096 chars |
| Top-level parameter names | the C# identifier | | 1–64 chars of `[A-Za-z0-9_.-]`; otherwise the tool is excluded. The schema must be valid JSON Schema 2020-12. Root-level `anyOf`/`oneOf`/`allOf` is flattened (the SDK never emits it). |
| Tool count | 172 tools in one `tools/list` page; no `nextCursor` | | tools are deferred behind tool search by default |
| Result size | none | | warning above 10k tokens; `MAX_MCP_OUTPUT_TOKENS` defaults to 25k; text over 50,000 chars moves to a file unless `_meta` raises the limit; images count toward the token limit; `isError` text over ~11k chars is middle-truncated |

---

## 11. Wire shapes for `tools/mcp_smoke.py` [V]

2025-06-18, the form the current smoke script uses:

```json
{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"mcp_smoke","version":"0.1"}}}
{"jsonrpc":"2.0","method":"notifications/initialized"}
{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"sim_static","arguments":{},"_meta":{"progressToken":"p1"}}}
```

2026-07-28, which Claude Code's v2 runtime uses. Add a pass in this form, and send the same three `_meta` keys on every
request. A request without `io.modelcontextprotocol/protocolVersion` is rejected with `-32602` ("Requests using protocol
version '2026-07-28' must include '_meta/io.modelcontextprotocol/protocolVersion'").

```json
{"jsonrpc":"2.0","id":1,"method":"server/discover","params":{"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientInfo":{"name":"mcp_smoke","version":"0.1"},"io.modelcontextprotocol/clientCapabilities":{}}}}
{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientInfo":{"name":"mcp_smoke","version":"0.1"},"io.modelcontextprotocol/clientCapabilities":{}}}}
```

Other message shapes:
- Progress: `{"method":"notifications/progress","params":{"progressToken":"p1","progress":1,"total":3,"message":"mesh"}}`
  arrives before the response.
- Cancel: send `{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":5,"reason":"stop"}}`; no
  response follows for id 5.
- Errors: tool failures arrive as `result.isError: true` with the code as the first token of `content[0].text`. An
  unknown tool is a top-level `error` (`-32602`), so the script must accept both shapes.

---
