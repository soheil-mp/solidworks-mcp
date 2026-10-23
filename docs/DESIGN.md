# solidworks-mcp: design and implementation plan

Status: v0.1 in development · Tested: SOLIDWORKS 2025 SP1.2 Premium (English UI, Windows 11). Other versions: untested.
License: MIT.

This document is the plan of record. Every tool, convention and quality bar in this repository is defined here.
When the plan and the code disagree, update one of them in the same change.

Revision 2 adds the outcome of a review by six critics: agent UX, COM robustness, safety, testing, OSS distribution and
completeness. It also adds the verified SDK behaviour in [`mcp-sdk-notes.md`](mcp-sdk-notes.md). The main changes:
- a target-document ownership model;
- dialog provenance;
- a per-process preference journal with compare-and-swap restore;
- a path policy;
- honest verification with rollback;
- query-style entity references;
- object_verb tool names;
- VBA off by default;
- server instructions;
- an offline test tier.

---

## 1. Goals and non-goals

### Goals
1. **LLM agents drive SOLIDWORKS reliably** from any MCP client (Claude Code, Claude Desktop, VS Code, Cursor, …). They can
   model parts, build assemblies, make drawings, inspect geometry, export and import, and run basic FEA.
2. **Correct by construction.** The code is typed against SolidWorks' official interop and `dynamic` is forbidden. A wrong
   member or argument count is a build error.
3. **Truthful results.** Every state-changing tool checks its own effect and reports a failure as a failure. If a call
   fails, it rolls back what it created. No tool reports success for something it didn't do.
4. **It never hangs silently and never corrupts user settings.** It never modifies, closes or overwrites the user's
   documents or files unless the user allowed it explicitly.
5. **Built for agents.** Tools are focused and composable. Selection is geometric (by point, query or id). Units are mm
   and degrees. Results carry the ids and names needed for the next step, and error messages say what to do next.
6. **Easy to adopt.** One build command. No proprietary binaries in the repo. Clear documentation. A Claude Code skill
   ships with it.

### Non-goals (v1)
- PDM, Electrical, Routing, Composer, CAM, Visualize, Motion, Plastics and Flow Simulation.
- Design tables, mold tools and motion; `vba_run` remains the escape hatch. Weldment structural members, hem, miter
  and jog, component patterns and mirror, GD&T and exploded views were out of scope for v0.1 too; milestone M7
  (§7) adds them.
- Platforms other than Windows. The transport is stdio only in v1.
- An in-process add-in mode (future).

---

## 2. Lessons that drive this design

These come from live testing all 187 tools of an existing SolidWorks MCP server (about 115 were broken or silently
wrong), from building verified workarounds for every feature, and from the design review.

| # | Observed failure | Design response |
|---|---|---|
| L1 | Calls to members that don't exist, wrong argument counts and wrong enums, hidden by late binding. | C# typed against `SolidWorks.Interop.*`; named enum constants; a live test for every tool. |
| L2 | "Fake success": an empty chamfer feature, a cut that made a boss, an ignored material, a hard-coded geometry check, a close-all that did nothing. | Mandatory verification (§3.12): feature-identity diff, error codes, face count, intent checks and rollback on failure. |
| L3 | Faces, edges and vertices could not be selected. | EntityRef (§3.8) by id, by point, by query or by name; listings return pick points that resolve back. |
| L4 | Selection marks were impossible to set. | Feature tools take explicit entity parameters and set marks internally. No selection state carries over between calls. |
| L5 | One modal dialog froze every later call. | An STA dispatcher with per-call timeouts and BUSY; a watchdog with provenance; an early DIALOG_BLOCKING; the dialog tools bypass the queue. |
| L6 | Exports overwrote unrelated system preferences. | PreferenceScope (§3.10): an allow-list, a per-process journal and compare-and-swap restore. |
| L7 | The default template was CGS, so bare numbers in equations meant cm. | New documents use MMGS; inputs and outputs are mm, degrees and kg. |
| L8 | Sketch inference snapped points (r = 1 became 0.9289). | `AddToDB` is on while the server draws, and restored afterwards. |
| L9 | The VBA tool reported success when the macro crashed or never compiled. | `vba_run` wraps the code, traps errors, captures output and detects macros that didn't compile. It is off by default. |
| L10 | Screenshots were BMP files under a `.png` name. | `view_screenshot` returns an inline PNG, downscaled. |
| L11 | The bounding box covered one body; documents had null titles. | Typed interop; boxes computed over all bodies. |
| L12 | A decimal-comma locale broke number formatting. | Invariant culture everywhere; values come from the API as doubles, never from display strings. |
| L13 | 187 tools, many redundant or fake, used up client context. | About 120 focused tools with one naming rule; toolsets (`SWMCP_TOOLSETS`); a size budget for `tools/list`. |
| L14 | Tools acted on whatever document was active, and could close the user's work. | A target document, ownership classes, close by identity and no discards (§3.7). |
| L15 | Arrays and Nothing/Empty were marshalled wrongly. | `ComArgs` holds the VARIANT constants and array builders; recipes record which VARTYPE they need. |
| L16 | With embedded interop types the compiler imports every VARIANT as `dynamic`, so typed calls silently become late-bound. An extension method named like an interop member is shadowed by that member. | The `ForbidDynamic` build target removes `Microsoft.CSharp`, so any dynamic use is a build error. VARIANT results go through `SwExt` and `ComArgs`, and extension names must not clash with interop members. |
| L17 | The SDK hides every exception except `McpException`, silently ignores misspelled arguments, emits only the annotations that are explicitly set, and logs to stdout by default. | A call-tool filter maps errors and validates argument names (§3.5); all four annotations are set on every tool; the host is empty and logs to stderr. |
| L18 | Component bodies report part-space geometry. | Assembly queries map points through the component transform (§3.8). |

---

## 3. Architecture

```
MCP client (Claude Code / Desktop / …)
        │  JSON-RPC over stdio (protocol 2025-06-18 and 2026-07-28)
┌───────▼─────────────────────────────────────────────────────────────┐
│ SolidWorksMcp.Server  (net10.0-windows exe "solidworks-mcp")
│  • empty Host, stderr + file logging, invariant culture, Console.Out→stderr
│  • ServerInstructions, McpJson (snake_case enums), ToolErrorFilter, catalog check
│  • Tool classes [McpServerToolType] per area → Core services → JSON text
└───────┬─────────────────────────────────────────────────────────────┘
        │  plain DTOs (no COM types cross this boundary)
┌───────▼─────────────────────────────────────────────────────────────┐
│ SolidWorksMcp.Core  (class library; embeds the SolidWorks interop types)
│  • SwSession: attach, StaDispatcher, OleMessageFilter, DialogWatchdog, cross-process lock
│  • DocumentRegistry (target, ownership), EntityRegistry/Resolver/TopoQuery
│  • PreferenceScope + journal, PathPolicy, Verify (snapshots, predicates, rollback)
│  • Services per area: Documents, Inspect, Sketch, Features, Reference, Bodies, SheetMetal,
│    Assembly, Drawing, Export, Properties, Simulation, Automation
└───────┬─────────────────────────────────────────────────────────────┘
        │  COM (out of process) on one STA thread
┌───────▼──────────┐
│ SLDWORKS.exe     │
└──────────────────┘
```

