---
name: solidworks-mcp
description: Model, inspect, edit, assemble, detail, export and simulate in SOLIDWORKS through the solidworks-mcp server (tools named sw_*, doc_*, sketch_*, feature_*, body_*, surface_*, sheetmetal_*, asm_*, drw_*, sim_*, face_list, edge_list, model_*, view_screenshot, ref_*, material_*, …). Use it whenever the user wants something built, measured, changed, exported or checked in SOLIDWORKS — "make a bracket", "add M5 clearance holes", "fillet the outer edges", "model a spring", "make a sheet-metal bracket", "assemble these parts", "make a drawing with dimensions", "export a STEP", "how heavy is this part", "will it hold 100 N". Read it before the first call: it explains units, coordinates, how to reference geometry, document ownership and how to recover from errors.
---

# Driving SOLIDWORKS with solidworks-mcp

The server drives the user's **live** SOLIDWORKS. Every change it makes is verified: the result reports what happened,
and a failed feature is rolled back. Work like a careful CAD engineer: plan the feature sequence, check each result,
and look at a screenshot when the shape matters.

## Rules
1. **Start with `sw_status`.** It shows the SOLIDWORKS version, the open documents and which of them you may change,
   any blocking dialog, and what happened to earlier calls.
2. **Units are mm, degrees and kg everywhere.** Never meters, except inside `vba_run`.
3. **SOLIDWORKS is Y-up.**
   - The planes are Top = XZ (normal +Y), Front = XY (+Z) and Right = YZ (+X). Name them `{"name":"top"}`, `"front"` or
     `"right"`.
   - Put flat parts on the top plane, so that "up" is +Y and "top face" means the +Y face.
4. **Points.**
   - `[x,y,z]` is model mm.
   - In sketch tools, `[x,y]` is sketch-local mm. On the top plane, sketch (x, y) is model (x, 0, −y).
   - When in doubt, pass model points `[x,y,z]`: sketch tools project them onto the sketch plane.
5. **Referencing geometry:**
   - `{"id":"F3"}`: ids come from `face_list`, `edge_list` and feature results (`newFaces`).
   - `{"at":[x,y,z]}`: the nearest face, edge or vertex. Use the pick points from listings; they always resolve back.
   - `{"name":"Sketch1"}` or `{"name":"top"}`.
   - For several edges or faces, use a query with a count guard:
     `"edges":[{"where":{"curve":"line","parallelTo":"y"},"expect":4}]`.
   - No selection state carries over between calls: pass every entity explicitly.
6. **Documents.**
   - Work in documents you create (`doc_new`). They are "owned".
   - The user's documents are read-only for you. If the user asks you to change one, call
     `doc_activate {doc, allowEdit:true}` (a backup copy is saved first).
   - Never close or discard the user's documents, and never overwrite their files unless they asked you to.
7. **Read the results before the next call.** A feature result gives:
   - the feature name and type;
   - `verified.volumeDeltaMm3` and `material` (added or removed);
   - `newFaces` with ids, normals, axes and radii;
   - the dimension names (`D1@Boss-Extrude1`);
   - the model's new bounding box.

   You rarely need another listing.
8. **Errors read `CODE: message Hint: …`.** Follow the hint.
   - A failed feature was rolled back: change the input instead of repeating the same call.
   - `ENTITY_AMBIGUOUS` lists candidate ids: pick one.
   - `TIMEOUT`: the operation may still finish. Call `sw_status` and check `recentOps` before retrying anything.
   - `DIALOG_BLOCKING`: SOLIDWORKS shows a dialog. Call `dialog_list`, tell the user what it says and ask how to answer.
     Use `dialog_respond` yourself only for a dialog your own call caused (for example an error message with OK).
   - `USER_BUSY`: the user is in a SOLIDWORKS command; ask them to finish it.
   - `NOT_OWNED`: see rule 6.
9. **Check visually with `view_screenshot`** after a few features, or whenever orientation matters. It restores the
   user's view afterwards.
10. **`vba_run` is a last resort** and is off unless the user enabled it. Inside VBA the API uses meters and radians:
    use `MM()` and `DEG()`.

## Workflows

**A plate with rounded corners and holes** (6 calls):
```text
doc_new {name:"plate"}
sketch_start {on:{name:"top"}}
sketch_add {entities:[{kind:"rectangle",center:[0,0],widthMm:100,heightMm:60},
                      {kind:"circle",center:[-35,0],diameterMm:6},{kind:"circle",center:[35,0],diameterMm:6}]}
feature_extrude {depthMm:6}                     // the open sketch is used and closed automatically
feature_fillet {radiusMm:5, edges:[{where:{curve:"line",parallelTo:"y"},expect:4}]}
view_screenshot
```

**Features on a face of the part.**
- Take the face id from the last result's `newFaces`, or from `face_list {where:{surface:"plane",normal:"+y"}}`.
- Then call `sketch_start {on:{id:"F2"}}`.
- Sketch with model points, or with sketch points inside the returned `faceBoundsMm`.
- A cut defaults to `direction:"auto"`, which goes into the material: `feature_extrude {cut:true, end:"through_all"}`.

