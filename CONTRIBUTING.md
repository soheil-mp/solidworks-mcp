# Contributing to solidworks-mcp

Thanks for helping. This file states the rules every tool follows. The plan of record is
[`docs/DESIGN.md`](docs/DESIGN.md). Verified SolidWorks API recipes are in
[`docs/solidworks-api-notes.md`](docs/solidworks-api-notes.md), and the MCP SDK's behaviour is in
[`docs/mcp-sdk-notes.md`](docs/mcp-sdk-notes.md).

## Prerequisites
- Windows 10/11 x64, the .NET 10 SDK, and git.
- A SOLIDWORKS installation (tested: 2025 SP1.2) to build Core, the Server and the live tests.
  - The interop assemblies come from `<SOLIDWORKS>\api\redist`. The build finds them through `SOLIDWORKS_DIR` or the
    registry.
  - **Never commit them**, typelib dumps or API-help text. `tools/check_repo.py` enforces this.
- The offline unit tests (`tests/SolidWorksMcp.UnitTests`) build without SOLIDWORKS.

```powershell
dotnet build                                   # everything (needs SOLIDWORKS installed)
dotnet test --project tests/SolidWorksMcp.UnitTests
dotnet test --project tests/SolidWorksMcp.LiveTests            # contract tests; live tests are skipped
$env:SWMCP_LIVE_TESTS=1; dotnet test --project tests/SolidWorksMcp.LiveTests   # live, with SOLIDWORKS running
src/SolidWorksMcp.Server/bin/Debug/net10.0-windows/solidworks-mcp.exe --check-catalog
python tools/mcp_smoke.py src/SolidWorksMcp.Server/bin/Debug/net10.0-windows/solidworks-mcp.exe [--scenario plate]
```

Live tests drive your real SOLIDWORKS. They create and close their own documents, and refuse to start while a document
has unsaved changes. Save your work first.

## Where code goes (one area = three files)
| What | File |
|---|---|
| Service (all COM work) and its DTOs | `src/SolidWorksMcp.Core/Services/<Area>Service.cs` |
| MCP tools (no COM types) | `src/SolidWorksMcp.Server/Tools/<Area>Tools.cs` |
| Live tests | `tests/SolidWorksMcp.LiveTests/<Area>LiveTests.cs` |

Templates to copy: `FeatureService.cs` + `FeatureTools.cs` + `M0LiveTests.cs`. Shared helpers live in `Core/Com`
(`SwExt`, `ComArgs`), `Core/Geometry` (`EntityResolver`, `TopoQuery`, `Topology`, `Units`, `AxisSpec`, `Xform`) and
`Core/Verify` (`Snapshot`, `FeatureVerifier`).

## Rules for Core (COM) code
1. **Everything runs through the session.** Write
   `session.RunAsync("<tool_name>", ctx => …, RunOptions.Write(doc) | RunOptions.Read(doc), ct)`.
   - `ctx.Doc` is the checked target document. `Write` enforces ownership and re-activates the document.
   - `ctx.Part()`, `ctx.Assembly()` and `ctx.Drawing()` check the document type.
   - Never touch COM objects outside `RunAsync`.
2. **No `dynamic`.** Embedded interop imports every VARIANT and IDispatch as `dynamic`, and the `ForbidDynamic` target
   turns any use of it into a build error (CS0656).
   - Cast results to `object?` and convert them with `ComArgs.Doubles/Objects/Strings/Bytes`, or use or add a typed
     wrapper in `SwExt`.
   - Never name an extension method like an interop member (for example `Normal`, `Evaluate`). The member shadows it
     and the call becomes dynamic.
3. **VARIANT flavours matter.** Use `ComArgs.Empty` (VT_EMPTY), `Nothing` (VT_DISPATCH null), `Missing` and
   `VariantArray` / `DispatchArray` as the recipe requires. Never call the in-process `I…` members (`IGetFaces`, …)
   that a contract test rejects.
4. **Units.** API values are meters and radians. Tool inputs and outputs are mm, degrees and kg. Convert only with
   `Units` (`M`, `Mm`, `Rad`, `Deg`, `Mm2`, `Mm3`, `PositiveM`, `PointM`).
5. **Entities.** Resolve references with `EntityResolver.Resolve` / `ResolveAll(ctx, doc, refs, "argName",
   EntityKind…)` and select them with `resolved.Select(doc, append, mark)`.
   - Clear the selection (`doc.ClearSelection2(true)`) before and after selecting.
   - Echo the resolutions with `resolved.Info("argName")` in the result.
6. **Verify, or it didn't happen.**
   - A feature tool takes `Snapshot.Take(doc)` before the change and calls `FeatureVerifier.Check(ctx, doc, before,
     new FeatureSpec(tool, types, VolumeRule.X), intent)` after it.
   - The intent checks are the Tier-B checks (DESIGN §3.12).
   - Return `FeatureVerifier.Result(…)`.
   - A failure rolls back automatically. Never report success without evidence.
   - Non-feature tools read back what they set: the value, the name, the file header.
7. **Errors.** Throw `SwException(ErrorCodes.X, message, hint)`. The message says what happened, and the hint says what
   to do next. Validate arguments before any COM call, and keep error text under about 1,000 characters.
8. **Preferences and files.** System preferences change only through `ctx.SystemPrefs()` and allow-listed ids in
   `Prefs/SystemPrefIds.cs`. File paths go through `ctx.Paths.ForWrite / ForRead`, which handle overwrite and backups.
9. **Recipes.** Follow `docs/solidworks-api-notes.md`. If you discover something new and verify it live, add it there
   in your own words.

## Rules for tools (Server)
- `[McpServerToolType, Toolset("<area>")]` instance class with the constructor `(SwSession session)`. Create the
  service once in a field.
- `[McpServerTool(Name = "object_verb", Title = "…", ReadOnly = …, Destructive = …, Idempotent = …, OpenWorld = false)]`.
  All four hints are always set explicitly.
- `[Description]` on the method and on **every** parameter. Start with the search keywords, then say what is verified.
  Keep it short: the server instructions carry the shared conventions.
- Parameters are camelCase with unit suffixes (`depthMm`, `angleDeg`). Optional parameters have defaults. `doc` is
  last before the `CancellationToken`.
- Return `ResultJson.Serialize(await svc.XAsync(…))`. For calls that can run longer than about 5 s, take
  `IProgress<ProgressNotificationValue> progress` and return `Output.Json(task, progress, "tool_name")`.
- Never reference SolidWorks interop types in the Server: they're embedded in Core only.
- `solidworks-mcp.exe --check-catalog` must print `CATALOG: ok`.

## Tests
- Every tool needs a positive live test with an analytic oracle where one exists (a volume, a length, a count), and at
  least one negative test (a typed error).
- Live tests derive from `LiveTest` and create their documents with `NewPartAsync`. The base class checks around each
  test that no allow-listed preference changed, no dialog stayed open and nothing leaked.
- Pure logic goes in an interop-free file and gets an offline unit test (linked into `SolidWorksMcp.UnitTests`).

## Commits
- Small commits with a clear message. Run `python tools/check_repo.py` before pushing.