### 3.1 Projects
| Project | Kind | Notes |
|---|---|---|
| `src/SolidWorksMcp.Core` | class library | The only assembly that references `SolidWorks.Interop.{sldworks,swconst,cosworks}`, embedded. Its public API exposes DTOs and services only. Its `ForbidDynamic` target makes any `dynamic` use a build error. |
| `src/SolidWorksMcp.Server` | console exe `solidworks-mcp` | The MCP host and tools. It depends on Core, `ModelContextProtocol` 2.3.0 and `Microsoft.Extensions.Hosting` 10.0.x. The SDK analyzer is removed by a build target. |
| `tests/SolidWorksMcp.UnitTests` | xUnit v3 | **Offline; CI.** It compiles the interop-free Core files (units, transforms, dispatcher, journal, path policy, dialog rules, JSON, filters) as linked sources. It needs no SOLIDWORKS to build or run. |
| `tests/SolidWorksMcp.LiveTests` | xUnit v3 | It needs a SOLIDWORKS installation to build. The catalog contract tests always run. Live tests run only with `SWMCP_LIVE_TESTS=1`, and then FAIL (never skip) if SOLIDWORKS can't be attached. |
| `tools/` | scripts | `mcp_smoke.py` (protocol and scenario runner), `install.ps1` (publish into a versioned folder and register), `check_repo.py` (forbidden-file guard), `dump_typelib.py` (writes local API dumps to `%LOCALAPPDATA%`, never into the repo). |