**Changing a user's part.**
1. `doc_list` to find the document.
2. Ask the user, then `doc_activate {doc:"D2", allowEdit:true}`.
3. `feature_list` and `dimension_list` to understand the part.
4. Edit with `dimension_set`, features and the other tools.
5. `view_screenshot`.
6. Save only if the user wants it (`doc_save`, mode `inPlace`).

**Exporting.** Save native files with `doc_save {mode:"as", path:"bracket.sldprt"}`. Relative paths go under
Documents\solidworks-mcp. Export with `model_export {path:"bracket.step"}` (STEP AP214 by default; also IGES, STL, 3MF,
Parasolid and more). Existing files need `overwrite:true`. `doc_open` imports STEP/IGES/Parasolid/STL as a new part.
A release in one call: `model_export_all {}` on the saved assembly or part writes a STEP of every model, a PDF of each
drawing saved next to its model under the same name, a flat DXF of each sheet-metal part and the BOM (CSV) into
`<name>_release`; what it couldn't make is listed with the reason.

**Editing an imported (or any) body directly.** An imported STEP has no features to edit, so change its faces:
- `face_edit {op:"delete", faces:[{where:{surface:"cylinder", radiusMm:3}, expect:4}]}` removes holes, fillets or bosses
  (heal `patch` by default: the neighbouring faces grow over the gap).
- `face_edit {op:"offset", faces:[{id:"F5"}], distanceMm:2}` moves faces along their normals; positive adds material,
  so a hole gets smaller.
- `face_edit {op:"move", faces:[…], translateMm:[10,0,0]}` moves faces, e.g. relocates a hole.

**Springs and swept shapes.**
1. Sketch a circle (the coil radius), then `feature_helix {definedBy:"pitch_revolutions", pitchMm:5, revolutions:4}`.
2. The result gives `startMm` and `profilePlaneNormal`: the profile must sit there, so either sketch the profile circle
   on a plane through `startMm`, or use `feature_sweep {path:{name:"Helix/Spiral1"}, circularProfileDiameterMm:2}`.
3. Straight or curved tubes: `feature_sweep {profile:{name:"Sketch2"}, path:{name:"Sketch3"}}`.
4. Transitions between sections: `feature_loft {profiles:[{name:"Sketch1"},{name:"Sketch2"}]}`, with each profile on
   its own plane (`ref_plane {kind:"offset", base:{name:"top"}, offsetMm:30}`).

**Sheet metal.** Sketch a closed outline, then `sheetmetal_base_flange {thicknessMm:2, bendRadiusMm:1}`. Add bends
with `sheetmetal_edge_flange`; for walls around corners (or all round, a tray) use one `sheetmetal_miter_flange
{edges:[…edges of the top face that meet end to end…], lengthMm:10}` (the walls go up from the face whose edges you
pick, mitered where they meet); fold edges back with `sheetmetal_hem {edges:[…], lengthMm:8}` (closed by default; open needs
`gapMm`, teardrop and rolled `radiusMm`), bend the plate along a line with `sheetmetal_bend {fromMm:[x,y,z], toMm:[x,y,z],
angleDeg:90}` (the larger side stays unless you give `fixedAtMm`), check the blank with `sheetmetal_flatten {flat:true}`,
save the part, then
`sheetmetal_export_flat {path:"bracket_flat.dxf"}` for the laser cutter. `model_cut_list` gives the blank's size,
thickness and number of bends.

