---
name: solidworks-mcp
description: Model, inspect, edit and export SOLIDWORKS parts through the solidworks-mcp server (tools named sw_*, doc_*, sketch_*, feature_*, face_list, edge_list, model_*, view_screenshot, ref_*, material_*, …). Use it whenever the user wants something built, measured, changed, exported or checked in SOLIDWORKS — "make a bracket", "add M5 clearance holes", "fillet the outer edges", "export a STEP", "how heavy is this part". Read it before the first call: it explains units, coordinates, how to reference geometry, document ownership and how to recover from errors.
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
Parasolid and more). Existing files need `overwrite:true`.

## More help
- `sw_help` explains the conventions and gives step-by-step recipes for common parts.
- The full tool list with parameters is in `docs/tools.md` of the solidworks-mcp repository.