### 3.2 Threading and the COM dispatcher
- **One dedicated background STA thread** owns every SolidWorks COM object (`IsBackground = true`, so a hung COM call
  can't keep the process alive). Tool methods run on MTA thread-pool threads, several at once, and never touch COM.
- **OLE message filter** on the STA. It retries `SERVERCALL_RETRYLATER` every 150 ms, for up to 30 s, and stops as soon
  as the caller of the running operation has given up. Incoming calls are accepted.
- **Queue and timeouts.**
  - `InvokeAsync` waits at most `SWMCP_QUEUE_WAIT_S` (default 20 s) for its turn. Otherwise it is dropped (it can never
    run later) and fails with `BUSY`, naming the running operation.
  - The execution time limit counts from start: `SWMCP_TIMEOUT_S`, default **50 s**. That is below the 60 s request
    timeout of many clients. Long tools set their own limits and report progress.
  - A started call that overruns gets `TIMEOUT` and is *abandoned*: it keeps running, because COM can't be aborted, and
    its outcome goes to `lateCompletions` and `recentOps` (the last 10 operations, shown in `sw_status`). The next tool
    result carries a one-line notice about it.
- **Early `DIALOG_BLOCKING`.** The SolidWorks main window is disabled (a modal dialog is up) for more than 2.5 s
  during a call, the dialog was not there before the call started, and no rule of the call covers it. The caller then
  gets `DIALOG_BLOCKING`, with the dialog's text and buttons, instead of waiting for the timeout.
- **Cross-process lock.** Every operation holds the mutex `Local\solidworks-mcp-com`. Two Claude sessions, Claude
  Desktop and the test suite therefore never interleave calls into one SolidWorks. If the lock can't be acquired within
  the queue wait, the call fails with `BUSY`.
- **Cancellation.** The MCP CancellationToken drops a queued call. A running call is abandoned, as on timeout.
- **Bypass.** `dialog_list`, `dialog_respond` and the Win32 part of `sw_status` never enter the queue, so they work
  while the STA is blocked.
- **RCW hygiene.** After each operation that ran longer than 100 ms, the dispatcher runs `GC.Collect()` plus
  `WaitForPendingFinalizers()` on the STA, which releases transient faces, edges and features. Long-lived RCWs (the
  application, registry documents) are never force-released.
- **Progress.** Tools that can run for more than about 5 s report progress every 5 s (operation, elapsed time, visible
  dialogs). This resets idle timers in clients that honour progress.

### 3.3 Connection lifecycle
- **Attach** with `GetActiveObject("SldWorks.Application")` (P/Invoke). If SLDWORKS.exe is running but can't be
  attached (still starting, or a different elevation), the call returns `NOT_CONNECTED` with that diagnosis. **A
  second instance is never launched.**
- **Launch** happens only through `sw_connect {launch:true}`, or with `SWMCP_AUTO_LAUNCH=1` on the process's first
  attach. It uses COM activation (never `Process.Start`), so SolidWorks isn't a child of the server and survives it. It
  sets `Visible` and `UserControl` to true, then waits for `StartupProcessCompleted`.
- **On every attach:** cache the pid, the revision and the main-window HWND (`IFrame.GetHWndx64`); replay orphaned
  preference journals; reload the persisted document registry.
- **Disconnects** are `RPC_E_DISCONNECTED`, `RPC_S_SERVER_UNAVAILABLE`, `RPC_S_CALL_FAILED(_DNE)`,
  `RPC_E_SERVER_DIED(_DNE)`, `CO_E_OBJNOTCONNECTED` and `RPC_E_INVALID_OBJECT`. One of them triggers a probe of the
  application object; only a failed probe means SolidWorks is gone. A dead application drops every RCW, clears the
  registries and reports `NOT_CONNECTED`; the next call re-attaches. A stale document proxy raises `ENTITY_STALE`.
  `RPC_E_SERVERFAULT` becomes `INTERNAL`. Mutating calls are never retried across a reconnect.

### 3.4 Version support
The build compiles against the local installation's interop. A prebuilt binary that calls a member newer than the user's
SolidWorks is undefined behaviour, so v0.1 ships source builds only. Releases wait for a computed API floor (from the
embedded member list joined with the API help's availability data) and signed binaries. `sw_status` reports the
revision; untested majors get a warning.

### 3.5 Error model
- Core throws `SwException(code, message, hint)`. The Server's **ToolErrorFilter** turns any exception (from a tool, from
  argument binding or from serialization) into `CallToolResult{isError:true}` with the text `CODE: message Hint: hint`.
  The SDK's generic "An error occurred invoking" message is never used. Unknown argument names are rejected with
  suggestions, `null` for an optional argument means "use the default", and binding errors name the argument.
- Error text stays short (under 4,000 characters). Candidate lists are capped at 6.

| Code | Meaning |
|---|---|
| `NOT_CONNECTED` | SolidWorks isn't attached (not running, still starting, or at another elevation). |
| `NO_ACTIVE_DOC` | No document is open. |
| `WRONG_DOC_TYPE` | The target is the wrong document type. |
| `NOT_OWNED` | A write to a document the user didn't allow (§3.7). |
| `DOC_CLOSED` | The session's target document was closed. |
| `DIRTY` | Closing a document with unsaved changes needs `save` or `discard`. |
| `USER_BUSY` | The user is in a SolidWorks command (a PropertyManager is open). |
| `INVALID_ARGUMENT` | A parameter is invalid, missing or unknown. |
| `ENTITY_NOT_FOUND` | No entity matched; the nearest candidates are listed. |
| `ENTITY_AMBIGUOUS` | Several entities matched; candidates are listed with unique pick points. |
| `ENTITY_STALE` | An id no longer resolves, or its geometry changed. |
| `SKETCH_STATE` | A sketch is open when it must be closed, or the reverse. |
| `FEATURE_FAILED` | The API created nothing. |
| `REBUILD_ERROR` | The feature was created with an error (it was rolled back). |
| `VERIFY_FAILED` | The expected change didn't happen (it was rolled back). |
| `TIMEOUT` | The call exceeded its limit; it may still complete (see `recentOps`). |
| `BUSY` | Another operation or server holds SolidWorks; this call was dropped. |
| `DIALOG_BLOCKING` | A modal dialog blocks SolidWorks. |
| `IO_ERROR` | A file problem (including a refused overwrite). |
| `NOT_SUPPORTED` | The operation isn't supported (version, add-in, lightweight component). |
| `DISABLED` | The tool is turned off by configuration (VBA). |
| `VBA_ERROR` | The VBA code failed. |
| `INTERNAL` | An unexpected error (type and message included). |

Warnings (rebuild warnings, a re-activated target, a nearest-entity pick, a preference changed externally) don't fail a
call. They come back in `warnings[]`.

### 3.6 Dialog watchdog
- **Discovery** uses Win32 only, never COM. It finds visible top-level windows of the *attached* SLDWORKS pid with class
  `#32770`. Titles come from `GetWindowText`. Control texts come from `SendMessageTimeout(WM_GETTEXT)` with
  abort-if-hung, so a hung SolidWorks can't block `dialog_list`. The modal state is `!IsWindowEnabled(mainHwnd)`.
- **Provenance.** Each operation snapshots the visible dialogs when it starts, and **pre-existing dialogs are never
  auto-answered**. If the main window is already disabled when a COM tool is called, the call fails at once with
  `DIALOG_BLOCKING` and issues no COM call.
- **Auto-answers** come only from the running operation's rules: a title regex, a text regex and an exact button
  caption, pressed with `PostMessage(WM_COMMAND, ctrlId)`. A failed post is reported; GetLastError 5 means an
  elevation mismatch.

| Dialog | Operation | Answer |
|---|---|---|
| `Microsoft Visual Basic` run-time error | `vba_run`, `macro_run` | Capture the text, press **End**, return `VBA_ERROR`. |
| `Modify` (dimension) | dimension tools (fallback; toggle 10 is off during them) | **OK** |
| `Simulation` + "save" | `doc_close` of an owned document | **No**, or **Yes** with `save:true` |

- **Prevention beats answering.** Each operation's PreferenceScope pins the options that would open a dialog: 10
  (input dimension value), 230 (rebuild on activation), 291 and 690 (import diagnostics), 691 (3D Interconnect), 353
  (sheet-format dialog), 77 (errors at every rebuild), 70 (the STL info box). Open uses `swOpenDocOptions_Silent`.
- **`dialog_respond`** targets a handle from `dialog_list`. On dialogs the server didn't cause, it refuses buttons that
  discard, overwrite or delete (Don't Save, No on save prompts, Discard, Delete, Replace) unless `confirm:true` is given.
  Its description tells the agent to ask the user first.

### 3.7 Documents: target and ownership
- The registry tracks every document seen, with a handle (`D1`, `D2`, …) and a **class**:
  - **owned**: created, imported, or opened (and not already open) by this server. Full access.
  - **adopted**: a user document the agent was allowed to edit, through `doc_activate {doc, allowEdit:true}`. A backup
    copy is saved first under `%LOCALAPPDATA%\solidworks-mcp\backups`. Adopted documents can be edited, but they are
    never discarded and never renamed by Save As.
  - **foreign**: everything else. The server only reads these.
- **The target document.** `doc_new`, `doc_open` and `doc_activate` set it.
  - Every document tool acts on the target, or on an explicit `doc` parameter (a handle, title or path). Tools never act
    on `ActiveDoc` implicitly.
  - Before each **write**, the target is checked:
    - a foreign target gives `NOT_OWNED`;
    - a closed target gives `DOC_CLOSED`;
    - an open user command gives `USER_BUSY`;
    - a target that isn't active is re-activated, with a warning;
    - an update stamp that changed since the server's last write gives a warning ("modified outside the server").
- **Close.** It closes by identity: it refuses when two open documents share the title, and it reports any other
  document that disappeared. A dirty document needs `save:true` or `discard:true`. Foreign documents are never
  closed, and the user's documents are never discarded. Nothing is closed at shutdown.
- **Persistence.** The registry is persisted per SolidWorks session (`registry-<swPid>-<swStartTicks>.json`), so a
  restarted server re-claims what it created.
- **Undo.** `doc_undo` is allowed only when the document's update stamp still equals the one recorded after the server's
  last write, and only up to the number of server operations since then. Each mutating tool is wrapped in a named undo
  object (to verify live).

### 3.8 Entity references (selection model)
```jsonc
{ "id": "E12" }                                          // from a listing or a feature result (session-unique)
{ "at": [x, y, z] }                                      // nearest face/edge/vertex; type inferred from the parameter
{ "type": "face", "at": [x, y, z], "tolMm": 0.05 }
{ "where": { "type": "edge", "curve": "line", "parallelTo": "y" }, "expect": 4 }   // query (list parameters)
{ "name": "top" }                                        // standard plane, language-independent: front/top/right (xy/xz/yz)
{ "name": "Sketch2" }  { "name": "Fillet1" }  { "type": "body", "id": "B1" }
{ "type": "face", "component": "shaft-1", "at": [x, y, z] }   // assemblies: 'at' in that part's own coordinates
{ "type": "sketch_segment", "name": "Line3" }  { "type": "sketch_point", "at": [x, y] }  { "type": "origin" }
```
- **Ids** are session-unique and bound to their document. Each holds the persistent reference plus a signature: the
  kind, the surface or curve type, and a pick point in the entity's own coordinates. On resolution the
  persistent-reference status is mapped (Invalid or Deleted → `ENTITY_STALE`; Suppressed → a hint about the
  suppressed feature) and the signature is re-checked. A split or merged face therefore can't silently stand in for the
  original.
- **Point resolution** is a geometric search, independent of the view. It runs over all bodies, prefiltered by body box.
  - In assemblies, each component's bodies report part-space geometry, so the query point is mapped into component
    space through `Transform2`.
  - A hit within `tolMm` (default 0.01 mm) wins. Otherwise the nearest entity is accepted with a warning, if it lies
    within max(1 mm, 1% of the model diagonal) and the runner-up is at least twice as far (`strict:true` disables this).
  - Ties within max(1e-9 m, 1e-3·tol) give `ENTITY_AMBIGUOUS`, which lists the candidates with **interior pick
    points** that resolve uniquely.
- **Queries** (`where` plus `expect`) reuse the `face_list` / `edge_list` filter: type, surface or curve, normal,
  parallelTo, radius, length, near and within, extreme, feature, body, ofFace with loop, and component. A count other
  than `expect` gives an error that lists the matches.
- **Pick points.** For faces, the centroid of a large tessellation triangle, projected onto the face and kept away from
  its edges. For edges, the mid-parameter point. A pick point always resolves back to the same id (live test).
- **Selection** goes through `Select4`/`Select2` with `SelectData.Mark`, and is read back. Owned documents are cleared
  before and after each tool. In other documents the user's selection is preserved
  (`SuspendSelectionList`/`ResumeSelectionList`).

### 3.9 Sketch coordinates and directions
- SolidWorks is **Y-up**:
  - Top Plane = XZ, normal +Y;
  - Front Plane = XY, normal +Z;
  - Right Plane = YZ, normal +X.
- "Top" means +Y, and flat parts get their footprint on Top Plane.
- In sketch tools, `[x, y]` is sketch-local mm, and `[x, y, z]` is model mm projected along the sketch normal.
  `sketch_start` returns the sketch frame (origin, x axis, y axis and normal, in model space).
  - On Top Plane, the sketch point (x, y) is the model point (x, 0, −y).
  - On Right Plane, the sketch point (x, y) is the model point (0, y, −x).
- **Directions are intents, not API flags.**
  - Extrude takes `direction`: `"normal"` (the default for a boss), `"reverse"`, a model axis such as `"+y"`, or `"auto"`
    for cuts (into the material, chosen from the body box against the sketch plane).
  - Patterns take a model direction plus a spacing.
  - Results report `directionModel`.

### 3.10 Preferences
- `PreferenceScope` writes only allow-listed system ids (`Prefs/SystemPrefIds.cs`). Before each change it journals the
  original value to its process's journal (`prefs-journal-<pid>-<startTicks>.json`), then sets the value and reads it
  back.
- **On dispose** it restores in reverse order, verifies each value, and retries once, for pairs that depend on each
  other (STL quality after deviation and angle). Restore is **compare-and-swap**: a value that someone else changed in
  the meantime is left alone and reported.
- **Crash recovery.** On every attach, the server replays the journals of dead processes with the same compare-and-swap
  rule, into the same SolidWorks major version only.
- **Document-level transient state** is restored in a `finally` without a journal: display toggles 5/196/4/6,
  `AddToDB`, the view orientation and the display mode.

### 3.11 Files (PathPolicy)
- Relative paths resolve under `SWMCP_OUTPUT_DIR` (default `%USERPROFILE%\Documents\solidworks-mcp`), never under the
  working directory.
- Writes are allowed only under the user profile, `%TEMP%`, the output folder and `SWMCP_WRITE_ROOTS`.
- UNC, device, `\\?\` and ADS paths are rejected; `SWMCP_ALLOW_NETWORK_PATHS=1` allows shares.
- **Overwrites.**
  - Writing over an existing file needs `overwrite:true`.
  - A file this server didn't write in this session is first copied to `backups\<timestamp>\`, and the result reports
    `backupPath`.
  - Verification proves that the call wrote the file: the timestamp is fresh, the size is greater than 0, and the
    header magic and format details match. The result reports the path actually written.

### 3.12 Verification (the quality bar)
- **Tier A** applies to every feature-creating tool, inside the same STA operation.
  - **Before and after snapshots** of:
    - all feature IDs (`IFeature.GetID`, sub-features included);
    - per-body volume, area, box and count (`IBody2.GetMassProperties`, which also works while a sketch is open; else the
      sketch is closed first);
    - face and edge counts.
  - **Checks on the new features:**
    - the set of new feature IDs must be non-empty and match the tool's type signature (extrude → {ICE, Extrusion,
      Cut}; hole → HoleWzd + profile sketches; base flange → {SheetMetal, SMBaseFlange, FlatPattern});
    - `GetErrorCode2 == 0`;
    - solid features need `GetFaceCount() > 0`, which is what exposes the empty-chamfer fake.
  - **Volume sign**, asserted only where it is unambiguous:
    - cut and hole: ΔV < 0;
    - merged boss, revolve, sweep and loft: ΔV > 0, or one more body;
    - fillet, chamfer, draft and shell: |ΔV| > ε, or a face-count change, reported as `material: added | removed`.
- **Tier B** checks each tool's intent: the extrude cap at the requested depth, the hole diameter and depth, the pattern
  instance positions, the sketch geometry read back to 1e-6 m, the dimension value after the rebuild, the material name
  and density, and export headers and schemas.
- **Rollback.** When `FEATURE_FAILED`, `REBUILD_ERROR` or `VERIFY_FAILED` is raised after side effects, the server
  deletes the features it created (keeping the absorbed sketch) and checks that the baseline is back. The error says
  what was rolled back (`keepOnFailure:true` keeps it, for diagnosis).
- **Evidence.** Results report `verified {…}`, the created features' names and types, the `newFaces` ids with their
  geometry (up to 12), the model's new extents, and `resolved[]` (what each reference turned into).

### 3.13 Results
- Results are compact JSON text, built from camelCase DTOs with nulls omitted. Numbers are rounded: mm to 1e-6,
  derived quantities to sensible precision; NaN and Infinity never appear. Structured content is off.
- **Common envelope:**
  - `doc`: the document acted on;
  - `warnings[]`;
  - `dialogsHandled[]`;
  - `notice`: a late completion, if any.
- Lists take `limit` (default 50) and `offset`, and return `total` and `truncated`.
- `face_list`, `edge_list` and `feature_list` set `_meta["anthropic/maxResultSizeChars"]`.

### 3.14 Configuration
| Variable | Default | Effect |
|---|---|---|
| `SWMCP_TIMEOUT_S` | 50 | Execution time limit per call (long tools use more). |
| `SWMCP_QUEUE_WAIT_S` | 20 | Wait for a turn before `BUSY`. |
| `SWMCP_AUTO_LAUNCH` | off | Launch SolidWorks on the first attach if it isn't running. |
| `SWMCP_VBA` | off | `on` enables `vba_run` / `macro_run`. |
| `SWMCP_MACRO_DIRS` | any local folder | `;`-separated folders that `macro_run` may load from. |
| `SWMCP_OUTPUT_DIR` | `Documents\solidworks-mcp` | Base for relative paths. |
| `SWMCP_WRITE_ROOTS` | profile; temp; output folder | Extra allowed write roots (`;`-separated). |
| `SWMCP_ALLOW_NETWORK_PATHS` | off | Allow UNC paths. |
| `SWMCP_DATA_DIR` | `%LOCALAPPDATA%\solidworks-mcp` | Journals, registry, backups, logs. |
| `SWMCP_TOOLSETS` | all | Comma list of toolsets to expose (e.g. `session,documents,inspect,sketch,features`). |
| `SWMCP_READ_ONLY` | off | Expose only read-only tools. |
| `SWMCP_LOG_LEVEL` | Information | The file log is `logs\solidworks-mcp-<date>.log`; stderr gets Warning and above. |

---

## 4. Tool catalog

Conventions:
- **Names** are `object_verb` in snake_case. The verbs are `list` (a list), `info` (one object), `set` (update), `add`
  (create something attached) and `delete`. Creation tools of an area use the area noun (`feature_extrude`, `ref_plane`).
- **Parameters** are camelCase. Scalars carry a unit suffix (`depthMm`, `angleDeg`, `radiusMm`). Coordinate arrays
  are in mm. Enums are snake_case strings.
- **Annotations:** all four hints (`ReadOnly`, `Destructive`, `Idempotent`, `OpenWorld = false`) and a `Title` on
  every tool. Only `vba_run` and `macro_run` are `OpenWorld = true`, and they also carry
  `anthropic/requiresUserInteraction`.
- **Descriptions** start with the search keywords and say what is verified. Every parameter has a `[Description]`.
- **Common parameter:** document tools take an optional `doc` (handle, title or path).

**Status:** ✅ verified live, 🧪 implemented and untested, ⏳ planned, ⚠ API path unresolved (behind
`SWMCP_EXPERIMENTAL=1` until green).

### 4.1 Session (toolset `session`) — M0
| Tool | Purpose | Status |
|---|---|---|
| `sw_status` | Connection, revision, pid, attachability, target and active document, owned documents, UI blocked, visible dialogs, the running operation, `recentOps`, late completions, pending journals, configuration warnings and the server version. The Win32 parts work even while the STA is blocked. | ⏳ |
| `sw_connect` | Attach, or launch with `launch:true`. Reports the revision and recovered preferences. | ⏳ |
| `dialog_list` | Visible SolidWorks dialogs: handle, title, text, buttons, origin. **Bypasses the queue.** | ⏳ |
| `dialog_respond` | Press a button on a dialog by handle (destructive buttons on foreign dialogs need `confirm`). **Bypasses the queue.** | ⏳ |
| `sw_help` | Agent guide topics: conventions, entity references, workflows (plate, enclosure, assembly, drawing, edit), recovery. | ⏳ |

### 4.2 Documents (toolset `documents`) — M0/M1
| Tool | Purpose | Status |
|---|---|---|
| `doc_new` | A part, assembly or drawing (drawings in M4); units default to MMGS; optional template and name. It becomes the target. | ⏳ |
| `doc_open` | Open a native file (silent; it reports missing references), or import STEP/IGES/Parasolid/STL via `LoadFile4`. An already open file is returned as foreign with `alreadyOpen`. | ⏳ M1 |
| `doc_list` | Open documents: handle, title, path, type, dirty, access (owned / adopted / foreign), active, target, units. | ⏳ |
| `doc_activate` | Make a document active and the target; `allowEdit:true` adopts a user document (with a backup). | ⏳ |
| `doc_save` | `mode`: `inPlace`, `as` (renames; owned only) or `copy`. Overwrite policy; it warns when an older file is upgraded. | ⏳ |
| `doc_close` | Close by identity; a dirty document needs `save` or `discard`; foreign documents are never closed. | ⏳ |
| `doc_rebuild` | Rebuild (`force` for a full rebuild); returns the features with errors (`GetWhatsWrong`). | ⏳ M1 |
| `doc_undo` | Undo the last N server operations (stamp-guarded). | ⏳ M1 |
| `doc_units_set` | Set the unit system (owned documents; it refuses when equations contain unitless numbers). | ⏳ M1 |

### 4.3 Inspection (toolset `inspect`) — M0/M1
| Tool | Purpose | Status |
|---|---|---|
| `model_info` | Type, units, configurations, material, bodies (count, volume), total mass and volume, bounding box, feature count, open sketch. It returns `SKETCH_STATE` hints instead of zeros. | ⏳ |
| `feature_list` | The feature tree: name, type, error/warning, suppressed, parents and children, rollback state. Mates and components for assemblies. | ⏳ |
| `face_list` | Faces matching a filter, each with id, surface, area, pick point, normal or axis and radius, feature, body and component. | ⏳ |
| `edge_list` | Edges matching a filter, each with id, curve, length, start, end and mid points, radius, center, axis, direction and adjacent face ids. | ⏳ |
| `vertex_list` | Vertices with id and point. | ⏳ M1 |
| `body_list` | Bodies with id, name, type, volume, area, box and face count. | ⏳ M1 |
| `model_measure` | Distance, normal distance, angle and deltas between two references. | ⏳ M1 |
| `model_mass` | Mass properties of the document or one body, with labelled units. | ⏳ M1 |
| `model_check` | Body checks (`IBody2.Check3`): the faults found. | ⏳ M1 |
| `view_screenshot` | An inline PNG (downscaled) of the target document. Options: views, display mode, zoom to fit, hide planes and sketches (restored afterwards), and a file path. | ⏳ |

### 4.4 Sketch (toolset `sketch`) — M0 basic, M1 complete
| Tool | Purpose | Status |
|---|---|---|
| `sketch_start` | Open a sketch on a plane or a planar face (or a 3D sketch). Returns the sketch name, the frame and the face bounds in sketch coordinates. | ⏳ |
| `sketch_add` | Add entities in one all-or-nothing call (fields per kind below). Returns, per entity, the segment names (rectangle sides named), geometry in sketch and model coordinates, and the contour status (closed loops, open ends, regions). | ⏳ |
| `sketch_finish` | Close the sketch. Returns the entity and relation counts, the contour status, whether it is fully defined, and its dimensions. | ⏳ |
| `sketch_edit` | Re-open a sketch by name. | ⏳ M1 |
| `sketch_info` | Entities, relations, constrained status, frame. | ⏳ M1 |
| `sketch_dimension_add` | A driving dimension (linear, horizontal, vertical, radial, diameter, angular) with a value; the Modify box is prevented. Returns `D1@Sketch1`. | ⏳ M1 |
| `sketch_relation_add` | horizontal, vertical, coincident, collinear, parallel, perpendicular, tangent, concentric, equal, symmetric, midpoint, intersection, fix, pierce, merge. Verified by the relation count. | ⏳ M1 |
| `sketch_trim`, `sketch_extend`, `sketch_offset`, `sketch_fillet`, `sketch_chamfer`, `sketch_pattern`, `sketch_mirror`, `sketch_convert`, `sketch_construction_set`, `sketch_delete` | Sketch editing; `sketch_offset` takes `side: outward\|inward\|both`. | ⏳ M1 |
| `sketch_import_dxf` | Import a DXF/DWG outline into a sketch. | ⏳ M2 |

`sketch_add` entity kinds (`kind` plus fields; points are `[x,y]` sketch or `[x,y,z]` model, mm):
- `line` / `centerline` {from, to}
- `polyline` {points, closed}
- `rectangle` {corners:[p1,p2]} or {center, widthMm, heightMm}
- `circle` {center, radiusMm \| diameterMm}
- `arc` {start, mid, end} or {center, radiusMm, startDeg, endDeg} (counter-clockwise about the sketch normal)
- `slot` {from, to, widthMm}
- `polygon` {center, sides, circumradiusMm \| inradiusMm, rotationDeg}
- `ellipse` {center, majorRadiusMm, minorRadiusMm, rotationDeg}
- `spline` {points}
- `point` {at}
- `text` {at, text, heightMm}

Every kind also takes `construction` and `label`.

### 4.5 Part features (toolset `features`) — M0 basic, M1/M2 complete
| Tool | Purpose | Status |
|---|---|---|
| `feature_extrude` | Boss or cut from a sketch, with a `direction` intent. End conditions: blind, through_all, through_all_both, mid_plane, up_to_next, up_to_vertex, up_to_surface, offset_from_surface, up_to_body. Also second direction, draft, thin, merge, `normalCut`. The cap is verified. | ⏳ M0 (blind, through_all, mid_plane) |
| `feature_revolve` | Boss or cut around an axis reference (or the sketch centerline, or a cylindrical face); angle or both directions; thin. | ⏳ M1 |
| `feature_fillet` | Constant radius on edges (ids, points or a query) and/or faces; tangent propagation. Concave and convex edges both supported. | ⏳ M0 |
| `feature_chamfer` | Distance-angle, distance-distance or equal distance. | ⏳ M1 |
| `feature_shell` | Faces to remove, thickness, outward. | ⏳ M1 |
| `feature_hole` | Hole Wizard: clearance (with fit), drill, tapped (⚠, falls back to a cosmetic thread), counterbore, countersink. Standard plus size ("M5"). Points in model coordinates; the end blind or through. Verified diameter, depth and instance count. | ⏳ M1 |
| `feature_pattern_linear` / `feature_pattern_circular` | Model direction plus spacing and count, or an axis (a cylindrical face works) plus count and angle. Instance positions verified. | ⏳ M1 |
| `feature_mirror` | Features or bodies about a plane or planar face. | ⏳ M1 |
| `feature_sweep`, `feature_loft`, `feature_helix`, `feature_draft`, `feature_rib` | Advanced features. | ⏳ M2 |
| `feature_info` | Type-specific definition (end condition, depth, hole size, pattern counts) plus dimension names. | ⏳ M1 |
| `feature_edit` | The top definition edits through `ModifyDefinition`: extrude depth and end, hole size, fillet radius, pattern count. | ⏳ M2 |
| `feature_delete` | Feature only by default; it refuses when there are children unless `deleteChildren`; `dryRun`. | ⏳ M1 |
| `feature_suppress_set`, `feature_rename`, `feature_rollback` | Feature management. | ⏳ M1 |
| `dimension_list` / `dimension_set` | Lengths, angles and integer counts; `configs: this\|all\|[names]`; a batch with one rebuild; returns `previous`. | ⏳ M1 |

### 4.6 Reference geometry (toolset `reference`) — M1
`ref_plane` (offset, angle, 3 points, normal to a curve, parallel through a point, mid-plane), `ref_axis` (two planes, two
points, a cylindrical face, an edge), `ref_point` (XYZ, arc center, face center, intersection), `ref_coordsys`. Each
returns its frame.

### 4.7 Bodies and surfaces (toolset `bodies`) — M2
`body_move` (translate or rotate, copies), `body_delete` (`mode: delete|keep_only` plus an explicit list), `body_combine`
(add ⚠, subtract, common), `body_split`, `surface_extrude`, `surface_revolve`, `surface_planar`, `surface_offset`,
`surface_knit`, `surface_thicken`.

### 4.8 Sheet metal (toolset `sheetmetal`) — M2
`sheetmetal_base_flange`, `sheetmetal_edge_flange` (⚠), `sheetmetal_flatten`, `sheetmetal_export_flat`.

### 4.9 Assembly (toolset `assembly`) — M3
`asm_insert` (by path or open document; the origin at a point; rotation `{axis, angleDeg}`; fixed or floating; the
transform verified), `asm_mate_add` (coincident, concentric, parallel, perpendicular, tangent, distance, angle, lock,
hinge, slot, gear, width, symmetric; it reports constrained status and transforms), `asm_mate_list`,
`asm_component_list` (full suppression state, transform), `asm_component_set` (fixed, suppression, visibility,
configuration), `asm_component_move`, `asm_interference` ([a, b, volumeMm3, centerMm]; an explicit 0 when clear),
`asm_replace` (one instance by default; reattach mates).

### 4.10 Drawing (toolset `drawing`) — M4
`doc_new` (drawing: sheet size, orientation, scale, projection, sheet format, units), `drw_sheet_set`,
`drw_views_standard` (returns {front, top, right, iso} with names and bounds), `drw_view_add`, `drw_view_projected`,
`drw_view_section`, `drw_view_detail`, `drw_dimensions_auto` (count per view), `drw_dimension_add` (a batch, model-space
refs, auto placement, visibility checked), `drw_note_add`, `drw_bom_add` (part or assembly view; anchors or [x, y]),
`drw_balloons`, `drw_center_marks`, `drw_info` (sheet, border and title-block rectangles, views, annotation counts).

### 4.11 Export (toolset `export`) — M1
`model_export`: step (AP203/214/242), iges, parasolid, stl (binary or ascii, quality), 3mf, pdf/dwg/dxf (drawings, M4),
png/jpg, eprt. It obeys the overwrite policy and pins its preferences in a scope. It verifies freshness, the header and
the schema, and returns the path actually written.

### 4.12 Properties, materials, equations, configurations, appearance (toolset `properties`) — M1
`material_list`, `material_set` (validated against the installed databases, density read back), `prop_list`, `prop_set`,
`prop_delete`, `equation_list`, `equation_set` (keyed by the left-hand side; units required), `equation_delete`,
`config_list`, `config_add`, `config_activate`, `config_delete`, `appearance_set`. Every set tool returns `previous`.

### 4.13 Simulation (toolset `simulation`) — M5
`sim_static` (an explicit material; fixtures, loads and mesh; it reports progress; max URES, a stress band and FOS; an
optional plot image), `sim_delete` (only studies the server created, unless named on an owned document).

### 4.14 Automation (toolset `automation`) — M0, off by default
| Tool | Purpose | Status |
|---|---|---|
| `vba_run` | Runs only with `SWMCP_VBA=on`. Wraps the code in `Sub main` with error trapping and an `Out` helper. Prelude: `swApp`, `swDoc` (the target), `MM(x)`, `DEG(a)`. Non-cp1252 characters are escaped as `ChrW`. Returns output, error text, the compiled/ran flags, the features added and whether the document is dirty. | ⏳ |
| `macro_run` | `.swp`/`.swb` from `SWMCP_MACRO_DIRS`; the same dialog handling. | ⏳ M1 |

---

## 5. Implementation notes (the knowledge base)

The verified API recipes are in [`solidworks-api-notes.md`](solidworks-api-notes.md), with exact calls, argument
meanings, marks, directions and pitfalls, all checked live on SOLIDWORKS 2025. The verified SDK behaviour is in
[`mcp-sdk-notes.md`](mcp-sdk-notes.md). Always qualify members with their interface (`IFeatureManager.FeatureCut4`), and
never use the obsolete `IModelDoc2` overloads that share a name. Recipes proven in late-bound VBA must be ported with
the same VARTYPE (`ComArgs.Empty`, `Nothing`, `VariantArray`, `DispatchArray`). Never call the `I`-prefixed in-process
members (for example `IGetFaces`) from out of process.

Banned in Core (enforced by a test): `CloseAllDocuments`, `ExitApp`, `QuitDoc`, `ClearUndoList`, `ResetUntitledCount`.

---

## 6. Testing strategy

### 6.1 Offline unit tests (`tests/SolidWorksMcp.UnitTests`, CI)
These run without SOLIDWORKS. They cover:
- units, under a forced de-DE culture;
- Xform layout and inverse;
- the plane mapping table;
- the dispatcher: queueing, BUSY, TIMEOUT, late completion, cancellation, the lock;
- journal compare-and-swap and recovery;
- PathPolicy;
- dialog-rule matching and provenance;
- JSON: invariant output, no NaN;
- EntityRef and filter validation;
- axis parsing;
- the VBA wrapper text.

### 6.2 Contract tests (`tests/SolidWorksMcp.LiveTests`, always run where SOLIDWORKS is installed)
These build the host without the transport and check:
- unique snake_case names, with the full client name at most 128 characters;
- descriptions on every tool and parameter;
- all four annotations set, and OpenWorld false except for VBA;
- camelCase parameter names with unit suffixes;
- snake_case string enums;
- a `tools/list` size budget: at most 6 KB per tool and 1.5 KB per tool on average. The EntityRef schema is inlined
  wherever a tool takes geometry, so `SchemaCompactor` drops its field descriptions there (the instructions explain the
  fields once) along with `"default": null`. Clients that load every tool up front can trim the list with toolsets;
- instructions present, and under 4,096 characters;
- banned members absent (by reflection over the embedded interop members);
- no `I`-prefixed in-process members.

### 6.3 Live tests (`SWMCP_LIVE_TESTS=1`)
- **Gating.** The tests run serialized, under the cross-process lock. If the variable is set and SOLIDWORKS can't be
  attached, every test FAILS.
- **Hygiene fingerprint**, taken at the start and around every test:
  - the allow-listed system preferences, plus a sample of others;
  - the open documents, with identity, dirty state and update stamp;
  - the visible dialogs;
  - the open sketch;
  - the temporary files.

  Any difference fails the test. The tests create their own documents, including "foreign" ones through the raw API,
  and never touch pre-existing documents. The run refuses to start while a foreign document is dirty, unless
  `SWMCP_TEST_ALLOW_USER_DOCS=1`.
- **Oracles with tolerances** (planar 1e-6 relative; quadric 1e-5; swept and spline 5e-3):

| Scenario | Expected |
|---|---|
| Box 100 × 60 × 20 | V = 120 000 mm³; box 100 × 60 × 20 |
| Fillet r5 on a convex 100 mm edge | ΔV = −536.5 mm³ |
| Fillet r5 on a concave 100 mm edge (L-block) | ΔV = +536.5 mm³ |
| Chamfer 3 × 3 on a 100 mm edge | ΔV = −450 mm³ |
| Cut Ø10 through 20 from an open sketch | ΔV = −1 570.8 mm³ |
| Revolved tube r10–20, h40 | V = 37 699.1 mm³ |
| Swept r5 × 50 | V = 3 926.99 mm³ |
| Spring r1 on an r8 helix, pitch 5, 4 turns | 64π² = 631.65 ± 0.5 mm³ |
| Hole Wizard Ø6 × 10, 118° tip | a cylinder of r 3.000 and length 10.000; ΔV = −299.73 mm³ |
| Pattern of a Ø6 × 20 hole | ΔV = −565.49 mm³ per instance, at the expected positions |
| Base flange 50 × 30 × 2 | V = 3 000 mm³; flat 50 × 30 |
| Interference of two 20 mm cubes offset 10 | 4 000 mm³ |
| Cantilever 100 × 10 × 10 AISI 1020, 0.1 MPa | URES 0.0753 mm ± 3%; σvM in 28–40 MPa |
| Exports | fresh file; magic header; STEP AP214 → `AUTOMOTIVE_DESIGN` |

- **Regression ledger.** One test per old fake-success mode proves that Verify rejects it: chamfer type 0, Hole Wizard
  zeros, a partial sweep, a cut built as a boss, a wrong extrude direction, a wrong pattern direction, a snapped circle,
  an ignored material, `sgEQUAL`, both-sides offset.
- **Fault injection.**
  - A VBA runtime error: VBA_ERROR with the text.
  - A MsgBox: DIALOG_BLOCKING, with `dialog_list` answering in under 1 s.
  - A timeout: a late completion is recorded.
  - A second server process: BUSY.
  - Journal recovery after a simulated crash: restored, or left alone when changed externally.
  - A pre-existing dialog is never auto-answered.

### 6.4 MCP protocol and scenario tests (`tools/mcp_smoke.py`)
- **Protocol.** Both revisions (2025-06-18 `initialize`, and 2026-07-28 `server/discover`), `tools/list` assertions,
  error paths (`isError` with a code prefix), unknown arguments, cancellation and progress. The script exits non-zero on
  any unexpected `isError`.
- **Golden scenarios with call budgets**, as exit criteria:

| Scenario | Budget | Milestone |
|---|---|---|
| plate | ≤ 8 calls | M1 |
| enclosure | ≤ 16 calls | M1 |
| edit an existing part | ≤ 7 calls | M1 |
| assembly | ≤ 10 calls | M3 |
| drawing | ≤ 10 calls | M4 |

### 6.5 Review
Each milestone gets an adversarial review across four lenses: COM correctness, verification honesty, safety, and agent
ergonomics. Each finding is verified before it is fixed.

### 6.6 Definition of done for a tool
- A catalog entry and status.
- Descriptions on the tool and every parameter.
- All four annotations.
- Validation before any COM call.
- Error codes mapped.
- Tier-A and Tier-B verification.
- A positive and a negative live test, with an oracle.
- Contract tests green.
- The docs updated.

---

## 7. Milestones

| Milestone | Scope | Exit criteria |
|---|---|---|
| **M0 Foundation** | Host, dispatcher, lock, watchdog, session, registries (target and ownership), EntityRef (id/where/at/name), PathPolicy, PreferenceScope, Verify and rollback, logging, instructions. Tools: `sw_status`, `sw_connect`, `dialog_list`, `dialog_respond`, `doc_new`, `doc_list`, `doc_activate`, `doc_save`, `doc_close`, `model_info`, `feature_list`, `face_list`, `edge_list`, `view_screenshot`, `sketch_start`, `sketch_add`, `sketch_finish`, `feature_extrude`, `feature_fillet`, `vba_run`. | Unit, contract and live tests green; smoke test passes. |
| **M1 Part core** | All sketch tools, extrude in full, revolve, chamfer, shell, hole, patterns, mirror, reference geometry, dimensions, feature management and info, the rest of inspection, export, materials, properties, equations, configurations, appearance, `doc_open`, rebuild, undo, units, `sw_help`. | Green; plate, enclosure and edit scenarios within budget. |
| **M2 Advanced part** | Sweep, loft, helix, 3D sketch, bodies, surfaces, sheet metal, draft, rib, feature_edit, DXF import. | Green, or ⚠ behind the experimental flag. |
| **M3 Assembly** | All `asm_*`. | Green; assembly scenario within budget. |
| **M4 Drawing** | All `drw_*` and drawing export. | Green; drawing scenario within budget. |
| **M5 Simulation** | `sim_static`, `sim_delete`. | Green against the oracle. |
| **M6 Release** | README (safety, quickstart, clients, configuration, compatibility, troubleshooting, migration), CONTRIBUTING, SECURITY, THIRD-PARTY-NOTICES, the skill (written fresh), `install.ps1`, CI, the GitHub repository, tag v0.1.0. | A clean clone builds; CI is green; smoke tests pass. |
| **M7 Breadth** | Component patterns and mirror (`asm_pattern`), more mate types, exploded views, direct face editing of imported bodies, drawing annotations (balloons, GD&T, datums, surface finish, hole and revision tables), weldment structural members, hem / miter flange / jog / sketched bend. New tools stay few (several kinds per tool) to keep the catalog small. | Each tool meets §6.6; all live tests green. |

### 7.1 Parallel work breakdown
M0 fixes the conventions. After that, each area owns its own files and needs no shared registration:
- `src/SolidWorksMcp.Core/Services/<Area>Service.cs` (and DTOs)
- `src/SolidWorksMcp.Server/Tools/<Area>Tools.cs`
- `tests/SolidWorksMcp.LiveTests/<Area>Tests.cs`

Shared helpers needed by more than one area go into `Core/Com`, `Core/Geometry` or `Core/Verify` and are coordinated
at merge. The catalog test catches duplicate names. Live testing is serialized: one agent drives SolidWorks at a time.

---

## 8. Distribution and installation
- **Prerequisites:** Windows 10/11 x64, SOLIDWORKS (2025 tested), the .NET 10 SDK (pinned by `global.json`), and git.
- **Build:** `dotnet build -c Release`.
  - The interop location is read from `SOLIDWORKS_DIR`, then the registry
    (`HKLM\SOFTWARE\SolidWorks\SOLIDWORKS <year>\Setup\SolidWorks Folder`), then the default path.
  - A clear error is raised when the interop isn't found.
- **Install:** `powershell -ExecutionPolicy Bypass -File tools\install.ps1`.
  - It publishes into `%LOCALAPPDATA%\solidworks-mcp\bin\<version>`, so a running exe is never locked.
  - It registers at user scope: `claude mcp add --scope user solidworks -- "<exe>"`.
  - It prints the Claude Desktop snippet.
  - With Smart App Control or Application Control, the fallback is `dotnet <dll>`, run through the Microsoft-signed host.
- **Migration from the Python server:**
  - `claude mcp remove solidworks -s user`;
  - remove or rename the old skill;
  - check the preferences the old server corrupted (75 STEP AP, 228, 74, toggle 10).
  - Never register both servers.
- **Safety and data** (README): the agent drives the user's live SolidWorks; data (screenshots, names, properties) goes
  to the LLM provider; VBA is off by default; one agent at a time; there is no network use and no telemetry.
- **Licensing:** MIT. No Dassault binaries, typelib dumps or API-help text in the repository (enforced by `.gitignore`
  and `check_repo.py`). It includes a trademark notice and THIRD-PARTY-NOTICES.

---

## 9. Risks and mitigations
| Risk | Mitigation |
|---|---|
| COM marshalling quirks | `ComArgs` constants; record the recipe VARTYPE; a live test for every call. |
| Older SolidWorks versions | v0.1: source builds only, compiled against the user's own interop; a computed floor before binary releases. |
| Long operations | Per-tool timeouts, progress every 5 s, start-and-poll for unbounded work (launch, simulation). |
| SolidWorks crash or restart | Disconnect detection; re-attach; journal replay; registry reload. |
| VBA / macros (prompt injection) | Off by default; `requiresUserInteraction`; a macro folder allow-list. |
| Several servers on one SolidWorks | Cross-process lock; per-process journals; `sw_status` shows other servers. |
| Application Control / Smart App Control | The SDK analyzer is removed; the `dotnet <dll>` fallback; signed releases later. |
| The user working in SolidWorks at the same time | `USER_BUSY`; the target is re-activated with a warning; update-stamp warnings; the README asks for one driver. |
| Large models | Box prefilters, limits and pagination, time budgets in the resolver. |
| Non-English installs | Plane aliases resolved by geometry; view ids instead of names; file locations from preferences. |

## 10. Future work
HTTP transport, add-in mode, design tables, mold tools, motion, rendering, PDM, signed binary releases with a computed
API floor.