**Weldment frames.** Sketch the frame's centre lines (a 2D sketch, or `sketch_start {threeD:true}` with [x,y,z] points),
then `weldment_member {type:"square tube", size:"40 x 40 x 4"}` (ISO by default; a wrong type or size lists the real
ones). One call per group of contiguous or parallel lines: e.g. a table's top rectangle (corners mitered), then its legs
with `segments:["Line5",…], corner:"none"`. The result lists each member's body and length; `model_cut_list
{update:true}` gives the cut list (each length with its quantity and end angles). Finish with `weldment_finish`:
`{kind:"trim", bodies:[{name:"<post body>"}], boundary:[{name:"<beam body>"}], gapMm:0}` (a trimmed body is renamed after
the trim; the result gives the new name), `{kind:"end_cap", faces:[{at:[…on the tube's end…]}], thicknessMm:5}` (direction
`inward` or `internal` with `insetMm`), `{kind:"gusset", faces:[{at:…beam top…},{at:…post side…}], thicknessMm:5,
legsMm:[50,50]}` and `{kind:"bead", faces:[…], faces2:[…], sizeMm:4}` (a weld bead; `side:"all_around"`, or
`lengthMm`+`pitchMm` for an intermittent weld).

**Assemblies.**
1. Save each part first (`doc_save {mode:"as", path:"cube.sldprt"}`): components must be files.
2. `doc_new {type:"assembly"}`, then `asm_insert {path:"cube.sldprt", originMm:[0,0,0]}`. The first component is fixed;
   insert the others with `fixed:false`.
3. Mate with `asm_mate_add {type:"coincident"|"concentric"|"distance"|…, a:{component:"cube-2", name:"top"},
   b:{component:"cube-1", name:"top"}}`. Faces of a component: `{component:"cube-2", at:[x,y,z]}`, with the point in
   that part's own coordinates. More kinds: a limit distance (`distanceMm` with `minMm`/`maxMm`), `symmetric` (with
   `plane`), `width` (a, b the slot's faces; c, d the tab's), `hinge` (a, b round; c, d planar), `gear` (`ratio:[20,40]`)
   and `rack_pinion` (`pitchMm` = rack travel per pinion turn). Screw mates are refused (SOLIDWORKS ignores their pitch).
4. Repeat components with `asm_pattern` instead of inserting each copy: `{kind:"linear", components:["bolt-1"],
   count:4, spacingMm:25, direction:"+x"}` (a second row: `direction2`, `count2`, `spacing2Mm`), `{kind:"circular",
   components:["bolt-1"], count:6, axis:{component:"flange-1", at:[x,y,z]}}` (a bore face), or `{kind:"mirror",
   components:["bracket-1"], plane:{name:"right"}}`. Copies are instances of the same file, so a part that isn't
   symmetric comes out turned, not mirrored; for a true left-hand version add `oppositeHand:true` (a new part file
   `<part>_mirror.sldprt` next to the seed, or `suffix`/`folder`; existing files are never replaced).
5. Check with `asm_interference` and `asm_component_list` (origins and rotations), then `view_screenshot`.
6. Exploded view (for assembly instructions or a drawing): `asm_explode {steps:[{components:["lid-1"], direction:"+y",
   distanceMm:60}, {components:["bolt-1","bolt-2"], direction:"+y", distanceMm:120}]}`; `collapse:true` keeps the view
   but shows the assembly assembled again.
7. Bill of materials without a drawing: `asm_bom {levels:"parts", path:"bom.csv"}` (`top`, `parts` with quantities through
   subassemblies, or `indented`; part numbers from the PartNo / Part Number property, else the file name).

**Drawings.**
1. Save the part or assembly.
2. `doc_new {type:"drawing"}` (A3 landscape, ISO, first angle; change it with `drw_sheet_setup`).
3. `drw_views_standard {model:"bracket.sldprt"}` (front, top, right, iso), then `drw_dimensions_auto`. Tolerances: set them
   on the model's dimensions with `dimension_tolerance_set {tolerances:[{name:"D1@Sketch1", type:"bilateral", upper:0.1,
   lower:-0.05}]}` (also `symmetric`, `limit`, `basic`, or fits `{type:"fit", hole:"H7", shaft:"g6"}`), so the drawing shows
   them; a drawing's own dimension (the name `drw_dimension_add` returns) takes one the same way with `doc` = the drawing.
   Hole Wizard holes get `drw_dimension_add {kind:"hole_callout", a:{type:"edge", at:[…]}}` (size and depth).
4. GD&T and symbols on model edges: `drw_annotation_add {kind:"datum", view, at:{type:"edge", at:[x,y,z]}, label:"A"}`,
   `{kind:"gtol", characteristic:"position", tolerance:"0.1", diameter:true, material:"mmc", datums:["A"], at:…}`,
   `{kind:"surface_finish", roughness:"1.6", at:…}` or a `balloon` (after `drw_bom_add`; without `at`, one per part of the
   view). Each is placed beside its edge unless you pass `atMm`. Tables: `drw_table_add {kind:"hole", view}` (X/Y of every
   hole from the face's lower-left corner, or from `origin`) and `{kind:"revision", revisions:["First release"]}`.
5. Add `drw_view_section`, `drw_view_detail`, `drw_note_add`, and `drw_bom_add` for assemblies. `drw_info` lists the
   views with their positions. Sheet metal: `drw_view_add {kind:"flat_pattern", model:"bracket.sldprt"}` (the blank, with
   bend lines; it reports the blank's size). Angled faces: `drw_view_add {kind:"auxiliary", parent, edge:{type:"edge",
   at:[…]}}`. Long parts: `drw_view_edit {kind:"break", view, lines:"vertical", fromMm, toMm}` (sheet mm); a look inside:
   `{kind:"broken_out", view, centerMm, radiusMm, depthMm}`. Weldments: `drw_table_add {kind:"cut_list", view}` and
   `drw_annotation_add {kind:"weld", weld:"fillet", size:"a5", length:"50", side:"both", at:…}`. More sheets:
   `drw_sheet_add {size:"a4"}` (it becomes current); `drw_sheet_setup {sheet:"Sheet1"}` goes back.
6. `model_export {path:"bracket.pdf"}` (or .dwg/.dxf).

**Stress check.** `sim_static {material:"AISI 1020", fixedFaces:[{at:[0,5,5]}],
loads:[{kind:"pressure", faces:[{at:[50,10,5]}], valueMpa:0.1}]}` meshes, solves and returns the maximum von Mises
stress (MPa), displacement (mm) and factor of safety. It can take minutes; the study is deleted afterwards unless
`keepStudy:true`. Explain to the user that this is a linear static estimate.

## More help
- `sw_help` explains the conventions and gives step-by-step recipes for common parts.
- The full tool list with parameters is in `docs/tools.md` of the solidworks-mcp repository.
