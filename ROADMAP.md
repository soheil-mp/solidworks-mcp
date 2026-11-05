# solidworks-mcp roadmap

This is the plan from today's state (0.1.0 pre-release, 161 tools) to a 1.0 that covers what an engineering team does in
SOLIDWORKS, and beyond it. It lists what is done, what is open, and every planned work item with its approach, the
exact check that proves it done, an estimate and a priority. [`docs/DESIGN.md`](docs/DESIGN.md) stays the design reference
(architecture, conventions, the definition of done); this file is the backlog.

*Audit of 2026-11-05.* **331 work items, about 4,400 hours** in 25 tracks; **about 2,160 hours** (P0 and P1) to reach
1.0. Totals are computed from the item tables ([effort by track](#effort-by-track)).

## Contents
- [How to read this](#how-to-read-this)
- [How the audit was done](#how-the-audit-was-done)
- [Part A: where we are](#part-a-where-we-are)
- [Part B: the plan](#part-b-the-plan)
  - [Release plan](#release-plan)
  - [Effort by track](#effort-by-track)
  - Tracks T0 to T24 (carry-over, reliability, releases, MCP and agent experience, sketching, part features, surfaces,
    sheet metal, weldments, assemblies, drawings, MBD and tolerancing, simulation, motion, molds, manufacturing, data
    management, interoperability, visualization, routing and electrical, design automation, architecture, security,
    testing, documentation)
- [Blocked by the API](#blocked-by-the-api)
- [Not planned](#not-planned)
- [Keeping this roadmap current](#keeping-this-roadmap-current)

## How to read this
- **Estimates** are focused engineering hours for one maintainer who knows the code base. They include what DESIGN §6.6
  demands of every tool: the implementation, live tests against SOLIDWORKS with an exact oracle, rollback on failure,
  the tool reference, the skill, the README status table and the catalog budget. API probes that fail are part of the
  estimate, so items marked 🔬 or ⚠ carry more risk than their number suggests.
- **Priority:** **P0** first in its track (the release that covers the track starts with them), **P1** needed for 1.0,
  **P2** after 1.0 or when a user asks, **P3** separate SOLIDWORKS products or research.
- **State:** ⬜ planned · 🟡 partly there (the item says what exists) · 🔬 a probe must settle the API path first ·
  ⚠ tried before and blocked so far (a new idea is named) · ✅ done.
- **IDs** (T5.3) stay stable. A finished item is marked ✅ with its date and commit, never deleted, so the history stays
  readable.
- **Done when** is the oracle the live test checks, in the project's usual style: a volume, a position, an angle, a
  value read back, a file header, a textbook formula. "Reads back" alone is not an oracle when geometry can be measured.

## How the audit was done
1. **The catalog against an engineering project.** The 161 tools were walked through the stages of a project
   (concept, detail design, validation, documentation, manufacturing, data management, hand-off). Each gap a real project
   would hit became an item.
2. **The catalog against the SOLIDWORKS API.** A reflection script listed every member of the main interfaces of the
   2025 interop and checked which ones the server's source calls. The table below shows how much of each interface is
   used. A low share does not mean low coverage everywhere: many unused members are older overloads of calls the
   server already makes (`FeatureCut` vs `FeatureCut4`). The rest were read one by one, and the distinct capabilities
   among them are items here.
3. **Feature-definition ids.** Newer features are created through `IFeatureManager.CreateDefinition(swFeatureNameID_e)`.
   Of the enum's 128 ids, 8 are used directly; the ones that name a missing capability (tab and slot, swept flange,
   corner relief, structure systems, table and dimension patterns, belt and chain, mate controller, bounding box, …)
   became items.
4. **Open items already recorded.** DESIGN §7.2 and §7.3, the README's known issues, and every UNRESOLVED line of
   [`docs/solidworks-api-notes.md`](docs/solidworks-api-notes.md).
5. **Add-in APIs on the test machine** (Simulation, Motion, Costing, Utilities, DimXpert, FeatureWorks, Design Checker,
   Inspection, Toolbox, Routing, Visualize, 3D printing, Document Manager, PDM, eDrawings), as surveyed in the API notes
   §17.
6. **The MCP specification.** Which protocol features the server uses (tools, progress, cancellation) and which it
   doesn't yet (structured output, resources, prompts, elicitation, completions, list-changed notifications, tasks, HTTP).
7. **Infrastructure.** Versions tested, distribution, CI, test tooling, documentation, security.

**API coverage of the 2025 interop** (members of the interface the source calls, of all its methods and properties):

| Interface | Used | Of | | Interface | Used | Of |
|---|---|---|---|---|---|---|
| `IFeatureManager` | 67 | 288 | | `IComponent2` | 43 | 165 |
| `IModelDoc2` | 132 | 775 | | `IConfigurationManager` | 3 | 21 |
| `IModelDocExtension` | 42 | 341 | | `IBody2` | 26 | 220 |
| `IPartDoc` | 21 | 171 | | `IFace2` | 15 | 121 |
| `IAssemblyDoc` | 24 | 206 | | `IModelView` | 11 | 84 |
| `IDrawingDoc` | 26 | 277 | | `IAnnotation` | 16 | 101 |
| `ISketchManager` | 25 | 92 | | `ISheet` | 10 | 54 |
| `ISldWorks` | 42 | 350 | | `IModeler` | 4 | 126 |
| `IView` | 44 | 466 | | `ICWLoadsAndRestraintsManager` | 12 | 42 |
| `ICWResults` | 9 | 106 | | `ICWStudy` | 14 | 61 |
| `ICWMesh` | 8 | 65 | | `ICWContactManager` | 4 | 20 |

---

# Part A: where we are

## Summary
- **161 tools** in 15 toolsets, each verifying its own effect and rolling back on failure.
- **Tests:** 425 live tests against SOLIDWORKS 2025 SP1.2 with exact oracles, 248 offline unit tests, 116 offline
  contract tests, and 6 golden scenarios through the real MCP protocol (plate, enclosure, edit, assembly, drawing, sheet
  metal), each within a call budget.
- **Tested on** SOLIDWORKS Premium 2025 SP1.2 only. Source builds against the user's own interop; no binary release yet.
- **Public** at github.com/soheil-mp/solidworks-mcp, MIT, CI on GitHub (repository guard, offline unit tests).
- **Built in** 87 commits, 2026-10-22 to 2026-10-31.

## Milestones
| Milestone | When | What it delivered |
|---|---|---|
| M0 Foundation | 2026-10-22 | STA COM dispatcher with time limits, cross-process lock, dialog watchdog, session, document registry (target and ownership), EntityRef (id, point, query, name), PathPolicy, PreferenceScope with crash journal, verification and rollback, logging, server instructions; the first 20 tools. |
| M1 Part core | 2026-10-22 | Every sketch tool, full extrude, revolve, chamfer, shell, holes, patterns, mirror, reference geometry, dimensions, feature management, inspection, export, materials, properties, equations, configurations, appearance, open, rebuild, undo, units, help. |
| M2 Advanced part | 2026-10-23 | Sweep, loft, helix, 3D sketch, bodies, surfaces, sheet metal, draft, rib, DXF import. |
| M3 Assembly | 2026-10-23 | Insert, mates, move, component states, interference, replace, BOM. |
| M4 Drawing | 2026-10-23 | Standard, projected, section and detail views, dimensions, notes, BOM tables, center marks, drawing export. |
| M5 Simulation | 2026-10-23 | `sim_static` with verified reactions, `sim_list`, `sim_delete`. |
| M6 Release | 2026-10-22/23 | README, CONTRIBUTING, SECURITY, notices, the skill, `install.ps1`, CI (10-22); the public repository (10-23). |
| M7 Breadth | 2026-10-24 | Component patterns and mirror (opposite hand), limit/symmetric/width/hinge/gear/rack mates, exploded views, direct face editing, GD&T and balloons, hole and revision tables, weldment members, hems, sketched bends, miter flanges. |
| M8 Engineering projects | 2026-10-31 (6bc6ca3) | Tolerances and fits, production drawings, BOM as data, release packages, cut lists, weldment finishing, frequency/buckling/thermal studies and plots, section properties, costing, custom materials, Pack and Go, revision compare, health report, standard hardware, top-down design, text, DXF, split lines, fillet kinds, curve/fill patterns, modelled threads. |
| M9 Tier 2 | 2026-10-31 (79fb529) | Configuration tables and display states, fill/trim/extend/untrim surfaces, projected/composite/XYZ curves, wrap/dome/indent, draft/undercut/thickness checks, cavity and core split, section screenshots, subassemblies and SpeedPak, local contacts, mesh convergence, nonlinear statics, study reports, feature recognition, templates per call. |

## What the tools cover today
| Area (toolset) | Tools | What works |
|---|---|---|
| Session (`session`) | 5 | Connect or launch, status that never hangs (documents, dialogs, recent operations), dialog list and answer, help topics and recipes. |
| Documents (`documents`) | 10 | New (MMGS whatever the template, company templates per call), open and import (STEP, IGES, Parasolid, SAT, STL) with verified bodies, activate with the edit permission and backup, save in place/as/copy with consent and backups, close, rebuild with What's Wrong, guarded undo, units, Pack and Go. |
| Inspection (`inspect`) | 15 | Bodies, faces, edges and vertices with ids and filters, feature tree, model summary, mass properties cross-checked, measure, section properties, geometry check, moldability checks (draft, undercut, wall thickness), revision compare, Costing estimate, cut lists, design review, screenshots (with 3D sections). |
| Sketch (`sketch`) | 18 | Lines, polylines, rectangles, circles, arcs, slots, polygons, ellipses, splines, points, text; 2D and 3D sketches; relations (incl. pierce and merge); driving dimensions without dialogs; trim, extend, offset, fillet, chamfer, mirror, patterns, convert entities, construction toggle, delete, edit, info, DXF/DWG import. |
| Part features (`features`) | 30 | Extrude (blind, through all, mid plane, draft), revolve (two directions), sweep (guides, twist, circular profile), loft (guides, closed), helix, Hole Wizard (drill, clearance with fits, counterbore, countersink, tapped), fillet (constant, variable, face, full round), chamfer, shell, draft, rib, linear/circular/curve/fill patterns, mirror, modelled and cosmetic threads, split lines, wrap/dome/indent, feature recognition, delete, rename, suppress, rollback, dimensions with tolerances and fits, mold split. |
| Reference (`reference`) | 5 | Planes (offset, angle, three points, parallel through a point, mid plane, normal to curve), axes, points, coordinate systems, curves (projected, composite, XYZ). |
| Bodies and surfaces (`bodies`) | 15 | Combine, split, delete/keep, move/copy, save bodies, derived and mirrored parts, direct face editing (delete and heal, offset, move), extruded/revolved/planar/offset/filled surfaces, knit, thicken, trim/extend/untrim. |
| Sheet metal (`sheetmetal`) | 7 | Base flange, edge flange, miter flange, hem, sketched bend, flatten and fold, flat-pattern DXF/DWG. |
| Weldments (`weldments`) | 2 | Structural members on 2D/3D sketches, trims, end caps, gussets, weld beads. |
| Assembly (`assembly`) | 15 | Insert, move, component states and configurations, standard and mechanical mates (limit, symmetric, width, hinge, gear, rack), mate list, interference, patterns and mirror (opposite hand), replace, exploded views, BOM as data (top, parts, indented, CSV), in-context parts, subassemblies formed and dissolved, ISO hardware and fasteners. |
| Drawing (`drawing`) | 16 | Standard, projected, section, detail, auxiliary, flat-pattern, broken and broken-out views; model and reference dimensions with tolerances and hole callouts; notes; GD&T, datums, surface finish, weld symbols, balloons; BOM, hole, cut-list and revision tables; center marks; sheets, sizes, scales, projection and formats; overview. |
| Export (`export`) | 2 | STEP (203/214/242), IGES, Parasolid, STL, 3MF, PNG, JPG, eDrawings, PDF, DWG/DXF, each checked from the file; release packages (models, drawings as PDF, flat DXFs, BOM CSV) in one call. |
| Properties (`properties`) | 15 | Materials (list, set with mass check, custom materials), custom properties (file and configuration), equations and global variables, configurations and display states, configuration tables without Excel, SpeedPak, appearance colours. |
| Simulation (`simulation`) | 5 | Static (parts and assemblies, pressure, force, gravity, fixed/roller/symmetry/hinge fixtures, local contacts, mesh convergence, nonlinear, reaction balance, Word reports), frequency, buckling, steady thermal, result plots as images, study list and delete. |
| Automation (`automation`) | 1 | `vba_run`, off by default. |

## Known limitations today
- Only SOLIDWORKS 2025 SP1.2 is tested; no prebuilt or signed binaries.
- Experimental (working, but without the full oracle set or with API variants still recorded): `body_combine` add and
  common, `body_split`, `surface_revolve`, `surface_planar`, `surface_offset`, `surface_knit {trySolid}`,
  `sheetmetal_edge_flange`; tapped holes get thread geometry only with `SWMCP_EXPERIMENTAL=1`.
- Blocked by the API so far: screw mates, sheet-metal jogs, hole series, motion study results, renders, global
  no-penetration contact, the factor-of-safety plot, Toolbox insertion (see [Blocked by the API](#blocked-by-the-api)).
- Long sessions slow SOLIDWORKS down (hundreds of documents); drawing center marks and model items can stop working in
  a long session; one test once hung without a dialog.
- No MCP resources, prompts, elicitation, completions or structured output; stdio transport only.

---

# Part B: the plan

## Release plan
Each release closes when its items are ✅, the full live suite and all golden scenarios pass on a freshly restarted
SOLIDWORKS, and the README, skill, tool reference and this file are updated.

| Release | Theme | Scope | Exit criteria |
|---|---|---|---|
| **0.2** | Trustworthy | The P0 and P1 items of T0 (M9 carry-over); the P0 items of T1; T2.11, T3.1, T22.1, T23.1 | Every tool verified (no "experimental" left); a rollback test for every state-changing tool; the regression orchestrator runs the suite unattended. |
| **0.3** | Installable | T2 (API floor, 2024 and 2026 runs, self-contained signed binaries, GitHub Releases, Claude Desktop bundle, client guides) | A user without the .NET SDK installs a signed release in one step; the README's compatibility table has three SOLIDWORKS versions. |
| **0.4** | Agent-native | T3 (resources, prompts, elicitation, completions, dynamic toolsets, annotated screenshots, session export), T23.5 first eval run | The agent benchmark has a published baseline; capped clients get a working subset. |
| **0.5** | Modelling depth | P0 and P1 of T4, T5, T6, T7, T8 | Every everyday modelling request in the benchmark is answerable without `vba_run`. |
| **0.6** | Assemblies and drawings | P0 and P1 of T9, T10, T11 | A production drawing package (views, dimensions laid out, tables, title block) comes from one prompt and passes the drawing checks. |
| **0.7** | Validation | P0 and P1 of T12 and T13 | The FEA benchmark suite (NAFEMS and textbook cases) passes within its tolerances. |
| **0.8** | Data and manufacturing | P0 and P1 of T15, T16, T17, T20 | Batch work over a folder of files; fabrication data for machined, sheet-metal and welded parts. |
| **0.9** | Hardening | The rest of T1, T21.3, T21.7, T22, T23, T24 P1 items | Soak test clean; documentation site live; agent benchmark at its target. |
| **1.0** | Stable | Every P0 and P1 item ✅ | Contracts frozen (semver from here); three SOLIDWORKS versions green; benchmark success rate published. |
| **1.x** | Breadth | P2 items, by user demand | — |
| **2.x** | Products | P3 items (add-in mode, PDM, Flow, Plastics, CAM, Routing, Electrical, Inspection) | — |

## Effort by track
| Track | Items | Hours | P0 | P1 | P2 | P3 |
|---|---|---|---|---|---|---|
| [T0 Carry-over from M9](#t0--carry-over-from-m9) | 17 | 160 | 58 | 50 | 44 | 8 |
| [T1 Reliability and robustness](#t1--reliability-and-robustness) | 19 | 246 | 56 | 160 | 30 | 0 |
| [T2 Versions, packaging and releases](#t2--versions-packaging-and-releases) | 14 | 196 | 24 | 118 | 54 | 0 |
| [T3 MCP protocol and agent experience](#t3--mcp-protocol-and-agent-experience) | 18 | 292 | 24 | 144 | 124 | 0 |
| [T4 Sketching](#t4--sketching) | 17 | 126 | 0 | 54 | 58 | 14 |
| [T5 Part features](#t5--part-features) | 35 | 340 | 44 | 84 | 172 | 40 |
| [T6 Surfaces and freeform](#t6--surfaces-and-freeform) | 11 | 102 | 0 | 34 | 46 | 22 |
| [T7 Sheet metal](#t7--sheet-metal) | 15 | 156 | 28 | 62 | 58 | 8 |
| [T8 Weldments and structure systems](#t8--weldments-and-structure-systems) | 6 | 76 | 0 | 44 | 32 | 0 |
| [T9 Assemblies](#t9--assemblies) | 29 | 322 | 0 | 174 | 148 | 0 |
| [T10 Drawings and detailing](#t10--drawings-and-detailing) | 20 | 276 | 12 | 212 | 40 | 12 |
| [T11 MBD, DimXpert and tolerancing](#t11--mbd-dimxpert-and-tolerancing) | 6 | 90 | 0 | 0 | 62 | 28 |
| [T12 Simulation (FEA)](#t12--simulation-fea) | 31 | 496 | 0 | 206 | 172 | 118 |
| [T13 Motion and mechanisms](#t13--motion-and-mechanisms) | 5 | 72 | 0 | 0 | 48 | 24 |
| [T14 Molds, plastics and casting](#t14--molds-plastics-and-casting) | 5 | 68 | 0 | 0 | 44 | 24 |
| [T15 Manufacturing](#t15--manufacturing) | 7 | 140 | 0 | 32 | 44 | 64 |
| [T16 Data management](#t16--data-management) | 8 | 154 | 0 | 48 | 46 | 60 |
| [T17 Interoperability](#t17--interoperability) | 10 | 152 | 0 | 52 | 68 | 32 |
| [T18 Visualization](#t18--visualization) | 5 | 68 | 0 | 0 | 62 | 6 |
| [T19 Routing and electrical](#t19--routing-and-electrical) | 3 | 86 | 0 | 0 | 0 | 86 |
| [T20 Engineering knowledge and design automation](#t20--engineering-knowledge-and-design-automation) | 12 | 132 | 0 | 28 | 72 | 32 |
| [T21 Architecture and performance](#t21--architecture-and-performance) | 9 | 220 | 0 | 64 | 32 | 124 |
| [T22 Security, safety and privacy](#t22--security-safety-and-privacy) | 9 | 70 | 8 | 48 | 14 | 0 |
| [T23 Testing and evaluation](#t23--testing-and-evaluation) | 11 | 270 | 16 | 184 | 70 | 0 |
| [T24 Documentation and community](#t24--documentation-and-community) | 9 | 126 | 0 | 90 | 36 | 0 |
| **Total** | **331** | **4436** | **270** | **1888** | **1576** | **702** |

---

## T0 · Carry-over from M9
The M9 items that are not done. Three of them were never attempted (fatigue, connectors, the factor-of-safety plot);
the others were tried and need the new idea named here.

| ID | Item | Approach (API) | Done when | h | P | State |
|---|---|---|---|---|---|---|
| T0.1 | Factor-of-safety plot: `sim_plot {result:"fos"}` | `CreatePlot(swsResultFactorOfSafety, criterion)` gave −1.53e6…1.48e6 earlier; read the range with `ICWResults.GetMinMaxFactorOfSafety2` / `…WithDetailSettings2` (yield limit, upper cap) instead of the plot, or `ICWPlot.SetFOSPlot` on a stress plot | On the 200 mm cantilever the plot's minimum equals yield / max von Mises within 0.1 % and the image shows the FOS legend | 6 | P0 | ⚠ |
| T0.2 | Bolt connectors: `sim_static {contacts:[{kind:"bolt", source, target, diameterMm, preloadN}]}` | `ICWLoadsAndRestraintsManager.AddBoltConnector(type, head edges, nut edges)`, `BoltConnectorBeginEdit`/`EndEdit` (shank and head diameters, material, axial preload); forces from `ICWResults.GetConnectorForces2(1, name, SI)` | Two plates with a gap, the top one pulled 1000 N along the bolt: axial force 1000 N ± 1 %; pulled 500 N across it: shear 500 N ± 1 % | 12 | P0 | 🔬 |
| T0.3 | Pin connectors `{kind:"pin", locked}` | `AddPinConnector(faces, faces)`, key and retaining ring (`IncludeTypeWithKey`, `IncludeTypeWithRetainerRing`) | A link held only by a locked pin, a force through the pin axis: pin shear equals the force ± 1 % | 8 | P0 | 🔬 |
| T0.4 | Connector results and strength checks | Per connector: shear, axial, bending, torque; `IncludeStrengthData` with tensile stress area, strength and safety factor | The bolt's safety factor equals strength × area / axial force | 4 | P1 | ⬜ |
| T0.5 | Fatigue: `sim_run {type:"fatigue", fatigue:{cycles, loading, snCurve}}` | Static study first, then `CreateNewStudy3(fatigue)`, `AddFatigueEventForConstantAmplitude`, loading type and ratio, the S-N curve on each study body (`ICWMaterial.SetFatigueSNCurve`, or the ASME curves), von Mises as the alternating stress; `GetMinMaxFatigue` for life, damage and load factor | Fully reversed cantilever with a two-point log-log S-N curve: minimum life = N(σmax) ± 2 %; damage = cycles / life; load factor = S(cycles) / σa | 16 | P0 | 🔬 |
| T0.6 | Fatigue options | Mean-stress corrections (Goodman, Gerber, Soderberg), several events with interaction, infinite-life cut-off, variable-amplitude events from a load history | R = 0 with Goodman matches the hand calculation; two events add their damage (Miner) | 10 | P2 | ⬜ |
| T0.7 | Motion study results | Earlier: the study, motor and `Calculate` work, but no reading was trustworthy. Next: `ExportCSVFile` of result plots, results after saving and reopening the study, `IMotionPlotFeatureOutput` | A crank at 60 rpm reaches 90° at 0.25 s; a slider-crank's stroke is 2r; the torque holding a lever is m·g·r | 24 | P1 | ⚠ |
| T0.8 | Motion animations to video | `IMotionStudy.SaveToAVI` with `IAVIParameter` | The file's frame count = duration × frame rate | 4 | P2 | ⬜ |
| T0.9 | `vba_run` and a suspended macro engine | A canary macro already tells a wedged engine from a compile error after a failed run. Open: the cause of the suspended state seen in a fresh session (2026-10-24), the crash on exit that followed (fault module `vbe7`), and a check before the user's code runs (canary first, the VBE's windows) | A deliberately suspended macro is found before the run and refused with the restart advice; SOLIDWORKS exits cleanly afterwards | 6 | P1 | 🟡 |
| T0.10 | Screw mates | Earlier: `RevolutionVal` keeps 1 mm/rev whatever is set. Next: `CreateMate` with `IScrewMateFeatureData` set before `CreateMate`, editing through `IMate2` after creation, or a gear-plus-distance coupling as a fallback | Turning the screw one revolution moves the nut by the pitch | 8 | P2 | ⚠ |
| T0.11 | Sheet-metal jogs | Earlier: `InsertSheetMetalJog` made nothing. Next: the jog line sketched on the face and preselected with the fixed face on its mark, both sides of the line, other offset types | The jog's offset and angle measured between the faces | 6 | P2 | ⚠ |
| T0.12 | Hole series (holes through stacked parts) | No API creates a Hole Series; emulate: one Hole Wizard hole per part at the shared axis (in context), clearance in the top parts, tapped in the last | Every part has a coaxial hole of its size; a fastener from `asm_fastener` passes without interference | 12 | P2 | ⬜ |
| T0.13 | Flex (bend, twist, taper, stretch) | `InsertFlexFeature` worked but its twist grew a bar's volume 20 %; check against SOLIDWORKS' own definition instead of volume: end positions and angles | A bent bar's end face lies at the angle about the bend axis; a twisted bar's end turned by the angle | 8 | P3 | ⚠ |
| T0.14 | Global no-penetration contact | `SetGlobalContact` reads back but the solver stays bonded. Next: set it before meshing and remesh, or emulate it with local contact sets on every touching face pair (`FindTouchingFacePairs`) | The stacked-bars test gives the same deflection as the local contact | 8 | P1 | ⚠ |
| T0.15 | Promote the experimental tools | Full oracle sets and recorded API variants for `body_combine` add/common, `body_split`, `surface_revolve`, `surface_planar`, `surface_offset`, `surface_knit {trySolid}`, `sheetmetal_edge_flange` | Each has live tests with exact volumes and areas; "experimental" disappears from the catalog | 16 | P0 | 🟡 |
| T0.16 | Tapped holes with thread geometry by default | Verify the cosmetic/modelled thread Hole Wizard options across sizes; drop the experimental flag | M3–M20 tapped holes: tap drill and thread diameters measured | 8 | P1 | 🟡 |
| T0.17 | STEP AP242 availability | Detect which application protocols the licence offers before exporting; report instead of refusing late | The tool states the protocols it can write; AP242 files verified where licensed | 4 | P2 | ⬜ |

## T1 · Reliability and robustness
The server's promise is that it never hangs, never lies and never damages the user's work. These items harden that
promise against long sessions, crashes, other locales and large models.

| ID | Item | Approach (API) | Done when | h | P | State |
|---|---|---|---|---|---|---|
| T1.1 | Long-session slowdown | Measure `doc_new`/`doc_close` latency over 500 documents; find the cause (undo history, closed-document memory, graphics); mitigate (release references, clear undo, reuse documents) and warn in `sw_status` with a restart recommendation | A latency curve before and after; the warning fires when latency triples; the suite runs 30 % faster | 16 | P0 | ⬜ |
| T1.2 | Drawing annotations stop in long sessions | Reproduce `drw_center_marks` / `drw_dimensions_auto` adding nothing after modelling; find the state that causes it | A test that ran 200 operations first still gets its center marks and model items | 12 | P1 | ⬜ |
| T1.3 | Hung calls without a dialog | A no-progress watchdog that records what the STA thread is doing, abandons the call, recreates the dispatcher and reattaches | An induced hang returns TIMEOUT and the next call works without restarting the server | 16 | P0 | ⬜ |
| T1.4 | SOLIDWORKS crash during a call | Detect the disconnect, reattach to a new instance, reload the registry, re-identify documents by path, mark lost work in `recentOps` | On a test machine where SOLIDWORKS is killed mid-call, the next call reports the crash and continues on the new instance | 16 | P1 | ⬜ |
| T1.5 | Server restart recovery | Persist document ownership and kept studies across server restarts; reconcile with the open documents at start | After a restart, owned documents stay owned and the user's stay read-only | 8 | P1 | ⬜ |
| T1.6 | Inch and other unit systems | Run every tool family on IPS, CGS and MKS documents (inputs and outputs stay mm) | 40 live tests on IPS documents give the same results as on MMGS | 16 | P1 | ⬜ |
| T1.7 | Non-English installs | A German, Japanese and Chinese SOLIDWORKS: plane names, feature type names, dialog buttons, material names, default templates | The full suite passes on one non-English install | 24 | P1 | ⬜ |
| T1.8 | Large models | Benchmarks: a 1000-component assembly, a 5000-face part; pagination for every list tool; resolver and screenshot time budgets | Each list tool within its budget; pagination complete and stable across calls | 24 | P1 | ⬜ |
| T1.9 | The user working at the same time | Review `USER_BUSY` (open PropertyManager, edit sketch, drag), update stamps, re-activation warnings, with scripted UI states | Each scripted state gives the documented error, never a silent change | 12 | P2 | ⬜ |
| T1.10 | Dialog catalogue | Every dialog a tool can raise (rebuild errors, missing references, save-as format warnings, licence prompts), each with its documented safe answer | A test per dialog: the tool reports it or answers it only where proven harmless | 12 | P1 | ⬜ |
| T1.11 | Rollback completeness | For every state-changing tool, a test that forces a failure after partial creation | The document equals its snapshot afterwards (features, bodies, sketches, properties, configurations) | 24 | P0 | ⬜ |
| T1.12 | Error message audit | Every error path has a code, a message and a hint that names the next call | Golden tests of the error texts; no bare exception text reaches the agent | 12 | P1 | ⬜ |
| T1.13 | Rebuild error explanations | Map `IFeature.GetErrorCode2` codes to readable reasons and fixes in `doc_rebuild` and `model_review` | Each of 20 provoked rebuild errors gets its reason and a fix that works | 8 | P1 | ⬜ |
| T1.14 | Missing references on open | `doc_open` of an assembly or drawing with missing files reports them (no dialog) and opens what it can | A test assembly with a renamed part opens with the missing file listed | 8 | P1 | ⬜ |
| T1.15 | Read-only and locked files | Files on read-only media, files opened by another user (PDM locks), view-only mode | Each case gives a clear error before any change | 6 | P2 | ⬜ |
| T1.16 | COM reference and memory leaks | Release policy for RCWs in loops; handle and memory counts over 1000 calls | Flat handle count after warm-up in the soak test (T23.13) | 12 | P1 | ⬜ |
| T1.17 | Several clients | Two MCP clients against one SOLIDWORKS: lock fairness, per-process journals, `sw_status` showing the other server | Interleaved calls from two clients all succeed or get BUSY, never corrupt state | 8 | P2 | ⬜ |
| T1.18 | Preference restore inventory | Every system option any tool changes is listed, journaled and restored, including after a killed process | A test per option kills the server mid-change; the next start restores it | 8 | P1 | 🟡 |
| T1.19 | Number formatting | One rounding policy (significant digits) across every result; schema test | No result carries float noise like 9.999999999 | 4 | P2 | 🟡 |

## T2 · Versions, packaging and releases
Today users need the .NET SDK and SOLIDWORKS 2025 and must build from source. 1.0 needs signed binaries that know which
SOLIDWORKS versions they support.

| ID | Item | Approach (API) | Done when | h | P | State |
|---|---|---|---|---|---|---|
| T2.1 | Computed API floor | Join the interop members each tool calls (from the built assembly) with the help's availability data; a minimum SOLIDWORKS version per tool; tools hidden or refused below it | A generated table per tool; on an older revision the catalog drops what it can't run | 16 | P0 | ⬜ |
| T2.2 | SOLIDWORKS 2024 | Full live suite on 2024 (a VM or a user machine); fix the differences | Suite green on 2024 SP5 | 24 | P1 | ⬜ |
| T2.3 | SOLIDWORKS 2022 and 2023 | Same as T2.2 | Suite green, or the floor (T2.1) explains each refused tool | 32 | P2 | ⬜ |
| T2.4 | SOLIDWORKS 2026 | New interop, deprecated members, new features worth using | Suite green on 2026 | 16 | P1 | ⬜ |
| T2.5 | Binaries per SOLIDWORKS major | Build against each major's interop in a release pipeline; the installer picks the matching build | `install` on 2024 and 2025 machines selects the right build | 16 | P1 | ⬜ |
| T2.6 | Code signing | Authenticode certificate for the exe and DLLs (Smart App Control, SmartScreen) | A fresh Windows 11 with Smart App Control runs the release without the `dotnet` fallback | 8 | P1 | ⬜ |
| T2.7 | Release pipeline | Tag → build on the maintainer machine → GitHub Release with checksums, SBOM and notes from the changelog | v0.3.0 published by one script | 12 | P1 | ⬜ |
| T2.8 | Installers | Self-contained zip, winget manifest, Scoop bucket; uninstall script; upgrades keep the data folder | Install, upgrade and uninstall on a clean VM, registration included | 16 | P2 | ⬜ |
| T2.9 | Claude Desktop extension | An `.mcpb` bundle with user settings (toolsets, VBA, folders) | Installs from the file; the settings reach the server | 8 | P1 | ⬜ |
| T2.10 | Client guides, tested | Claude Desktop, Claude Code, VS Code, Cursor, Windsurf, Codex CLI, Gemini CLI, Zed, LM Studio: config, tool-count limits, toolset presets | A real handshake and one modelling task per client, recorded in the README | 16 | P1 | ⬜ |
| T2.11 | Contract versioning | Semver for tool schemas; a snapshot test that flags breaking changes; deprecation notes | Changing a parameter's type fails the snapshot test until the changelog records it | 8 | P0 | ⬜ |
| T2.12 | Releases 0.2 to 1.0 | Release notes, tags, announcements, migration notes per release | Each release in the plan published | 12 | P1 | ⬜ |
| T2.13 | Self-contained publish | No .NET SDK or runtime needed on the user's machine | The release runs on a machine without .NET | 6 | P1 | ⬜ |
| T2.14 | Dependency upkeep | Dependabot for NuGet, MCP SDK upgrades with notes in `mcp-sdk-notes.md` | Upgrades land with the suite green | 6 | P2 | ⬜ |

## T3 · MCP protocol and agent experience
The server uses tools, progress and cancellation. The rest of the protocol and better visual feedback make agents
faster and safer.

| ID | Item | Approach (API) | Done when | h | P | State |
|---|---|---|---|---|---|---|
| T3.1 | Structured output | `outputSchema` for every tool and `structuredContent` beside the text | Schema test for every tool; the catalog budget still holds | 24 | P0 | ⬜ |
| T3.2 | Resources | Documents, feature trees, model summaries, BOMs as resources (`swmcp://doc/D1/features`); the tool reference, skill and recipes; resource templates | A client lists and reads each one; the content matches the tool results | 16 | P1 | ⬜ |
| T3.3 | Resource updates | Notify on document changes (from SOLIDWORKS events, T21.2, or polling) | A subscribed client is notified when the user edits the part | 16 | P2 | ⬜ |
| T3.4 | Prompts | Workflow prompts with arguments: model a part from a description, sheet-metal bracket, release package, FEA check, drawing from a part, clean up an imported STEP | Each prompt runs end to end in the benchmark | 12 | P1 | ⬜ |
| T3.5 | Elicitation | Ask the user through the client for destructive confirmations (discard, overwrite, edit a user document), dialog answers and ambiguous picks; fall back to errors without it | A test client answers; without elicitation the old behaviour stays | 16 | P1 | ⬜ |
| T3.6 | Completions | Document handles, feature, dimension, material, configuration and toolset names | Completion requests return the open documents' names | 8 | P2 | ⬜ |
| T3.7 | Dynamic toolsets | Start with core toolsets; a meta-tool enables more with `tools/list_changed` | Capped clients (40 tools) can still reach every tool | 16 | P1 | ⬜ |
| T3.8 | Catalog quality | Review every description for agent readability; an LLM picks the tool for 300 requests (with T23.6) | ≥ 95 % right tool on the request set | 16 | P1 | ⬜ |
| T3.9 | Long operations as tasks | MCP tasks for simulation, batch export and folder work, with polling and results later | A client polls a 10-minute study to its result | 16 | P2 | ⬜ |
| T3.10 | Cancellation inside long calls | Honour cancellation between COM steps (mesh, solve, each exported file) and clean up | Cancelling a running study leaves no study or temporary file behind | 8 | P1 | 🟡 |
| T3.11 | Streamable HTTP transport | Localhost by default, token authentication, origin checks (with T22.8) | Remote client works; unauthenticated requests are refused | 24 | P2 | ⬜ |
| T3.12 | Logging notifications | MCP `logging/setLevel` and messages | A client sees warnings while a call runs | 4 | P2 | ⬜ |
| T3.13 | Screenshots for agents | Face and edge id labels drawn on the image, a highlighted selection, a four-view composite, zoom to an entity, transparency | Labels sit at the projected pick points (checked from the view transform) | 24 | P1 | ⬜ |
| T3.14 | Compound calls | Several operations in one call with one verification and one rollback (sketch + extrude + fillet) | A failing third step leaves the part as before the call | 24 | P2 | ⬜ |
| T3.15 | Session export | The session's successful calls as a replayable script (JSON recipe, C# or VBA macro) | Replaying the script on a new part gives the same volume and features | 24 | P1 | ⬜ |
| T3.16 | Recipe library | 100 tested recipes in `sw_help` ("keyway", "countersunk hole pattern", "lip and groove", …) | Each recipe runs as a scenario snippet | 24 | P2 | ⬜ |
| T3.17 | Skills per area | Drawing, simulation, sheet metal, assembly and import skills with examples, within size limits | The benchmark's success rate does not drop when only the area skill is loaded | 12 | P1 | 🟡 |
| T3.18 | Suggested next calls | Machine-readable `suggestedCalls` in error hints | Agents follow them in the recovery benchmark | 8 | P2 | ⬜ |

## T4 · Sketching
Lines, arcs, circles, rectangles, slots, polygons, ellipses, splines, points, text, relations and dimensions work. What
is left is the rest of the sketch toolbox.

| ID | Item | Approach (API) | Done when | h | P | State |
|---|---|---|---|---|---|---|
| T4.1 | Arc and center-point slots | `CreateSketchSlot` with the arc and center types | Slot width and arc radius measured | 6 | P1 | 🟡 |
| T4.2 | Conics, parabolas, elliptical arcs, tangent arcs | `CreateConic`, `CreateParabola`, `CreateEllipticalArc`, `CreateTangentArc` | Points on the curve satisfy its equation; tangency read back | 8 | P2 | ⬜ |
| T4.3 | Equation-driven curves | `CreateEquationSpline2` (explicit and parametric) for cams, involutes, airfoils | Sampled points match the equation within 1 µm | 6 | P1 | ⬜ |
| T4.4 | Spline control | Tangency and curvature at points, handles, fit splines, editing spline points | Tangent directions read back at each point | 10 | P2 | ⬜ |
| T4.5 | 3D sketch geometry | Lines, arcs and splines in 3D sketches, 3D sketch planes, relations along X/Y/Z | A 3D polyline's segments read back at their points | 12 | P1 | 🟡 |
| T4.6 | Sketch blocks | `MakeSketchBlockFromSelected`, `InsertSketchBlockInstance`, explode, edit | Block instances move as one; exploded entities read back | 12 | P2 | ⬜ |
| T4.7 | Sketch pictures | `InsertSketchPicture2` at a scale and position, for tracing | The picture's size in mm read back | 6 | P2 | ⬜ |
| T4.8 | Fully define and repair | `FullyDefineSketch` with chosen relations and dimension schemes; report over-defining items | An under-defined sketch becomes fully defined with its shape unchanged | 8 | P1 | ⬜ |
| T4.9 | More convert entities | Intersection curves, silhouette edges, offset on surface, chain options | The new segments lie on the intersection | 8 | P2 | ⬜ |
| T4.10 | Move, rotate, scale, copy entities | `MoveOrCopy`, `RotateOrCopy`, `ScaleOrCopy` in the open sketch | Each entity's new position measured | 6 | P1 | ⬜ |
| T4.11 | Split and replace entities | `SplitOpenSegment`, `SplitClosedSegment`, `SketchReplace2` | Two segments where one was, total length kept | 6 | P2 | ⬜ |
| T4.12 | Text options | Fonts, height, along a curve, alignment, width factor | Text bounding box and baseline read back | 4 | P3 | 🟡 |
| T4.13 | More dimension types | Ordinate, path length, arc length, min/max arc conditions, driven dimensions | The value equals the geometry measured | 8 | P2 | ⬜ |
| T4.14 | Relation management | List, delete and replace relations; coradial, along X/Y/Z, on-surface | Deleting a relation frees the entity it held | 8 | P1 | 🟡 |
| T4.15 | Sketch editing of existing parts | Change a segment's geometry (endpoints, radius) in place with its relations | The edited feature rebuilds with the new size | 8 | P1 | ⬜ |
| T4.16 | Derived sketches and sketches on surfaces | Derived sketch (copy linked to the parent), 3D sketch on a face | The derived copy follows a change of its parent | 6 | P3 | ⬜ |
| T4.17 | Edit sketch patterns | `EditLinearSketchStepAndRepeat`, `EditCircularSketchStepAndRepeat` | Counts and spacing change, instances measured | 4 | P3 | ⬜ |

## T5 · Part features
Everyday features work. The gaps are end conditions, thin features, more patterns, editing feature definitions, and the
rarer feature types.

| ID | Item | Approach (API) | Done when | h | P | State |
|---|---|---|---|---|---|---|
| T5.1 | Extrude end conditions | Up to vertex, up to surface, offset from surface, up to body, up to next; start offset or start from a surface; two directions with their own depths | The end face is coincident with (or at the offset from) its target, measured | 12 | P0 | 🟡 |
| T5.2 | Contour selection | Extrude or cut chosen regions of a sketch with several loops (sketch regions and contours) | Volume = the selected regions' area × depth | 8 | P0 | ⬜ |
| T5.3 | Thin features | `FeatureExtrusionThin2`, `FeatureRevolveThin`, thin sweeps and lofts; one-sided, two-sided, mid | Wall thickness by ray cast equals t | 10 | P1 | ⬜ |
| T5.4 | Edit feature definitions | A `feature_edit` tool: change an existing extrude's end condition, a fillet's edges or radius, a pattern's count and seed, a hole's type and size, through `IFeatureData` + `ModifyDefinition` | Each edit rebuilds without errors and the geometry shows the change; a failed edit is undone | 24 | P0 | ⬜ |
| T5.5 | Reorder features | `IModelDocExtension.ReorderFeature` with the dependency check | The tree order reads back; the volume is unchanged where the order doesn't matter | 6 | P1 | ⬜ |
| T5.6 | Table-driven patterns | `InsertTableDrivenPattern2` with X/Y points from a coordinate system | Instances at the table's points | 8 | P1 | ⬜ |
| T5.7 | Sketch-driven patterns | `swFmSketchPattern` on sketch points | One instance per sketch point | 8 | P1 | ⬜ |
| T5.8 | Variable (dimension-driven) patterns | `swFmDimPattern` with per-instance dimension values | Each instance's size measured | 12 | P2 | ⬜ |
| T5.9 | Chain patterns | `swFmLocalChainPattern` along a closed path | Instances evenly along the chain, link to link | 12 | P2 | ⬜ |
| T5.10 | Pattern options | Skip instances, vary sketch, pattern bodies instead of features, instances to vary | Skipped instances absent; body patterns add whole bodies | 8 | P1 | ⬜ |
| T5.11 | Mirror bodies and faces | `InsertMirrorFeature2` with bodies | The mirrored body's volume and box reflected | 4 | P1 | ⬜ |
| T5.12 | Advanced holes | `AdvancedHole2`: near- and far-side counterbores and countersinks, stacked elements | Each element's diameter and depth measured | 16 | P2 | ⬜ |
| T5.13 | More Hole Wizard types and ends | Slot holes, pipe taps, legacy holes, up-to-surface and up-to-next ends, holes on curved faces, positions by a sketch with dimensions | Slot length and width measured; hole ends coincident with the target | 16 | P2 | ⬜ |
| T5.14 | More thread standards | Inch UNC/UNF, pipe NPT/BSP, trapezoidal, left-hand, in `feature_thread` and Hole Wizard | Pitch and major diameter measured against the standard | 12 | P1 | ⬜ |
| T5.15 | Fillet completeness | Setback corners, asymmetric and conic (curvature continuous) profiles, chord width, hold line, several radii in one feature, FilletXpert fixes | Radii and setback distances measured | 16 | P1 | 🟡 |
| T5.16 | Chamfer completeness | Vertex chamfers, offset-face and face-face chamfers, several distances | Leg lengths measured | 8 | P2 | 🟡 |
| T5.17 | Shell completeness | Several thicknesses, outward shells, failure diagnosis (the smallest radius blocking it) | Each wall measured; a failing shell names the face | 8 | P1 | 🟡 |
| T5.18 | Draft kinds | Parting-line and step drafts; DraftXpert fixes from `model_check {kind:draft}` | Every listed face drafted by the angle | 8 | P2 | 🟡 |
| T5.19 | Intersect feature | Solids from intersecting surfaces and solids (fluid volumes, molds) | The kept region's volume | 8 | P2 | ⬜ |
| T5.20 | Scale feature | `InsertScale` uniform and per axis, about origin, centroid or a coordinate system | Box scaled by the factors | 4 | P2 | 🟡 |
| T5.21 | Replace and delete faces | Replace face with a surface; delete hole in a surface | The face now lies on the surface | 6 | P2 | ⬜ |
| T5.22 | Library features | Insert `.sldlfp` with location references; make one from features | Inserted features at the references, volume added | 12 | P2 | ⬜ |
| T5.23 | Bounding box feature | `InsertGlobalBoundingBox` and its stock-size properties | Its size equals the body's box (or the minimal box) | 4 | P1 | ⬜ |
| T5.24 | Feature tree organisation | Folders (`InsertFeatureTreeFolder2`), comments, the freeze bar (`EditFreeze2`), feature lock | Folder contents and frozen state read back | 8 | P2 | ⬜ |
| T5.25 | Equation completeness | Functions (`IIF`, `sin`), suppression by equation, units in equations, configuration-specific values | Driven dimensions and suppression states read back per configuration | 6 | P2 | 🟡 |
| T5.26 | Dimension management | Rename dimensions, driving/driven toggle, mark for drawing, linked values | Names and states read back | 6 | P2 | 🟡 |
| T5.27 | Body folders and names | Rename bodies, body folders, local operations on chosen bodies (feature scope) | Body names read back; scoped features touch only their bodies | 4 | P2 | ⬜ |
| T5.28 | Rib options | Draft, natural vs linear extension, several sketch segments | Rib faces' draft measured | 4 | P2 | 🟡 |
| T5.29 | Solid sweeps | Tool-body sweeps (milling paths) | Removed volume equals the swept tool's | 8 | P2 | ⬜ |
| T5.30 | Loft options | Centerline lofts, start/end tangency and curvature, connectors | Tangency read back at the profiles | 8 | P2 | 🟡 |
| T5.31 | Variable-pitch helix | `InsertVariablePitchHelix`, `AddVariablePitchHelixSegment` (springs with closed ends) | Height per region equals pitch × revolutions | 6 | P2 | ⬜ |
| T5.32 | Defeature | Simplify a part or assembly (remove holes, fillets, internal detail) for sharing | Removed features listed; the outer envelope unchanged | 12 | P2 | ⬜ |
| T5.33 | Freeform and deform | `InsertFreeform2`, deform features | The pushed point moved by the distance | 24 | P3 | ⬜ |
| T5.34 | Macro features | `InsertMacroFeature3`: parametric features defined by the server | A macro feature rebuilds with the part | 16 | P3 | ⬜ |
| T5.35 | Center of mass and mate references | Center-of-mass point; mate references for smart insertion (with T9.3) | The point at the computed center; mates found from the references | 8 | P2 | ⬜ |

## T6 · Surfaces and freeform
Extrude, revolve, planar, offset, fill, knit, thicken, trim, extend and untrim work. Open: the swept and lofted
surfaces, mid-surfaces, flattening, quality analysis and repair of imported geometry.

| ID | Item | Approach (API) | Done when | h | P | State |
|---|---|---|---|---|---|---|
| T6.1 | Swept surfaces | `InsertSweepSurface3` | Area = profile length × path length for a straight sweep | 6 | P1 | ⬜ |
| T6.2 | Lofted surfaces | `InsertLoftRefSurface2` with guides | Its edges pass through every profile | 6 | P1 | ⬜ |
| T6.3 | Boundary surfaces | No API inserts one; probe `CreateDefinition` and `IBoundaryBossFeatureData` for surfaces | A surface through its curves in both directions | 8 | P2 | 🔬 |
| T6.4 | Ruled surfaces | `InsertRuledSurfaceFromEdge2` (tangent, normal, tapered, sweep) | Width and angle measured from the edge | 6 | P2 | ⬜ |
| T6.5 | Mid-surfaces | `InsertMidSurface` from face pairs (for shell meshes) | The mid faces at half the thickness | 6 | P1 | ⬜ |
| T6.6 | Flatten surfaces | `InsertFlattenSurface2` (developed blanks of curved panels) | A cylinder segment flattens to its developed area | 8 | P2 | ⬜ |
| T6.7 | Cut and thicken-cut with surfaces | `InsertCutSurface`, `FeatureCutThicken` | Removed volume equals the side of the surface | 8 | P2 | ⬜ |
| T6.8 | Net blends and freeform surfaces | `InsertNetBlend2` and its curve data | Surface through every curve of the network | 16 | P3 | ⬜ |
| T6.9 | Surface quality analysis | Continuity between faces (G0/G1/G2 by sampling normals and curvature), deviation analysis, curvature combs and zebra stripes as images | A G1 blend reports G1; a sharp edge reports G0 with its angle | 16 | P2 | ⬜ |
| T6.10 | Repair imported geometry | Import diagnostics: heal faces, close gaps, knit, report what was fixed | A deliberately broken STEP comes back as a valid solid with the volume of the original | 16 | P1 | ⬜ |
| T6.11 | Face and intersection curves | Face curves (iso lines), intersection split lines | Curves lie on the faces | 6 | P3 | ⬜ |

## T7 · Sheet metal
Base, edge and miter flanges, hems, sketched bends, flattening and flat-pattern DXF work. Real fabrication needs the
corners, conversions, forming tools and bend tables.

| ID | Item | Approach (API) | Done when | h | P | State |
|---|---|---|---|---|---|---|
| T7.1 | Edge flange, all options | Length references (outer/inner virtual sharp, tangent), positions (material inside/outside, bend outside), custom relief, angle, edited profile; drop "experimental" | Flange height to the reference measured for each option | 16 | P0 | 🟡 |
| T7.2 | Convert solids to sheet metal | `InsertConvertToSheetMetal2` from a solid or imported part, rips at corners | The flat pattern's area equals the faces' area | 12 | P0 | ⬜ |
| T7.3 | Rips | Rip edges of a shelled box before conversion, gap and direction | Gap measured; flat pattern opens | 6 | P1 | ⬜ |
| T7.4 | Lofted bends | `InsertSheetMetalLoftedBend2` (square-to-round transitions) with a flat pattern | The blank area equals the surface area | 12 | P1 | ⬜ |
| T7.5 | Corners | Closed corners, break corners and corner trims (`InsertSheetMetalCornerTrim`), welded corners, corner reliefs (`swFmCornerRelief`) | Gaps, chamfers and relief sizes measured | 12 | P1 | ⬜ |
| T7.6 | Unfold and fold features | Cuts across bends: unfold, cut, fold back | The cut follows the bend in the folded state | 8 | P1 | ⬜ |
| T7.7 | Bend allowance control | Gauge tables, bend tables, K-factor, bend allowance and bend deduction, per-bend overrides | The flat length equals the formula for each method | 12 | P1 | 🟡 |
| T7.8 | Flat-pattern options and data | Grain direction, fixed face, corner treatment, bend lines and notes in the DXF on layers; cut length, bend count and directions per bend | DXF layers and bend list read back; cut length equals the blank's perimeter | 12 | P1 | 🟡 |
| T7.9 | Tab and slot | `swFmTabAndSlot` between two sheet-metal bodies | Tab and slot sizes and positions measured | 10 | P2 | ⬜ |
| T7.10 | Swept flanges | `swFmSweptFlange` along a path | Flange length along the path | 8 | P2 | ⬜ |
| T7.11 | Sheet-metal gussets | `InsertSheetMetalGussetFeature3` | Gusset depth and width measured; flattens | 6 | P2 | ⬜ |
| T7.12 | Normal cuts | `swFmNormalCut` on bent parts | Cut walls normal to the sheet | 6 | P2 | ⬜ |
| T7.13 | Forming tools | Insert a form tool (louvers, lances, embosses), make one (`CreateFormTool2`) | Volume and position of each instance; the flat pattern marks them | 16 | P2 | ⬜ |
| T7.14 | Multibody sheet metal | Several base flanges in one part, each with its cut-list item | One cut-list item per body with its own flat blank | 12 | P2 | ⬜ |
| T7.15 | Cross breaks and vents | `InsertCrossBreak`, vents | Read back; vent open area measured | 8 | P3 | ⬜ |

## T8 · Weldments and structure systems
Members, trims, end caps, gussets, beads and cut lists work. Open: profile libraries, member options, structure systems
and weldment drawings.

| ID | Item | Approach (API) | Done when | h | P | State |
|---|---|---|---|---|---|---|
| T8.1 | Profile libraries and custom profiles | Every installed standard and size listed; save a sketch as a custom profile in the weldment profile folder | A member's section area equals the profile's | 12 | P1 | 🟡 |
| T8.2 | Member options | Profile rotation, mirroring, pierce point, merged arcs, per-corner treatments, gaps | Each option measured on the member's end faces | 12 | P1 | 🟡 |
| T8.3 | Structure systems | `swFmStrctSys*` (2022 and later): primary and secondary members from points and planes, corner management groups | Members where the system puts them; corner trims measured | 24 | P2 | ⬜ |
| T8.4 | Sub-weldments and cut-list editing | Sub-weldment folders, custom cut-list properties per item | Properties read back per item | 8 | P1 | ⬜ |
| T8.5 | Weld bead completeness | Intermittent and staggered beads, sizes in the cut list, total weld length | Weld length equals the edges' length | 8 | P2 | 🟡 |
| T8.6 | Weldment drawings | Member views per body, cut-list balloons, length tables | One balloon per item; lengths in the table match the cut list | 12 | P1 | 🟡 |

## T9 · Assemblies
Inserting, mating, patterns, BOMs, exploded views, replacing, subassemblies and fasteners work. Open: the rest of the
mates, mate editing and diagnosis, assembly features, large-assembly tools and references.

| ID | Item | Approach (API) | Done when | h | P | State |
|---|---|---|---|---|---|---|
| T9.1 | Cam mates | Cam follower mates | The follower touches the cam at three rotations | 6 | P1 | ⬜ |
| T9.2 | Slot mates | Slot mates (free, centred, at distance, along slot percent) | The pin's position along the slot measured | 6 | P1 | ⬜ |
| T9.3 | Path mates | Path mates with pitch/yaw control | The point stays on the path at three positions | 6 | P2 | ⬜ |
| T9.4 | Linear coupler mates | Linear/linear coupler with ratio | Moving one component moves the other by the ratio | 6 | P2 | ⬜ |
| T9.5 | Universal joint mates | Universal joint | The output angle follows the joint formula | 6 | P2 | ⬜ |
| T9.6 | Profile center mates | Profile center (rectangles and circles) | Centres coincide, offset measured | 6 | P2 | ⬜ |
| T9.7 | Edit mates | Change a mate's type, value, entities, alignment; suppress and unsuppress (`EditMate4`, `IMate2`) | The new value solves; components move accordingly | 10 | P1 | ⬜ |
| T9.8 | Mate diagnosis | Over-defined and conflicting mates, redundant ones, remaining degrees of freedom per component | A test assembly's conflicting mate is named; DOF counts match the mates | 16 | P1 | 🟡 |
| T9.9 | Kinematic sweeps | Step a driving mate's dimension and record positions (a mechanism check without the Motion add-in), with collisions per step | A slider-crank's stroke equals 2r; a collision at a known angle is found | 16 | P1 | ⬜ |
| T9.10 | Mate controller positions | `swFmMateController` positions for mechanisms | Each saved position reproduces its mate values | 8 | P2 | ⬜ |
| T9.11 | Pattern-driven component patterns | `InsertDerivedPattern` (components follow a feature pattern) | One component per hole of the pattern | 8 | P1 | ⬜ |
| T9.12 | Sketch- and curve-driven component patterns | Components at sketch points or along a curve | Positions measured | 12 | P2 | ⬜ |
| T9.13 | Belt and chain | `swFmBeltAndChain` around pulleys | Belt length equals the tangent-arc formula | 8 | P2 | ⬜ |
| T9.14 | Assembly features | Cuts and Hole Wizard holes in an assembly with a feature scope; assembly weld beads | Volume removed only from the scoped components | 16 | P1 | ⬜ |
| T9.15 | Cavity, join, envelope | `InsertCavity4`, `InsertJoin2`, `InsertEnvelope` | Cavity volume equals the tool's overlap; envelopes stay out of the BOM | 12 | P2 | ⬜ |
| T9.16 | Flexible subassemblies | Solve as flexible or rigid | A flexible subassembly moves with its mates | 6 | P2 | ⬜ |
| T9.17 | Copy with mates | Copy a component with its mates to new references | The copy's mates solve at the new place | 8 | P2 | ⬜ |
| T9.18 | Clearance and hole alignment | Clearance verification between components; hole alignment across parts; interference options (coincidence, fasteners, hidden bodies) | A 0.4 mm gap is found under a 0.5 mm minimum; misaligned holes listed | 16 | P1 | 🟡 |
| T9.19 | Large-assembly modes | Large design review, selective open, quick view; benchmark opening 2000 components | Open time and memory recorded; tools work in the lighter modes | 24 | P1 | 🟡 |
| T9.20 | Exploded view completeness | Rotation steps, radial explodes, explode lines (`InsertExplodeLineSketch`), auto explode, editing steps, several views per configuration | Component positions after each step | 16 | P1 | 🟡 |
| T9.21 | External references | List (`ListExternalFileReferences`), lock, break, repair missing references through search paths | A broken reference repaired; the list matches the files | 16 | P1 | ⬜ |
| T9.22 | In-context editing | Edit a part in context with references to other components; reference status | The in-context feature follows a moved mating face | 12 | P2 | 🟡 |
| T9.23 | Layout-driven design | Layout sketch with blocks, then parts from blocks | Each part's outline equals its block | 16 | P2 | ⬜ |
| T9.24 | Toolbox parts | No insertion API: insert the Toolbox master files from `SOLIDWORKS Data\browser` and set the size configuration | The inserted screw's size and length measured | 16 | P2 | 🔬 |
| T9.25 | More standard hardware | DIN and ANSI inch screws, nuts and washers; pins, retaining rings, keys, O-rings, more bearing series, each from its standard's table | Every size's volume equals the table's geometry | 24 | P1 | 🟡 |
| T9.26 | Component renaming and virtual parts | Rename in the assembly, make virtual parts external and back | Names and files read back | 8 | P2 | 🟡 |
| T9.27 | Move components between subassemblies | Reorganize into an existing subassembly | Quantities unchanged; the component under its new parent | 6 | P2 | 🟡 |
| T9.28 | BOM completeness | Columns by configuration, Excel output (`SaveAsExcel`), part-number sources, BOM of chosen configurations | Excel cells match the data BOM | 8 | P2 | 🟡 |
| T9.29 | Isolate and component appearance | Isolate, transparency, colour per component | Appearance and visibility read back | 4 | P2 | ⬜ |

## T10 · Drawings and detailing
Views, dimensions, notes, GD&T, balloons and tables work, one at a time. A production drawing also needs the remaining
dimension and annotation types, laid-out dimensions, editable title blocks, and checks.

| ID | Item | Approach (API) | Done when | h | P | State |
|---|---|---|---|---|---|---|
| T10.1 | Ordinate, baseline and chain dimensions | `InsertHorizontalOrdinate`, `InsertVerticalOrdinate`, `InsertBaseDim`, `InsertChainDim` | Each value equals the geometry measured from the datum | 12 | P0 | ⬜ |
| T10.2 | Angular running, chamfer and arc-length dimensions | `InsertAngularRunningDim`, `AddChamferDim`, arc length | Values equal the geometry | 8 | P1 | ⬜ |
| T10.3 | Dimension layout | Align and space dimensions, avoid overlaps (annotation boxes), move dimensions out of views | No two annotations overlap on a test drawing; spacing within the standard | 24 | P1 | ⬜ |
| T10.4 | Edit and delete annotations | List annotations with positions; move, edit text or value, delete | Each change reads back | 12 | P1 | 🟡 |
| T10.5 | Notes with leaders and property links | Leaders, multi-jog leaders (`InsertMultiJogLeader3`), notes linked to `$PRP` properties | The note shows the property's value; the leader ends on the edge | 8 | P1 | 🟡 |
| T10.6 | Center lines and center mark options | `InsertCenterLine2`, center mark patterns and styles | One center line per cylinder seen from the side | 6 | P1 | 🟡 |
| T10.7 | Revision clouds and symbols, datum targets, thread callouts, dowel symbols | `InsertRevisionCloud`, `InsertRevisionSymbol`, `InsertThreadCallout`, `InsertDowelSymbol`, datum targets | Each annotation read back with its attachment | 14 | P2 | ⬜ |
| T10.8 | Balloon options | Auto-balloon layouts, stacked balloons, balloon styles and quantities | Every component ballooned once; stacks read back | 6 | P1 | 🟡 |
| T10.9 | More view types | Relative views (`CreateRelativeView`), alternate position, crop, removed and aligned sections, half sections, offset sections with several segments, assembly sections with excluded components | Each view's type, orientation and cut read back | 32 | P1 | 🟡 |
| T10.10 | View display and alignment | Hidden lines (visible, removed), shaded, tangent edges; view rotation; break and restore alignment; named-view and exploded views | Display modes read back; exploded views show the explode | 12 | P1 | 🟡 |
| T10.11 | More tables | General tables, bend tables, punch tables, weld tables, BOM options (indented, parts only, columns) | Rows and columns read back against the model data | 24 | P1 | 🟡 |
| T10.12 | Title blocks and templates | Title block fields linked to custom properties; make a company sheet format and drawing template; sheet formats from DWG | Fields show the properties; a new drawing from the template carries them | 16 | P1 | 🟡 |
| T10.13 | Layers and line styles | `CreateLayer2`, `SetCurrentLayer`, line styles and weights; layer mapping in DWG export | Entities on their layers in the DWG | 8 | P2 | ⬜ |
| T10.14 | Blocks and custom symbols | `MakeBlockDefinition`, `InsertBlock` | Block instances read back | 8 | P2 | ⬜ |
| T10.15 | Drawing checks | Dangling and unattached annotations, duplicate dimensions, features without dimensions, title block fields empty | A seeded drawing's five defects are all found | 24 | P1 | ⬜ |
| T10.16 | Production drawing in one call | Views, model dimensions, center marks and lines, title block fields, BOM and balloons, notes, laid out, then checked (T10.15) | The drawing scenario's oracle passes and the checks find nothing | 40 | P1 | ⬜ |
| T10.17 | Replace the model of views | `ReplaceViewModel` for revisions and families | Views show the new model; dimensions reattached or listed | 6 | P2 | ⬜ |
| T10.18 | Area hatch and section hatch by material | Hatches per material in section views | Hatch pattern read back per body | 4 | P3 | ⬜ |
| T10.19 | Drawing compare | Two revisions of a drawing: changed dimensions and notes | The changed values listed | 8 | P3 | ⬜ |
| T10.20 | Drawing images for review | A PNG of a sheet at a chosen resolution, for the agent to check layout | Image size and sheet extent agree | 4 | P2 | ⬜ |

## T11 · MBD, DimXpert and tolerancing
Tolerances and fits on dimensions and GD&T on drawings work. Model-based definition and stack-up analysis are open.

| ID | Item | Approach (API) | Done when | h | P | State |
|---|---|---|---|---|---|---|
| T11.1 | DimXpert schemes | `swdimxpert` API: auto-dimension scheme, datums, tolerances on features | Recognised features match the Hole Wizard holes; datums read back | 24 | P2 | 🔬 |
| T11.2 | 3D PMI | Annotation views, GD&T and dimensions in the model | Annotations read back in the model's views | 16 | P2 | ⬜ |
| T11.3 | 3D PDF and AP242 with PMI | MBD 3D PDF with a template; STEP AP242 with semantic PMI | The files carry the annotations (structure checked) | 12 | P3 | ⬜ |
| T11.4 | Tolerance stack-ups | Worst case and RSS stacks from model dimensions and fits, in the server | A textbook stack's results | 16 | P2 | ⬜ |
| T11.5 | TolAnalyst | Probe the add-in's API | A stack study runs and reports | 16 | P3 | 🔬 |
| T11.6 | General tolerances | ISO 2768 and ISO 286 tables offline; general tolerance notes | Table values against the standard | 6 | P2 | ⬜ |

## T12 · Simulation (FEA)
Static (parts and assemblies, contacts, convergence, nonlinear, reports), frequency, buckling and thermal work. Next:
every load, fixture and connector, shells and beams, mesh control, result breadth, more study types and a validation
suite.

| ID | Item | Approach (API) | Done when | h | P | State |
|---|---|---|---|---|---|---|
| T12.1 | Bearing loads | `AddBearingLoad` on cylindrical faces | Reactions balance the load | 6 | P1 | ⬜ |
| T12.2 | Remote loads and masses | `AddRemoteLoad` (force, moment, mass at a point) | Reaction moment = force × arm | 6 | P1 | ⬜ |
| T12.3 | Centrifugal loads | `AddCentrifugalForce` | A rotating ring's hoop stress = ρω²r² | 6 | P1 | ⬜ |
| T12.4 | Torque | Torque on cylindrical faces | Shaft shear stress = Tr/J | 6 | P1 | ⬜ |
| T12.5 | Prescribed displacements | `AddPrescribedDisplacement` | Reaction = k × displacement for a bar | 6 | P1 | ⬜ |
| T12.6 | Non-uniform pressure | Pressure varying by an equation over a face (hydrostatic) | Resultant = ∫p dA | 6 | P2 | ⬜ |
| T12.7 | Thermal stress | Temperatures as loads, or a thermal study's results imported into a static one | A constrained bar's stress = E·α·ΔT | 12 | P1 | ⬜ |
| T12.8 | More fixtures | Elastic supports, reference-geometry (directional) fixtures, edges and vertices, cyclic symmetry | Each fixture's reaction components as expected | 12 | P1 | ⬜ |
| T12.9 | Spring, rigid and link connectors | `AddSpringConnector`, `AddRigidConnector`, `AddLinkConnector` | A spring's force = k × elongation | 12 | P2 | ⬜ |
| T12.10 | Spot and edge weld connectors | `AddSpotWeldConnector`, `AddEdgeWeldConnector` with weld sizing | Weld forces balance the load; the size check against the throat | 12 | P2 | ⬜ |
| T12.11 | Bearing connectors | `AddBearingConnector` | Bearing reaction = shaft load | 6 | P2 | ⬜ |
| T12.12 | Contact completeness | Friction, shrink fits, virtual walls, contact pressure results (`GetContactForcesAndFriction`), automatic contact sets for touching faces | A shrink fit's contact pressure equals the Lamé solution within 5 % | 16 | P1 | 🟡 |
| T12.13 | Mesh control | Local controls (`ApplyMeshControl`), curvature-based meshes, quality report (aspect ratio, Jacobian), adaptive h/p methods, failure diagnosis | Local element size read back; adaptive runs converge | 20 | P1 | 🟡 |
| T12.14 | Shells | Sheet-metal and surface bodies as shells with thickness (`ICWShell`), offsets | A plate under pressure matches Roark's deflection | 12 | P1 | ⬜ |
| T12.15 | Beams | Weldment members as beams with joints; beam stresses and diagrams | A portal frame's moments match the hand calculation | 16 | P1 | ⬜ |
| T12.16 | Result breadth | Strain, principal and component stresses, probes at points and along edges (`GetResultsProbeManager`), results on chosen faces, free-body forces, reaction per fixture, results to CSV | Probe values match the plot; reactions per fixture sum to the total | 24 | P1 | 🟡 |
| T12.17 | Load cases | Load case manager, combinations | A combination equals the sum of its cases | 12 | P2 | ⬜ |
| T12.18 | Transient thermal | Time steps, initial temperatures, results over time | A lumped body's temperature follows exp(−t/τ) | 12 | P1 | ⬜ |
| T12.19 | Linear dynamics | Modal time history, harmonic, random vibration, response spectrum | An SDOF system's amplification matches theory | 32 | P2 | ⬜ |
| T12.20 | Drop tests | Study type 6 | The run completes; impact results reported with energy checks | 12 | P3 | ⬜ |
| T12.21 | Pressure vessel studies | Combinations and stress linearization | Membrane stress = pr/t on a thin cylinder | 10 | P3 | ⬜ |
| T12.22 | Topology optimization | Study type 13: goals, constraints, preserved regions; the smoothed mesh exported | Mass reduction reached under the stress constraint | 24 | P2 | ⬜ |
| T12.23 | Design studies | Parameters on dimensions, constraints on results, goals | A cantilever's optimum thickness matches the closed form | 24 | P2 | ⬜ |
| T12.24 | Nonlinear materials | Plasticity (bilinear von Mises), large strain, nonlinear contact | A bar pulled past yield follows the bilinear curve | 24 | P2 | ⬜ |
| T12.25 | 2D simplification and submodeling | Plane stress/strain, axisymmetric; submodels | An axisymmetric cylinder matches the 3D study | 16 | P3 | ⬜ |
| T12.26 | Simulation materials | Temperature-dependent properties, S-N curves in custom materials | Values read back in the study | 8 | P2 | 🟡 |
| T12.27 | Validation suite | NAFEMS LE1, LE10, FV32 and textbook beam, plate and shell cases as regression oracles | Every case within its published tolerance | 40 | P1 | ⬜ |
| T12.28 | Plot options | Animations, iso and section clipping, deformation scale, custom views | Clipped plot's range matches the probe | 12 | P2 | 🟡 |
| T12.29 | Existing studies | Run, edit (with the user's permission) and read the user's own studies | Results of a user study read without changing it | 12 | P1 | ⬜ |
| T12.30 | Flow Simulation | Probe the Flow Simulation API: internal flow pressure drop | A pipe's pressure drop matches Darcy–Weisbach within 5 % | 40 | P3 | 🔬 |
| T12.31 | Plastics | Probe the Plastics API: fill time and weld lines | A plate's fill completes; results reported | 40 | P3 | 🔬 |

## T13 · Motion and mechanisms
Motion results were blocked (T0.7). Kinematic sweeps (T9.9) give a robust path without the Motion add-in.

| ID | Item | Approach (API) | Done when | h | P | State |
|---|---|---|---|---|---|---|
| T13.1 | Motors, springs, dampers, contacts, gravity | `IMotionStudy.CreateDefinition` with the `swFmAEM*` ids, after T0.7 | Each element read back; results match the oracles of T0.7 | 16 | P2 | ⚠ |
| T13.2 | Motion results and plots | `IMotionPlotFeatureOutput`, CSV export, reaction forces, motor power | A lever's motor torque = m·g·r over the motion | 16 | P2 | ⚠ |
| T13.3 | Animations | Exploded-view animations, rotate and walk-through cameras; AVI, then MP4 or GIF | Frame count and size of the file | 16 | P2 | ⬜ |
| T13.4 | Event-based motion | Tasks triggered by sensors and times | Events fire at their triggers | 12 | P3 | ⬜ |
| T13.5 | Motion loads into FEA | Import motion loads into a static study | Static reactions equal the motion forces at that frame | 12 | P3 | ⬜ |

## T14 · Molds, plastics and casting
Draft, undercut and thickness checks and a flat-parting-line cavity/core split work.

| ID | Item | Approach (API) | Done when | h | P | State |
|---|---|---|---|---|---|---|
| T14.1 | Stepped parting lines | Non-planar parting lines, stepped parting surfaces, interlocks | Cavity + core = block − part for a stepped part | 24 | P2 | ⬜ |
| T14.2 | SOLIDWORKS' tooling split | `InsertMoldCoreCavitySolids` again, from SOLIDWORKS' example sequence | The tool split's bodies match `mold_split`'s | 12 | P2 | ⚠ |
| T14.3 | Side cores and lifters | Core features from sketches on the cavity/core | The side core's body releases sideways (no undercut left) | 16 | P3 | ⬜ |
| T14.4 | Draft fixes and thickness with Utilities | DraftXpert-style fixes from the draft check; `IThicknessAnalysis` compared with the ray casts | Fixed faces pass the draft check; both thickness methods agree | 8 | P2 | ⬜ |
| T14.5 | Casting | Shrink and machining allowances, cast and machined configurations | The machined configuration removes the allowance volume | 8 | P3 | ⬜ |

## T15 · Manufacturing
Single-body costing and cut lists work.

| ID | Item | Approach (API) | Done when | h | P | State |
|---|---|---|---|---|---|---|
| T15.1 | DFM checks in the server | Hole depth to diameter, minimum wall, internal corner radii against tool sizes, bend radius against thickness, hole-to-bend distances, draft; each finding with its faces | A seeded part's six violations are all found, no false ones | 24 | P1 | ⬜ |
| T15.2 | Quoting data | Stock size (bounding box), hole list with sizes and counts, paint or coating area, weld length, bend count | Each number against the model's own | 8 | P1 | ⬜ |
| T15.3 | Costing completeness | Assemblies (roll-up), casting, plastic and 3D-printing methods, company templates, quantity breaks | Totals add up across the assembly | 16 | P2 | 🟡 |
| T15.4 | Design Checker | `ISWDesignCheck`: run a standards file on parts and drawings | A seeded standard violation reported | 12 | P2 | 🔬 |
| T15.5 | 3D printing preparation | Orientation for least support (facet normals), overhang report, 3MF with colours, `ISw3DPrinter` | The suggested orientation's overhang area is the minimum of the candidates | 16 | P2 | ⬜ |
| T15.6 | Inspection reports | SOLIDWORKS Inspection API: ballooned drawings and characteristics (AS9102 first-article) | Every dimension becomes a characteristic | 24 | P3 | 🔬 |
| T15.7 | SOLIDWORKS CAM | Probe: machinable features, operation plan, toolpaths, post | A pocket's toolpath is generated | 40 | P3 | 🔬 |

## T16 · Data management
Pack and Go, release packages, BOMs and properties work on open documents.

| ID | Item | Approach (API) | Done when | h | P | State |
|---|---|---|---|---|---|---|
| T16.1 | Document Manager API | Properties, configurations, references, BOM data and thumbnails without opening files (needs the user's licence key) | A folder of 200 files read in under a minute; values equal those read in SOLIDWORKS | 24 | P1 | 🔬 |
| T16.2 | Batch operations over folders | Properties, export, rebuild and save, conversions, design review over a folder, resumable, with progress (T3.9) | 100 files processed; a stopped run resumes where it stopped | 24 | P1 | ⬜ |
| T16.3 | Where-used | Which assemblies and drawings in a folder use a part | Matches the references SOLIDWORKS reports | 12 | P2 | ⬜ |
| T16.4 | Rename and move with references | Rename or move files and update the assemblies and drawings that use them | The renamed set opens without missing references | 12 | P2 | ⬜ |
| T16.5 | PDM Professional | Vault login (the user's own), search, check out and in, get latest, versions, workflow states, variables; read-only first, writes with confirmation | Round trip on a test vault: a checked-in revision with its variables | 60 | P3 | 🔬 |
| T16.6 | Revisions | Revision schemes, revision table synced with the property, change notes | Table and property agree after a revision | 8 | P2 | ⬜ |
| T16.7 | Property templates | Property tab builder files, bulk apply to a folder | Every file gets the template's properties | 6 | P2 | ⬜ |
| T16.8 | Design Library | Insert parts, features and annotations from the design library | Inserted items read back | 8 | P2 | ⬜ |

## T17 · Interoperability
Import (STEP, IGES, Parasolid, SAT, STL) and export (STEP, IGES, Parasolid, STL, 3MF, images, eDrawings, PDF, DWG/DXF)
work.

| ID | Item | Approach (API) | Done when | h | P | State |
|---|---|---|---|---|---|---|
| T17.1 | Native CAD files (3D Interconnect) | Open CATIA, NX, Creo, Inventor, Solid Edge and JT files; break the link | Volumes of test files against their STEP equivalents | 12 | P1 | ⬜ |
| T17.2 | More export formats | glTF/GLB, OBJ, VRML, ACIS SAT, JT, U3D, IFC, VDA-FS, with option sets | Each file's header and structure; volumes after re-import | 20 | P1 | ⬜ |
| T17.3 | Import options | Knit, surfaces vs solids, units, assemblies kept as assemblies or merged | Each option's result checked (body count, structure) | 12 | P1 | 🟡 |
| T17.4 | Mesh bodies | STL, OBJ and 3MF as mesh, graphics or solid bodies; mesh to solid | Facet count and volume | 16 | P2 | 🟡 |
| T17.5 | Scans and point clouds | ScanTo3D probe | A scanned mesh becomes a surface fit | 24 | P3 | 🔬 |
| T17.6 | DXF/DWG into drawings and options | Import as a sheet; export options (splines as polylines, layers) | Entity counts in the file | 8 | P2 | 🟡 |
| T17.7 | Better images | High resolution, transparent background, chosen views and display modes | Image size and alpha channel | 4 | P2 | 🟡 |
| T17.8 | eDrawings | Export with markup and measure permissions; capture through the eDrawings API | File opens with the permissions | 8 | P3 | 🟡 |
| T17.9 | Round-trip suite | Export and re-import every format, compare volumes and areas | Every format within its tolerance | 8 | P1 | ⬜ |
| T17.10 | Design-intent JSON | Export a part's features, sketches and dimensions as JSON; rebuild a part from it | A rebuilt part has the same volume, features and dimension names | 40 | P2 | ⬜ |

## T18 · Visualization
Screenshots, section screenshots and plots work. PhotoView 360 is gone from 2025.

| ID | Item | Approach (API) | Done when | h | P | State |
|---|---|---|---|---|---|---|
| T18.1 | Renders through Visualize | `IVisualizeAddin`: send the model, set the scene and camera, render to a file | A render file of the asked size | 24 | P2 | 🔬 |
| T18.2 | Appearances and scenes | Library appearances (metals, plastics), textures, decals, scenes, lights, cameras, named views | Appearance names read back per face and body | 16 | P2 | 🟡 |
| T18.3 | Display options in screenshots | Perspective, RealView, ambient occlusion, transparent components | The settings restored afterwards | 6 | P2 | 🟡 |
| T18.4 | Animations to GIF/MP4 | Turntables, explode and collapse | Frame count and size | 16 | P2 | ⬜ |
| T18.5 | Contact sheets | One image with a thumbnail per component | One tile per component | 6 | P3 | ⬜ |

## T19 · Routing and electrical
Separate products; only when a user asks.

| ID | Item | Approach (API) | Done when | h | P | State |
|---|---|---|---|---|---|---|
| T19.1 | Pipes and tubes | Routing API (`RouteManager`): routes between connection points, fittings from the library, route BOMs | Route length equals the segments' sum | 40 | P3 | 🔬 |
| T19.2 | Cables and harnesses | Electrical routes, harness flattening for drawings | Harness length and connectors read back | 40 | P3 | 🔬 |
| T19.3 | Connection and route points | `InsertConnectionPoint` on parts | Points read back with their diameters | 6 | P3 | ⬜ |

## T20 · Engineering knowledge and design automation
Things an engineer would otherwise look up or compute, built on the verified tools.

| ID | Item | Approach (API) | Done when | h | P | State |
|---|---|---|---|---|---|---|
| T20.1 | Involute gears | Spur and helical gears from module and teeth (equation curves) | Tooth thickness and base circle against the standard | 10 | P2 | ⬜ |
| T20.2 | Springs | Compression, extension and torsion springs with closed ends; rate against EN 13906 | Free length, coils and rate formula | 10 | P2 | ⬜ |
| T20.3 | Shafts and keyways | Shafts with DIN 6885 keyways, shoulders, circlip grooves (DIN 471) | Groove and keyway sizes against the tables | 10 | P2 | ⬜ |
| T20.4 | Pulleys and sprockets | Timing pulleys, ISO 606 sprockets | Pitch diameters | 10 | P2 | ⬜ |
| T20.5 | Seal grooves | O-ring grooves (ISO 3601) | Groove depth and width against the table | 6 | P2 | ⬜ |
| T20.6 | Material name mapping | EN, ASTM and JIS designations to SOLIDWORKS materials | Known pairs map both ways | 8 | P2 | ⬜ |
| T20.7 | Explain a model | Design intent from the tree, sketches and relations as readable text; fragile references (faces made by fillets, under-defined sketches) | A known part's description names its features and its two fragile references | 16 | P1 | ⬜ |
| T20.8 | Company rules | `model_review` against a rules file: naming, required properties, material set, units | A seeded part's violations found | 12 | P1 | 🟡 |
| T20.9 | Variant generation | Drive `config_table` from CSV and export each variant (release per variant) | One release folder per variant with its volumes | 12 | P2 | 🟡 |
| T20.10 | Sensors | Read sensors (mass, dimensions, simulation) and their alerts | Values equal the measured ones | 6 | P2 | ⬜ |
| T20.11 | Engineering checks | Bolt joints (simplified VDI 2230), shaft deflection, bearing L10 life, linked to model values | Textbook examples' results | 24 | P3 | ⬜ |
| T20.12 | DriveWorksXpress | Probe its API | A rule-driven variant generated | 8 | P3 | 🔬 |

## T21 · Architecture and performance
| ID | Item | Approach (API) | Done when | h | P | State |
|---|---|---|---|---|---|---|
| T21.1 | Add-in mode | A SOLIDWORKS add-in hosting the COM side, talking to the server over a named pipe; in-process calls and events; the out-of-process mode stays | The suite passes in both modes; median call 5× faster in add-in mode | 80 | P3 | ⬜ |
| T21.2 | SOLIDWORKS events | Document changed, saved, closed by the user; rebuilds; feeding caches, ownership stamps and MCP notifications | A user edit is seen within a second | 24 | P2 | ⬜ |
| T21.3 | Performance | Profile every tool; cache geometry per rebuild; faster resolver on large parts | Median call time halved on the benchmark | 24 | P1 | ⬜ |
| T21.4 | Undo transactions | One undo step per tool call (`StartRecordingUndoObject` / `FinishRecordingUndoObject2`) | `doc_undo` of one call undoes all its features | 16 | P1 | ⬜ |
| T21.5 | Several SOLIDWORKS instances | Choose an instance by process id | A test against a second instance leaves the first untouched | 12 | P3 | ⬜ |
| T21.6 | Remote SOLIDWORKS | The server next to SOLIDWORKS on a Windows VM, clients over HTTP (T3.11) | A remote client models a part | 8 | P3 | ⬜ |
| T21.7 | Code health | Split the largest files (the simulation service is 1,859 lines), shared verification helpers, stricter analyzers | No file over 1,000 lines; warnings as errors | 24 | P1 | ⬜ |
| T21.8 | Tool packs | Company tools loaded from a folder with the same verification framework | A sample pack's tool appears and passes its test | 24 | P3 | ⬜ |
| T21.9 | Verification levels | Fast and thorough modes | Fast mode skips only the documented checks | 8 | P2 | ⬜ |

## T22 · Security, safety and privacy
| ID | Item | Approach (API) | Done when | h | P | State |
|---|---|---|---|---|---|---|
| T22.1 | Threat model | Prompt injection through model content (properties, file and feature names, drawing notes), malicious files, paths, VBA | `docs/THREAT-MODEL.md` with each threat's mitigation and test | 8 | P0 | ⬜ |
| T22.2 | Untrusted content | Text read from documents is marked as data in results; control characters removed | A property holding instructions comes back marked and inert | 8 | P1 | ⬜ |
| T22.3 | VBA hardening | Allow-list mode (owned documents only), checks against `Shell`, `Kill`, `CreateObject`, file and network calls; a log of every macro | A macro calling `Shell` is refused | 12 | P1 | ⬜ |
| T22.4 | Audit log | Every state-changing call: document, inputs, verification outcome; exportable; retention | The log reconstructs a session | 8 | P1 | ⬜ |
| T22.5 | Privacy modes | No-screenshot mode, path redaction, redacted property lists (ITAR/EAR) | Results carry no paths or images in those modes | 8 | P1 | ⬜ |
| T22.6 | Backups | Rotation and a size cap; a restore tool | A restored file equals its backup | 6 | P2 | 🟡 |
| T22.7 | Supply chain | Pinned packages, SBOM, signed releases | SBOM published with each release | 4 | P1 | 🟡 |
| T22.8 | HTTP security | Tokens, origin checks, DNS-rebinding protection (with T3.11) | Attacks in the test list refused | 8 | P2 | ⬜ |
| T22.9 | Input fuzzing | Fuzz EntityRef, numbers, paths and JSON shapes offline | No crash or unvalidated value reaches COM | 8 | P1 | ⬜ |

## T23 · Testing and evaluation
| ID | Item | Approach (API) | Done when | h | P | State |
|---|---|---|---|---|---|---|
| T23.1 | Regression orchestrator | Every live class in batches, SOLIDWORKS restarted between them (only without unsaved user documents), known-flaky retries, one report with timings | The full suite runs unattended and writes one report | 16 | P0 | 🟡 |
| T23.2 | Scheduled runs | Weekly live runs on the maintainer machine; results on a status page | A published history of runs | 8 | P1 | ⬜ |
| T23.3 | Self-hosted CI with SOLIDWORKS | A runner for labelled pull requests | A PR shows live results | 12 | P2 | ⬜ |
| T23.4 | More golden scenarios | Weldment frame, molded part and mold, bracket FEA with report, configuration family, top-down assembly, surface model, imported STEP edit, drawing package, sheet-metal enclosure with corners, fastened assembly, release per variant, design review | 12 new scenarios within their budgets | 48 | P1 | 🟡 |
| T23.5 | Agent benchmark | 100 engineering tasks in plain language with exact oracles, run by a real agent through MCP: success rate, calls, tokens, time, per release | A baseline, then a tracked trend | 100 | P1 | ⬜ |
| T23.6 | Tool-selection set | 300 requests with their expected tools (with T3.8) | Run in CI with a model | 12 | P1 | ⬜ |
| T23.7 | Flakiness tracking | Pass/fail history, quarantine with issues | Flaky tests listed with their rates | 6 | P2 | ⬜ |
| T23.8 | Visual regression | Perceptual diffs of screenshots, plots and drawings against baselines | A deliberate change is caught | 12 | P2 | ⬜ |
| T23.9 | Coverage map | Every tool, kind and option mapped to its live tests | No option without a test | 8 | P1 | ⬜ |
| T23.10 | COM record and replay | A recorded fake of SOLIDWORKS for running service logic in CI | Ten services' tests run in CI without SOLIDWORKS | 40 | P2 | ⬜ |
| T23.11 | Soak test | An eight-hour scripted agent session | No leak, no slowdown beyond T1.1's limits | 8 | P1 | ⬜ |

## T24 · Documentation and community
| ID | Item | Approach (API) | Done when | h | P | State |
|---|---|---|---|---|---|---|
| T24.1 | Documentation site | Getting started, concepts (ownership, EntityRef, verification), the generated tool reference, guides per area, recipes, troubleshooting | Published on GitHub Pages from the repository | 24 | P1 | ⬜ |
| T24.2 | Tutorials | Ten walk-throughs with images: plate, enclosure, sheet-metal bracket, weldment frame, fastened assembly, drawing package, FEA, STEP clean-up, configurations, mold | Each tutorial replays as a scenario | 30 | P1 | ⬜ |
| T24.3 | Demos | GIFs and short videos for the README | In the README | 8 | P2 | ⬜ |
| T24.4 | Examples | `examples/` with prompts, results and replay scripts | Each example replays | 16 | P2 | ⬜ |
| T24.5 | Adding a tool | A template, the §6.6 checklist and a generator script | A new tool from the generator passes its tests | 8 | P1 | ⬜ |
| T24.6 | Community files | Issue and PR templates, a compatibility report template, code of conduct | On GitHub | 4 | P1 | ⬜ |
| T24.7 | API field notes | The verified recipes and failures published as a standalone reference | Published | 12 | P2 | 🟡 |
| T24.8 | Skill upkeep | Update the skill each release; check that agents follow it | The benchmark with the skill beats it without | 8 | P1 | 🟡 |
| T24.9 | Roadmap and changelog upkeep | This file and the changelog at each release | Updated at every release | 16 | P1 | 🟡 |

---

## Blocked by the API
What was tried, why it failed and the next idea. Each has an item above.

| Capability | What happened | Next idea | Item |
|---|---|---|---|
| Screw mates | `RevolutionVal` keeps its default whatever is set | Feature data before `CreateMate`; editing after; a coupled fallback | T0.10 |
| Sheet-metal jogs | `InsertSheetMetalJog` made nothing | Other selections and marks | T0.11 |
| Hole series | No API creates one | Per-part holes on a shared axis | T0.12 |
| Motion results | Readings jumped, or came back zero or constant | CSV export; a saved and reopened study; kinematic sweeps instead | T0.7, T9.9 |
| Factor-of-safety plot | The plot's range was ±1.5 million | Range from `GetMinMaxFactorOfSafety2` | T0.1 |
| Global no-penetration contact | Reads back, results stay bonded | Before meshing; local sets on every touching pair | T0.14 |
| Flex | Twist grew the volume 20 % | Check end positions instead of volume | T0.13 |
| Renders | PhotoView 360 is gone from 2025 | SOLIDWORKS Visualize | T18.1 |
| Toolbox insertion | No API | Insert the master files and set the size | T9.24 |
| Tooling split | `InsertMoldCoreCavitySolids` made nothing | SOLIDWORKS' example sequence | T14.2 |
| Boundary surfaces | No API inserts one | Probe `CreateDefinition` | T6.3 |
| Sensors | They can be read but not created | Read and edit only | T20.10 |
| DFMXpress, Smart Fasteners | No API | In-server DFM checks (T15.1); `asm_fastener` | T15.1 |
| Suspended VBA engine | No API reads its state | Window probes, a sentinel macro | T0.9 |

## Not planned
- **3DEXPERIENCE platform** (cloud PLM, xDesign): a different product and API.
- **Driving the user interface** by screen clicks or keystrokes: against the design (typed API calls, verified results).
- **Non-Windows hosts for SOLIDWORKS**: SOLIDWORKS runs on Windows only. Remote use goes through HTTP (T3.11, T21.6).
- **Writing to the user's documents without permission**: ownership rules stay (DESIGN §3.7).
- **Telemetry**: the server makes no network calls; the benchmark and status pages use the maintainer's own runs.

## Keeping this roadmap current
- When an item starts, its approach is refined in DESIGN (a milestone section, as for M8 and M9) and its probes land in
  the API notes.
- When an item is done: ✅ with the date and commit in its State column, the README status table and the changelog
  updated, the effort table recomputed with `python tools/roadmap_totals.py` (CI runs it with `--check`, which also
  rejects malformed rows and duplicate ids).
- New items get the next free ID in their track and an oracle; items without an oracle are not ready to start.
- At each release, the release plan's row is closed and the next one refined.
