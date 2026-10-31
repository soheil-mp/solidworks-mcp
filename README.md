
[![M8ven Score](https://m8ven.ai/badge/mcp/soheil-mp-solidworks-mcp-1iko97)](https://m8ven.ai/mcp/soheil-mp-solidworks-mcp-1iko97?s=readme)

# solidworks-mcp

An MCP server that lets AI agents (Claude Code, Claude Desktop and other MCP clients) drive a running SOLIDWORKS.
With its 140 tools, agents can sketch and model parts (extrudes, revolves, sweeps, lofts, holes, patterns, shells,
surfaces, sheet metal, weldment frames), edit imported bodies directly (delete, offset and move faces), build assemblies with mates (also limit, symmetric, width, hinge, gear and rack mates) and component patterns, make drawings with views, dimensions, GD&T, balloons and tables, import and export
(STEP, IGES, STL, Parasolid, PDF, DXF …), run static stress studies, and inspect, measure and screenshot the result. It
is written in C# against SolidWorks' official COM API.

> **Unofficial.** This project is not affiliated with, endorsed by or supported by Dassault Systèmes. SOLIDWORKS is a
> trademark of Dassault Systèmes SolidWorks Corporation.

> **Status: 0.1.0 pre-release.** Every tool passes the offline contract tests and the live tests against a real
> SOLIDWORKS 2025 ([live status](#live-test-status), [known issues](#known-issues)). Other SOLIDWORKS versions are
> untested; please report what you find.

Why another SolidWorks MCP server? It was built from the lessons of testing every tool of an earlier server, where
about 115 of 187 tools were broken or silently wrong.

| Area | What this server does |
|---|---|
| **Typed calls** | Calls are typed against the SolidWorks interop. Late binding is forbidden at build time, so a wrong member or argument count is a compile error. |
| **No fake success** | Every change is verified: feature identity, rebuild errors, volume and geometry changes, and file headers. A failed feature is **rolled back** and the error says so. |
| **Selection that works** | Faces, edges and vertices are found by id, by point (a geometric search, not a screen pick) or by query (`{"where":{"curve":"line","parallelTo":"y"},"expect":4}`). |
| **No hangs** | All COM calls go through one STA thread with time limits. A modal dialog makes the call return `DIALOG_BLOCKING` within seconds, and `dialog_list` / `dialog_respond` work while SOLIDWORKS is blocked. |
| **Safe by default** | It never modifies or closes **your** documents unless you allow it, never overwrites files without consent (it backs them up first), and puts back any system option it changes, even after a crash. |
| **Built for agents** | mm and degrees everywhere, and results that carry the ids and names needed for the next step. Errors look like `CODE: message Hint: what to do`. |

## Safety and data — read this first
- **It drives your live SOLIDWORKS.** Save your work before letting an agent use it, and don't work in SOLIDWORKS at
  the same time. If SOLIDWORKS crashes, unsaved work in every open document is lost.
- **Your documents are read-only for the server** unless you allow edits. Documents it creates are "owned". The agent
  can edit a document you opened only after `doc_activate {doc, allowEdit:true}`, which saves a backup copy first, and
  it never discards or closes your documents.
- **Data leaves your machine.** Screenshots, geometry, names, properties and file paths go to the model provider, which
  matters for NDAs and export control (ITAR/EAR). The server itself makes no network calls and collects no telemetry.
- **VBA is off by default.** `vba_run` executes arbitrary code with your rights. Enable it only if you need it
  (`SWMCP_VBA=on`), and never wildcard-allow it in your client.
- **Files:**
  - Relative paths go under `Documents\solidworks-mcp`.
  - Overwriting needs `overwrite:true`, and files the server didn't write are backed up to
    `%LOCALAPPDATA%\solidworks-mcp\backups`.
  - Writes outside your profile and `%TEMP%` are refused (`SWMCP_WRITE_ROOTS` adds folders), and so are network paths.
- **System options** are only changed temporarily, such as the STEP application protocol during an export. They are
  journaled under `%LOCALAPPDATA%\solidworks-mcp` and restored, even after a crash.
- **Run one agent at a time per SOLIDWORKS.** Several server processes are serialized by a lock, but they still share
  one SOLIDWORKS.

## Quickstart (build from source)
Requirements:
- Windows 10/11 x64;
- SOLIDWORKS (tested: **2025 SP1.2**; other versions are untested, so please report results);
- the [.NET 10 SDK](https://dotnet.microsoft.com/download);
- git.

```powershell
git clone https://github.com/soheil-mp/solidworks-mcp.git
cd solidworks-mcp
powershell -ExecutionPolicy Bypass -File tools\install.ps1
```

`install.ps1` builds the server, publishes it to `%LOCALAPPDATA%\solidworks-mcp\bin\<version>`, so a running copy
never blocks an update, and registers it with Claude Code at user scope.

To register it yourself instead:

```powershell
dotnet publish src\SolidWorksMcp.Server -c Release -o "$env:LOCALAPPDATA\solidworks-mcp\bin\dev"
claude mcp add --scope user solidworks -- "$env:LOCALAPPDATA\solidworks-mcp\bin\dev\solidworks-mcp.exe"
```

Claude Desktop (`%APPDATA%\Claude\claude_desktop_config.json`):

```json
{
  "mcpServers": {
    "solidworks": {
      "command": "C:\\Users\\<you>\\AppData\\Local\\solidworks-mcp\\bin\\dev\\solidworks-mcp.exe",
      "env": { "SWMCP_VBA": "off" }
    }
  }
}
```

Start SOLIDWORKS, then ask the agent something like *"Check the SOLIDWORKS status"* or *"Make a 100 × 60 × 6 mm plate
with rounded corners and two 6 mm holes, then show me a screenshot."*

### Smart App Control / Application Control
Windows Smart App Control judges each unsigned binary by its hash, and it can block a freshly built
`solidworks-mcp.exe` or `SolidWorksMcp.Core.dll` with "An Application Control policy has blocked this file" (Code
Integrity event 3077). When the exe is blocked, `tools\install.ps1` registers the DLL through the Microsoft-signed
`dotnet` host instead (`-Dotnet` goes that way from the start); by hand that is
`claude mcp add --scope user solidworks -- dotnet "<folder>\solidworks-mcp.dll"`. When a DLL is blocked, run the script
again: it publishes a new binary, which gets a new verdict. Signed releases will avoid this.

## Tools
140 tools in 15 toolsets. The full, current list with parameters is in [`docs/tools.md`](docs/tools.md), generated from
the server.

| Toolset | Tools |
|---|---|
| session (5) | `sw_status`, `sw_connect`, `sw_help`, `dialog_list`, `dialog_respond` |
| documents (10) | `doc_new` (part, assembly, drawing), `doc_open` (also imports STEP/IGES/Parasolid/STL), `doc_save`, `doc_close`, `doc_activate`, `doc_list`, `doc_rebuild`, `doc_undo`, `doc_units_set`, `doc_pack` (Pack and Go) |
| inspect (12) | `model_info`, `feature_list`, `face_list`, `edge_list`, `vertex_list`, `body_list`, `model_measure`, `model_mass`, `model_section` (section properties), `model_check`, `model_cut_list` (weldment and sheet-metal cut lists), `view_screenshot` |
| sketch (17) | `sketch_start`, `sketch_add` (lines, arcs, circles, rectangles, slots, polygons, ellipses, splines, points), `sketch_finish`, `sketch_edit`, `sketch_info`, dimensions, relations, trim, extend, offset, fillet, chamfer, pattern, mirror, convert, construction, delete |
| features (23) | extrude, revolve, sweep, loft, fillet, chamfer, shell, Hole Wizard holes, draft, rib, helix, cosmetic thread, linear/circular patterns, mirror; feature info, delete, suppress, rename, rollback; dimension list/set, tolerances and fits |
| reference (4) | `ref_plane`, `ref_axis`, `ref_point`, `ref_coordsys` |
| bodies (11) | move/copy, delete, combine, split; direct face edits (`face_edit`: delete and heal, offset, move); surfaces: extrude, revolve, planar, offset, knit, thicken |
| sheetmetal (7) | base flange, edge flange, miter flange (around corners, closed trays), hem (closed, open, teardrop, rolled), sketched bend, flatten, flat-pattern DXF/DWG |
| weldments (2) | `weldment_member`: structural members (tube, pipe, channel, angle, beam profiles) along 2D or 3D sketch lines, mitered or butt corners; `weldment_finish`: trim/extend, end caps, gussets, weld beads |
| assembly (11) | insert, mates (standard, limit, symmetric, width, hinge, gear, rack and pinion), mate list, component list/set/move, interference, replace, linear/circular patterns and mirror of components (also as new opposite-hand parts), exploded views, bill of materials as data or CSV |
| drawing (16) | sheets (setup, more sheets), standard/projected/section/detail/auxiliary/flat-pattern views, broken views and broken-out sections, model dimensions, dimensions, hole callouts, notes, BOM, center marks, datums, surface finish, tolerance frames, weld symbols, balloons, hole, cut-list and revision tables, info |
| export (2) | `model_export`: STEP (AP203/214/242), IGES, Parasolid, STL, 3MF, images, eDrawings; PDF/DWG/DXF from drawings. `model_export_all`: a release package (a STEP per model, a PDF per drawing, a flat DXF per sheet-metal part, the BOM) |
| properties (14) | materials (also custom ones), custom properties, equations, configurations, appearance |
| simulation (5) | `sim_static` (parts and assemblies; fixed, roller, symmetry and hinge fixtures; pressure, force and gravity loads; mesh, von Mises, displacement, safety factor, reactions), `sim_run` (natural frequencies, buckling load factors, steady-state thermal), `sim_plot` (result plots as images), `sim_list`, `sim_delete` |
| automation (1) | `vba_run` (off by default) |

### Live test status
Every area has live tests that drive a real SOLIDWORKS (2025 SP1.2) and check the result against an exact answer
(volumes, positions, angles, file headers, restored system options). All 381 pass:

| Area | Live tests |
|---|---|
| Core: session, documents, inspection, sketch, extrude, fillet, screenshots, dialogs | 17 |
| Sketch editing | 57 |
| Part features (revolve, chamfer, shell, Hole Wizard, patterns, mirror) | 33 |
| Feature management, dimensions and tolerances, rebuild, undo | 25 |
| Reference geometry and measurement (also section properties) | 40 |
| Materials (also custom ones), properties, equations, configurations, appearance | 15 |
| Export and import | 20 |
| Sweep, loft, helix, draft, rib, cosmetic thread | 25 |
| Bodies, surfaces, sheet metal (including hems, sketched bends, miter flanges and cut lists) | 52 |
| Direct face editing (also on an imported STEP) | 6 |
| Weldments (members, cut lists, trims, end caps, gussets, weld beads) | 10 |
| Assembly (including component patterns, mirror, opposite hands, mechanical mates, exploded views, BOMs, release packages, Pack and Go) | 29 |
| Drawing (including GD&T, weld symbols, balloons, hole callouts, flat-pattern, auxiliary and broken views, hole, cut-list and revision tables) | 35 |
| Simulation (static, gravity, guided fixtures, frequency, buckling, thermal, plots, assemblies) | 17 |

The golden scenarios in `tools/mcp_smoke.py` (drawing, plate, enclosure, edit, assembly, sheet metal) run through the
real MCP protocol within their call budgets, each with an exact oracle (volumes, interference, inserted dimensions, a
PDF, a flat blank and its DXF).

### Known issues
- **Long sessions slow SOLIDWORKS down.** After a few hundred documents in one SOLIDWORKS session (heavy agent use or
  the test suite), creating and closing documents can take 20 s to minutes. Restart SOLIDWORKS.
- **Drawing annotations in a long session.** `drw_center_marks` and `drw_dimensions_auto` sometimes stop adding
  anything after other modeling in the same session, until SOLIDWORKS is restarted (cause unknown; their hints say so).
- **A hung call, once.** In the final test run, one drawing test never returned (SOLIDWORKS stayed responsive and showed
  no dialog) until its process was stopped; it passed when run again. If a call seems stuck, check `sw_status`, then
  restart the MCP server, and SOLIDWORKS if needed.
- **STEP AP242** may come out as AP203 depending on the installation's license; the export then refuses with
  `NOT_SUPPORTED` instead of passing the file off as AP242.
- **Tapped holes** are made as their tap-drill holes, without thread geometry, unless `SWMCP_EXPERIMENTAL=1`.
- **Not available:** screw mates (SOLIDWORKS 2025 ignores their pitch set through the API, so `asm_mate_add` refuses
  them) and sheet-metal jogs. Gear mates read their ratio back reversed; the tool accepts and reports that.

A typical modelling loop:

```
doc_new → sketch_start {on:{name:"top"}} → sketch_add {entities:[{kind:"rectangle",center:[0,0],widthMm:100,heightMm:60}]}
        → feature_extrude {depthMm:6} → feature_fillet {radiusMm:5, edges:[{where:{curve:"line",parallelTo:"y"},expect:4}]}
        → view_screenshot → doc_save {mode:"as", path:"plate.sldprt"}
```

Conventions:
- Units are mm, degrees and kg.
- SOLIDWORKS is Y-up: the top plane is XZ.
- Geometry is referenced as `{id}`, `{at:[x,y,z]}`, `{name}` or `{where, expect}`.

The server sends these rules to the client as instructions, and `sw_help` explains them in detail.

## Configuration (environment variables)
| Variable | Default | Effect |
|---|---|---|
| `SWMCP_TIMEOUT_S` | `50` | Time limit per call, counted from when it starts (long tools use more and report progress). |
| `SWMCP_QUEUE_WAIT_S` | `20` | How long a call waits for its turn before `BUSY`. |
| `SWMCP_VBA` | `off` | `on` enables `vba_run`. |
| `SWMCP_AUTO_LAUNCH` | `off` | Start SOLIDWORKS on the first call if it isn't running (otherwise only `sw_connect {launch:true}` does). |
| `SWMCP_OUTPUT_DIR` | `Documents\solidworks-mcp` | Base folder for relative paths. |
| `SWMCP_WRITE_ROOTS` | your profile; `%TEMP%`; the output folder | Extra folders the server may write to (`;`-separated). |
| `SWMCP_ALLOW_NETWORK_PATHS` | `off` | Allow `\\server\share` paths. |
| `SWMCP_TOOLSETS` | all | Comma list, e.g. `session,documents,inspect,sketch,features` (for clients that cap the tool count). |
| `SWMCP_READ_ONLY` | `off` | Expose only read-only tools. |
| `SWMCP_DATA_DIR` | `%LOCALAPPDATA%\solidworks-mcp` | Journals, registry, backups, logs. |
| `SWMCP_LOG_LEVEL` | `Information` | File log level (`logs\solidworks-mcp-<date>.log`). |

## Troubleshooting
| Symptom | Fix |
|---|---|
| `NOT_CONNECTED … running but can't be reached` | SOLIDWORKS is still starting, or it runs as administrator while your MCP client doesn't (or the reverse). Run both at the same level. |
| `DIALOG_BLOCKING` | SOLIDWORKS shows a dialog. Answer it in SOLIDWORKS, or let the agent call `dialog_list` and ask you. |
| `TIMEOUT` | The call may still finish; `sw_status` shows `recentOps`. Raise `SWMCP_TIMEOUT_S` for big models. |
| `NOT_OWNED` | The agent tried to change your document. Allow it explicitly, or let it work in a new document. |
| Claude Desktop gives up after about 60 s | Keep calls short; long operations report progress. |
| Build error "SOLIDWORKS interop assemblies not found" | Set `SOLIDWORKS_DIR` to your SOLIDWORKS install folder (the one containing `api\redist`). |
| Where are the logs? | `%LOCALAPPDATA%\solidworks-mcp\logs`. Include them, and the `sw_status` output, in bug reports. |

## Migrating from the Python SolidWorks MCP server
1. Remove the old registration: `claude mcp remove solidworks -s user`, and its Claude Desktop entry. Never register
   both servers.
2. Remove or rename any skill written for the old server.
3. The old server's exports could change system options: check *Tools > Options > Export* (the STEP application
   protocol) and *Input dimension value*.

## Development
See [CONTRIBUTING.md](CONTRIBUTING.md) for the build and test commands and the rules every tool follows. The design and
its rationale are in [`docs/DESIGN.md`](docs/DESIGN.md).

## License
MIT; see [LICENSE](LICENSE). The build embeds type information from your own SOLIDWORKS installation's interop
assemblies; no Dassault Systèmes binaries are included in this repository.
