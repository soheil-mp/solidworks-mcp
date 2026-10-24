# SOLIDWORKS API notes (verified live)

These notes come from live testing on **SOLIDWORKS Premium 2025 SP1.2 (revision 33.1.2)**, Windows 11. Each item is
marked:
- **VERIFIED**: the effect was confirmed by geometry, such as a volume, bounding box, face count, file header or screenshot.
- **UNRESOLVED**: still failing after the attempts listed.

The recipes were proven in SolidWorks' own BASIC macro engine (late bound). The API calls are identical from C#, where
the interop types give the signatures. Argument lists are given in the API's parameter order. All API lengths are in
**meters** and all angles in **radians**.

Signatures can be looked up in a local typelib dump: `python tools/dump_typelib.py` (needs pywin32) reads the installed
type libraries and writes `sldworks_api.txt`, `swconst_enums.txt`, `swcommands.txt` and `simulation_api.txt` to
`artifacts/typelib/`, which git ignores. The format is `Interface.Member | kind | dispid | nparams | (Name:TYPE, ...) -> RET`.

---

## 1. Connection, documents, units
- **Attach.** `GetActiveObject` via P/Invoke (oleaut32) plus `CLSIDFromProgID("SldWorks.Application")`, then cast to
  `ISldWorks`. VERIFIED from .NET 10 with embedded interop types (NoPIA).
- **Revision.** `ISldWorks.RevisionNumber()` returns `"33.1.2"`; that is SOLIDWORKS 2025 SP1.2.
- **Default templates.** `ISldWorks.GetUserPreferenceStringValue(swDefaultTemplatePart = 8)` (assembly 9, drawing 10). On
  the test machine `Part.PRTDOT` was **CGS**. MBD templates (`templates\MBD\part 0051mm to 0250mm.prtdot`) are MMGS.
- **Units.**
  - `IModelDocExtension.SetUserPreferenceInteger(swUnitSystem = 263, 0, swUnitSystem_MMGS = 5)` sets a document to mm
    (VERIFIED). The values are 1 CGS, 2 MKS, 3 IPS, 4 custom, 5 MMGS.
  - The linear unit is preference 47 (`swUnitsLinear`): 0 mm, 1 cm, 2 m, 3 in.
  - These are *document* preferences and need no restore.
- **Activate.** `ISldWorks.ActivateDoc3(title, false, 0, ref errors)` takes the title without an extension. VERIFIED.
- **Close.** `ISldWorks.CloseDoc(title)` closes without prompting; unsaved changes are discarded.
  - Closing a document that has **Simulation studies** shows a modal "Do you want to save Simulation data?" (Yes/No)
    dialog.
  - Closing a non-active document can switch the active document to an unexpected one.
  - A damaged document raised `RPC_E_SERVERFAULT` from `CloseDoc` and `QuitDoc` while it was active (see §3 Patterns).
    `ClearSelection2` and `ClearUndoList` didn't help, but it closed once another document was active.
- **Titles.** `GetTitle()` omits the extension when Explorer hides extensions. Drawing titles are `"<name> - Sheet1"`.
- **List documents.** `ISldWorks.GetDocuments()` returns an `object[]` of `ModelDoc2` (typed access works).
- **Save As.** `IModelDocExtension.SaveAs(path, 0, swSaveAsOptions_Silent = 1, null, ref err, ref warn)`. The option value
  **3** (silent + copy) saves a copy. It **overwrites silently**. The destination folder must exist, otherwise the error is 1.
- **Import STEP/IGES.** `ISldWorks.GetImportFileData(path)` → `LoadFile4(path, "r", importData, ref err)`. VERIFIED: it
  opens a new unsaved part.
  - `OpenDoc6` with type Part on a `.step` file fails with error 2097152 (`swFileRequiresRepairError`).
- **Undo.** `IModelDoc2.EditUndo2(steps)`.

## 2. Selection
- **`IModelDocExtension.SelectByID2(name, type, x, y, z, append, mark, callout, 0)`.** x, y, z are **model coordinates in
  meters**, even while editing a sketch. VERIFIED type strings:

| Type | Name format |
|---|---|
| `PLANE` | `"Front Plane"`, `"Plane1"`, and in assemblies `"Front Plane@part-1@Assem1"` |
| `SKETCH` | `"Sketch1"` |
| `SKETCHSEGMENT` | a bare `"Line3"` **only while that sketch is open** (`"Line3@Sketch2"` fails there) |
| `EXTSKETCHSEGMENT` | `"Line1@Sketch1"` (sketch closed) |
| `SKETCHPOINT` | name, or `""` plus model XYZ |
| `EXTSKETCHPOINT` | `""` plus XYZ (a 3D-sketch point) |
| `BODYFEATURE` | feature names |
| `AXIS` | `"Axis1"` (**not** `REFAXIS`) |
| `REFERENCECURVES` | `"Helix/Spiral1"` |
| `SOLIDBODY` / `SURFACEBODY` | body name (**not** `BODY`) |
| `COMPONENT` | `"ring-1@Assem1"` |
| `FACE` / `EDGE` / `VERTEX` | `""` plus XYZ (names like `Face<1>` never resolve) |

- `SelectByID2("", "FACE", xyz)` **failed on a face of a surface body**, but `face.Select4(append, selectData)` worked.
  Geometric resolution followed by `IEntity.Select4` is therefore the robust approach.
- **Marks:** `ISelectionMgr.CreateSelectData()` returns a `SelectData`; set `.Mark = n`, then `entity.Select4(append, data)`.
- **Selection persists** between calls, so a previous selection can leak into the next feature. Clear it with
  `ClearSelection2(true)`.
- **Pick points.** `IFace2.GetClosestPointOn(x, y, z)` gives a point on a face; `IEdge.GetClosestPointOn`, and
  `IEdge.GetCurveParams2()` (`[start xyz, end xyz, t0, t1, …]`) with `IEdge.Evaluate2((t0 + t1)/2, 0)` give a mid-edge
  point. Both VERIFIED.
- **Persistent references:** `IModelDocExtension.GetPersistReference3(entity)` returns a `byte[]`;
  `IModelDocExtension.GetObjectByPersistReference3(bytes, out status)` resolves it back (status 0 ok, 1 invalid, 2 suppressed, 4 deleted). Persistent ids do NOT follow splits or merges: re-check a stored signature after resolving.

## 3. Sketching
- **Open.** Select a plane or face, then `ISketchManager.InsertSketch(true)`, which **toggles**: called again it closes the
  sketch.
  - A **3D sketch** is `Insert3DSketch(true)` (also a toggle).
  - `IModelDoc2.EditSketch()` re-opens a selected sketch (VERIFIED). `IModelDoc2.MenuCommand` doesn't exist.
- **Active sketch.** `ISketchManager.ActiveSketch` is null when none is open.
- **Mass properties** return **zeros while a sketch is open**.
- **Inference / snapping.** `ISketchManager.AddToDB = true` disables it. It persists across calls on that document.
  - With it off, a circle drawn near a helix came out as r 0.9289 instead of 1 and gained a driven dimension. With
    `AddToDB = true` the radius was exact and no relations were added. VERIFIED.
  - Corner rectangles still form closed profiles with `AddToDB` on. VERIFIED.
- **Entities** (sketch coordinates in meters, z = 0):
  - `CreateLine`, `CreateCenterLine`, `CreateCircleByRadius(cx, cy, 0, r)`, `CreateArc(cx, cy, 0, sx, sy, 0, ex, ey, 0, dir)`
    where dir +1 = CCW.
  - `CreateCornerRectangle`, `CreateCenterRectangle(cx, cy, 0, cornerX, cornerY, 0)`.
  - `CreatePolygon(cx, cy, 0, vx, vy, 0, sides, inscribed)`, `CreateSpline2((double[] xyz…), true)`, `CreatePoint`.
  - Text: `IModelDoc2.InsertSketchText(x, y, 0, text, 0, 0, 0, 100, 100)`.
  - `CreateEllipse(center, majorPoint, minorPoint)`. Pass the minor point exactly perpendicular to the major axis. The
    old server's rotated ellipse came out wrong (a 8 → 8.124).
- **Naming.** Circles are named **ArcN**. Centerlines are LineN with `ConstructionGeometry = true`. Names count up per
  sketch in creation order. Polygons add a construction circle.
- **Geometry readback.** `ISketch.GetSketchSegments()`; `ISketchSegment.GetName()`, `.GetType()` (0 line, 1 arc, 2 ellipse,
  3 spline, 4 text), `.ConstructionGeometry`, `.GetLength()`. `ISketchLine.GetStartPoint2/GetEndPoint2` return
  `SketchPoint` (.X/.Y). `ISketch.GetArcs2` returns 16 doubles per arc: center at [12..14], start at [6..8].
- **Sketch-to-model transform.** `ISketch.ModelToSketchTransform.Inverse()` applied to points through
  `IMathUtility.CreatePoint`. Plane mappings:

| Sketch on | Model point | Normal |
|---|---|---|
| Front Plane | (x, y, 0) | +Z |
| Top Plane | (x, 0, **−y**) | +Y |
| Right Plane | (0, y, **−x**) | +X |
| Top face (normal +Y) | like Top Plane | +Y |

- **Relations.** `IModelDoc2.SketchAddConstraints(id)` ignores unknown ids, and ids that don't fit the selection,
  silently.
  - VERIFIED ids: `sgHORIZONTAL`, `sgVERTICAL`, `sgPARALLEL`, `sgPERPENDICULAR`, `sgTANGENT`, `sgCONCENTRIC`, `sgCOINCIDENT`,
    `sgSYMMETRIC` (two points plus a centerline), `sgFIXED`, `sgSAMELENGTH` (equal), `sgCOLINEAR`, `sgATMIDDLE` (midpoint),
    `sgATINTERSECT`.
  - **Two points level/plumb:** `sgHORIZPOINTS` / `sgVERTPOINTS` (types 25 / 26). `sgHORIZONTAL` and `sgVERTICAL` do
    nothing for two points. VERIFIED.
  - **Pierce:** `sgATPIERCE` (type 40) with a point of the open sketch and a curve crossing its plane: a segment of
    another sketch (selected with `Select4`, or `SelectByID2("Line1@Sketch1", "EXTSKETCHSEGMENT")`) or a body edge. The
    point moves to the crossing. VERIFIED (line of a Top sketch → (20, 0) on Front; block edge → (50, 20)).
    `sgCOINCIDENT` with an edge that crosses the plane does nothing.
  - **Merge:** `sgMERGEPOINTS` on two **segment end points** joins them into one point, with no relation (one fewer
    sketch point). One of the two `ISketchPoint` objects is then dead (`RPC_E_DISCONNECTED`), so find the points again
    through their segments. It ignores a free point. `sgCOINCIDENT` on two end points merges them the same way; on a free
    point it adds a coincident relation (type 9) and moves the second point onto the first. VERIFIED.
  - Coincident point + line puts the point on the **infinite** line. VERIFIED.
  - Verify by counting with `ISketch.RelationManager.GetRelationsCount(0)`. Relation types (`swConstraintType_e`) include
    1 distance, 2 angle, 4 horizontal, 5 vertical, 6 tangent, 9 coincident, 11 symmetric (mirror copies), 12 midpoint,
    13 intersection, 47 sketch offset and 57 patterned.
- **Origin** in a sketch: `SelectByID2("Point1@Origin", "EXTSKETCHPOINT", 0, 0, 0, …)` (selection type 25). Dimensions
  and coincident relations to it work. VERIFIED.
- **Dimensions.** `IModelDoc2.AddDimension2(x, y, z)` returns a `DisplayDimension`; call `.GetDimension2(0)` and
  `.SetSystemValue3(value, swSetValue_InThisConfiguration, null)`.
  - It **opens a modal "Modify" dialog** if the system toggle `swInputDimValOnCreate = 10` is true, which is the user
    default. Switch it off in a PreferenceScope.
  - The text point is in **model** coordinates. For two lines it decides which angle is measured: on the Top plane the
    same point read as sketch coordinates gave 126.87° instead of 53.13°. On a line or two points, `AddDimension2`
    gives the aligned distance wherever the text is; `AddHorizontalDimension2` / `AddVerticalDimension2` give the
    components. VERIFIED.
  - `IParameter("D1@Sketch1").SystemValue` reads and writes values.
  - Setting a value through the parameter and rebuilding (`EditRebuild3`) **closes an open sketch**.
  - A dimension made driven by inference relations can't be changed (`DrivenState == 2`).
- **Patterns** (all VERIFIED on instance positions; counts include the seed).
  - `ISketchManager.CreateLinearSketchStepAndRepeat(nX, nY, dx, dy, angX, angY, "", false, false, false, false, false)`
    (12 args). Both angles are absolute directions from sketch +x; copies keep their orientation.
  - `CreateCircularSketchStepAndRepeat(R, A, count, spacing, true, "", false, false, false)`:
    - The center is the **middle of the selection's bounding box** (all seeds together; arcs by their real extent)
      **+ R·(cos A, sin A)**.
    - `spacing` is the angle between instances, **positive = clockwise**.
    - When `count × |spacing|` exceeds 360°, the instances are spaced 360°/count apart instead (3 at 150° came out
      120° apart).
    - It returns False for a **negative A** (use 270°, not −90°) and for **R = 0**.
    - It adds one extra sketch point at the pattern center.
    - **Avoid failing calls.** After four failed calls in one document, `CloseDoc` and `QuitDoc` raised
      `RPC_E_SERVERFAULT` while that document was active. It closed once another document was active.
- **Mirror.** `IModelDoc2.SketchMirror()` with the entities on mark **1** and the mirror line on mark **2**; a solid
  line works as the axis. With everything on mark 0, a centerline in the selection becomes the axis (even one meant to
  be copied), and a solid line plus another line mirrors nothing. Copies get symmetric relations (type 11), a mirrored
  circle reports rotation direction −1, and free points are mirrored too. VERIFIED.
- **Trim.** `ISketchManager.SketchTrim(0 /*closest*/, x, y, 0)` removes the piece nearest the pick point (sketch
  coordinates); the verified recipe selects the segment first. A circle cut by a line becomes the remaining arc.
  VERIFIED.
- **Extend.** `SketchExtend(x, y, 0)` with the segment selected, the pick point near the end to extend, in **sketch**
  coordinates (on the Top plane the model coordinates of the same point extended the other end). Arcs extend along their
  circle. Without a selection it returns False. VERIFIED.
- **Offset.** `SketchOffset2(d, bothDirections, chain, 0, 0, false)`:
  - A positive `d` goes **outward** on a closed loop, whatever its direction; on an open chain it goes to the
    **right of the first selected segment** (seen from its start to its end).
  - A negative `d` goes the other way, and `bothDirections` makes both.
  - `chain=true` with one segment selected takes the whole connected loop or chain.
  - VERIFIED on loop sizes and arc radii (100×60 ±5 → 110×70 / 90×50, r5 ±2 → r7 / r3).
- **Fillet.** `ISketchManager.CreateFillet(r, swConstrainedCornerKeepGeometry)` on two selected segments. VERIFIED.
- **Chamfer.** `CreateChamfer(type, D, X)` on two selected segments:
  - Types: 0 = distance-angle, 1 = distance-distance, 2 = equal distance.
  - **D goes along the second selected segment and X along the first.**
  - For distance-angle, X is the angle, measured from the second segment.
  - Type 2 with two different values behaves like type 1, so pass the same value twice.
  - VERIFIED on the trimmed lengths.
- **Undo in a sketch.** `EditUndo2(1)` undoes one offset, chamfer, pattern, mirror, relation, merge or extend and keeps
  the sketch open. After a call that changed nothing it undoes an earlier step; once that closed the sketch. VERIFIED.
- **Convert entities.** `ISketchManager.SketchUseEdge3(false, false)` with model edges (or a face, for its whole
  outline) selected. VERIFIED (a 120° r30 arc becomes Arc2, 62.832 mm; a 100×60 face gives 4 lines, 320 mm).

## 4. Features (all VERIFIED by volume unless noted)
| Feature | Call | Notes |
|---|---|---|
| Boss extrude | `IFeatureManager.FeatureExtrusion3(Sd, Flip, Dir, T1, T2, D1, D2, Dchk1, Dchk2, Ddir1, Ddir2, Dang1, Dang2, OffRev1, OffRev2, TransSurf1, TransSurf2, Merge, UseFeatScope, UseAutoSelect, T0, StartOffset, FlipStartOffset)` (23) | Works on the open sketch or the selected sketch. Mid-plane is `T1 = 6`. Draft: `Dchk1 = true` with `Dang1` in radians. Front Plane extrudes toward +Z, Top Plane toward +Y. |
| Cut extrude | `FeatureCut4` (27 args: …, NormalCut, UseFeatScope, UseAutoSelect, AssemblyFeatureScope, AutoSelectComponents, PropagateFeatureToParts, T0, StartOffset, FlipStartOffset, OptimizeGeometry) | **The default direction is opposite the sketch normal.** `Dir=true` reversed it. Through all, both sides: `Sd=false, T1=1, T2=1`. `T1=9` with `Sd=true` returned null. ΔV exact (−1570.8 for a Ø10 hole through 20). |
| Thin extrude | `FeatureExtrusionThin2` (30) | Untested. |
| Revolve | `FeatureRevolve2(SingleDir, IsSolid, IsThin, IsCut, ReverseDir, BothDirUpToSame, Dir1Type, Dir2Type, Dir1Angle, Dir2Angle, OffRev1, OffRev2, OffDist1, OffDist2, ThinType, Thin1, Thin2, Merge, UseFeatScope, UseAutoSelect)` (20) | The sketch has exactly one centerline, so the axis is auto-selected. V = 37 699.1 exact. |
| Fillet | `FeatureFillet3(195, r, 0, 0, 0, 0, 0, Empty×7)` | 195 = propagate (1) + uniform (2) + attach edges (64) + keep features (128). ΔV = −536.5 exact. |
| Chamfer | `InsertFeatureChamfer(4, 1, d, angleRad, 0, 0, 0, 0)` | Options 4 = tangent propagation (+1 flip, +2 keep). Type 1 = angle-distance, 2 = distance-distance, 16 = equal distance (untested). ΔV = −450 exact. **Type 0 produced an empty feature that still reported success.** |
| Shell | `IModelDoc2.InsertFeatureShell(t, outward)` (void) | The faces to remove are pre-selected. Read the new feature from `FeatureByPositionReverse(0)`. `IFeatureManager` has no shell method. VERIFIED visually. |
| Sweep | `InsertProtrusionSwept4(Propagate, Alignment, TwistCtrl, KeepTangency, AdvSmoothing, StartMatch, EndMatch, IsThin, T1, T2, ThinType, PathAlign, Merge, UseFeatScope, UseAutoSelect, TwistAngle, MergeSmoothFaces, CircularProfile, CircularDia, Direction)` (20) | Marks: profile **1**, path **4**, guides 2. V = 3926.99 exact. `CreateDefinition(swFmSweep)` returned null. |
| Loft | `InsertProtrusionBlend2(Closed, KeepTangency, ForceNonRational, TessTol, StartMatch, EndMatch, StartTanLen, EndTanLen, StartTanDir, EndTanDir, IsThin, T1, T2, ThinType, Merge, UseFeatScope, UseAutoSelect, GuideInfluence)` (18), called as `(false, true, false, 1, 0, 0, 1, 1, true, true, false, 0, 0, 0, true, true, true, 0)` | Profiles in order, each on mark **1**; guides on 2. Bounding box VERIFIED. |
| Helix | `IModelDoc2.InsertHelix(reversed, clockwise, tapered, outward, definedBy, height, pitch, revs, taper, startAngle)` (void), with definedBy 0 = pitch & revolutions, 1 = height & revolutions, 2 = height & pitch, 3 = spiral | Creates `"Helix/Spiral1"`, selected with type `REFERENCECURVES`. The **start point is the circle point on the sketch's −Y side** (Top Plane circle at (50, 0), r 8 → model (50, 0, 8)) and it climbs along the sketch normal. The start is readable through `GetSpecificFeature2 → IReferenceCurve.GetFirstSegment().GetStartVertex().GetPoint()`. Spring: profile circle on a Right-parallel plane at the start, then sweep. V = 631.54 (Pappus 631.65). **If the profile isn't at the start, the sweep silently covers part of the path.** |
| Hole Wizard | `HoleWizard5(GenericType, Standard, FastenerType, Size, EndType, Dia, Depth, Length, V1..V12, ThreadClass, RevDir, FeatScope, AutoSelect, AsmScope, AutoSelComp, PropagateToParts)` (27) | Select the **face at the hole center** (the hole is placed exactly at the pick point). Pick with `SelectByRay` along the face normal: `SelectByID2` with coordinates picks through the screen and its pick point is rounded to a pixel (0.1–0.15 mm off, VERIFIED). **Through all = `swEndCondThroughAllBoth` (9)**: with `swEndCondThroughAll` (1) `HoleWizard5` returns nothing whatever the depth (VERIFIED). Clearance (screw clearances fastener types), counterbore and countersink VERIFIED live by their faces. Simple drill: `(2 hole, 1 ANSI metric, 39 drill sizes, "Ø6.0", 0 blind, 0.006, 0.010, -1, -1×12, "", false, true, true, true, true, false)`. **Every unused value must be −1; zeros gave a Ø25.4 hole with drill angle 0.** Tapped (4, 1, 43, `"M6x1.0"`) with all −1 gave a Ø50.8 hole. Dia 0.005, depth 0.012, V1 0.01, V2 0.005, V3 0.012 gave a Ø5 drill plus a Ø6 × 5 section: semantics unclear, always verify the faces. `Ø` = `Ø`; feature names contain it. |
| Linear pattern | `FeatureLinearPattern5(n1, s1, n2, s2, flip1, flip2, "", "", geomPattern, vary, hasOff1, hasOff2, ctrlNum1, ctrlNum2, fromCentroid1, fromCentroid2, revOff1, revOff2, off1, off2, d2SeedOnly, syncSub)` (22) | Direction edge or axis on mark **1** (dir2 on mark 2), features on mark **4**. The count includes the seed. For an edge parallel to Z the instance went toward −Z, so check and flip. ΔV per instance exact. |
| Circular pattern | `FeatureCircularPattern5(n, totalRad, flip, "", geom, equalSpacing, vary, sync, dir2, symmetric, n2, s2, "", eq2)` (14) | Axis on mark **1**, features on mark **4**. VERIFIED. |
| Mirror | `InsertMirrorFeature2(mirrorBody=false, geom=false, merge=false, knit=false, 0)` | Feature on mark **1**, plane on mark **2**. VERIFIED (volume and symmetric center of mass). |
| Delete | `IModelDocExtension.DeleteSelection2(opt)` with opt 0 = feature only, 1 = + children (dependent features), 2 = + absorbed (for example the sketch) | VERIFIED. |
| Suppress | `IFeature.SetSuppression2(swSuppressFeature = 0 / swUnSuppressFeature = 1, swThisConfiguration = 1, null)` | Per configuration. |
| Rebuild state | `IFeature.GetErrorCode2(out bool isWarning)` gives 0 = OK. `GetTypeName2()` returns types like `ProfileFeature`, `3DProfileFeature`, `Extrusion`/`ICE` (API extrudes show `ICE`), `Cut`, `Revolution`, `Sweep`, `Blend` (loft), `Fillet`, `Chamfer`, `Shell`, `HoleWzd`, `LPattern`, `CirPattern`, `MirrorPattern`, `RefPlane`, `RefAxis`, `RefPoint`, `Helix`, `ExtruRefSurface`, `SheetMetal`, `SMBaseFlange`, `FlatPattern`, `WeldmentFeature`, `MateGroup`, `MateCoincident`, `MateDistanceDim`. | **A failed call still uses up the feature name**: always read `IFeature.Name`. |

- **Draft** (VERIFIED). `IFeatureManager.InsertMultiFaceDraft(angle, flip, false, 0, false, false)` with the neutral plane or
  face on mark 1 and the faces to draft on mark 2. The Draft feature owns no faces: the tilted faces still report the
  feature they came from, and `Draft1.GetFaceCount()` is 0, so check the faces of the bodies. 5° on the four sides
  of a 100 × 60 × 20 block about its bottom face: V = 125 680.9 mm³ (the analytic tapered block). A rollback rebuilds
  the part, which disconnects faces resolved before it: resolve again before retrying.
- **Fillets overflow** (VERIFIED). A constant-radius fillet doesn't fail when the radius is larger than a neighbouring
  face: r50 and even r1000 on the top edge of a 100 × 20 × 60 block rebuild without an error (r1000 leaves 7 099 mm³
  of the block). Check the volume change, not the error code, to catch a runaway radius. A shell thicker than the
  part does fail.
- **Boss or cut** from the feature type name (`Cut`, `RevCut`): `IExtrudeFeatureData2.IsBossFeature()` reads false for an
  API-made boss extrude (`ICE`). VERIFIED.

## 5. Reference geometry
- **Plane.** Select the base plane or entity (marks 0/1/2 for multiple constraints), then
  `IFeatureManager.InsertRefPlane(c1, v1, c2, v2, c3, v3)`. VERIFIED for an offset (`8` = distance, `+256` = flip).
  Other constraints: 1 parallel, 2 perpendicular, 4 coincident, 16 angle, 128 mid-plane, 32 tangent, 64 project.
- **Axis.** Select two planes, then `IModelDoc2.InsertAxis2(true)`, which returns a bool; the new feature is the last one.
  VERIFIED. Select it later with type `AXIS`.
- **Point at XYZ.** Open a 3D sketch, `CreatePoint(x, y, z)`, close it, select `EXTSKETCHPOINT` at XYZ, then
  `IFeatureManager.InsertReferencePoint(7 /*swRefPointSketchPoint*/, 0, 0, 1)`, which returns an `object[]`. VERIFIED.
- **Plane frame.** `IRefPlane.Transform` for a reference plane; for standard planes the frames are known.

## 6. Bodies and surfaces
- **Body names** come from `IBody2.Name`, which is the name of the last feature that modified the body. List with
  `IPartDoc.GetBodies2(0 solid / 1 sheet, visibleOnly)`.
- **Move/copy** (VERIFIED). Body selected (`SOLIDBODY`, mark 1), then
  `InsertMoveCopyBody2(tx, ty, tz, tdist, rpx, rpy, rpz, rax, ray, raz, copy, numCopies)` (12 args).
- **Delete body** (VERIFIED). `InsertDeleteBody2(keepBodies=false)` with the body selected.
- **Combine subtract** (VERIFIED). Main body mark 1, tool body mark 2, then
  `InsertCombineFeature(15902 /*SWBODYCUT*/, null, null)`.
  - **Combine add (15903): UNRESOLVED.** Every variant returned null, even with overlapping bodies: marks 2/2, 1/1 and
    1/2, and arrays with main null, tools = both, and main = body. Investigate the `ICombineBodiesFeatureData` definition
    route.
- **Extruded surface** (VERIFIED). On the open sketch: `FeatureExtruRefSurface3(Sd, Dir, StartCond, Offset, T1, T2, D1, D2,
  …)` (22, void).
- **Thicken** (VERIFIED, exact volume). The surface **body** is selected with **mark 1** (`SURFACEBODY`), then
  `FeatureBossThicken(t, dir 0/1/2, 0, false, true, true, true)`. Selecting the face, or omitting the mark, returned null.
- Untested: `IModelDoc2.InsertPlanarRefSurface()`, `InsertSewRefSurface(…)` (knit), `InsertOffsetSurface(t, rev)` and
  `InsertFillSurface2(…)`.

- **Split a body** (VERIFIED). `PreSplitBody2` returns the pieces, but `PostSplitBody2` (and the older pair) created nothing in
  every variant tried (marks, selected pieces, origins/paths arrays, real save paths, consume-cut). **Intersect does it**:
  select the bodies and the plane, `PreIntersect2(false, 0)`, then `PostIntersect(null, false /*merge*/, true)`: a
  100 × 60 × 20 block split by the Right plane gives two bodies of 60 000 mm³.
- **Knit** (VERIFIED). `InsertSewRefSurface(false, true, true, 1e-5, 1e-4)` needs the surface bodies on **mark 1** (mark 0, faces,
  a larger tolerance or gap filters create nothing). `TryToFormSolid` did not make a solid of a closed tube + caps;
  `FeatureBossThicken(t, 0, 0, true /*FillVolume*/, true, true, true)` with the closed surface on mark 1 does.
- **Delete face** (VERIFIED 2026-10-23, also on an imported STEP body). Faces on mark 1, then
  `IModelDocExtension.InsertDeleteFace(swFaceDeleteOption_e)`: 1 patch (the neighbours grow over the gap: a Ø10 through
  hole in a 100 × 20 × 60 block comes back as the full 120 000 mm³ block), 2 fill, 3 fill with tangency, 0 delete only
  (the block becomes an open surface body with 5 faces). The feature type is **`DelFace`**.
- **Move face** (VERIFIED). Faces on mark 1, then `FeatureManager.InsertMoveFace3(type, reverse, angle, distance,
  translation[3] m, rotation, swEndCondBlind, 0)`: type 0 offset (`distance` along each face's normal **out of the
  material**: a hole's face moves towards its axis, r 5 → 3 for +2 mm; `reverse` goes into it), 1 translate (the triad
  vector; a hole moves with its axis). The feature type is `MoveFace`. Moving a hole off the block fails (rolled back).
- **Face normals** (VERIFIED). `IFace2.FaceInSurfaceSense()` is **true when the face normal and the surface normal point
  in opposite directions** (the API help says so, despite the name). The outward normal of a face at a point is the
  surface normal from `ISurface.EvaluateAtPoint` (first three values), reversed when it returns true.

- **Weldment structural members** (VERIFIED 2026-10-23; UNRESOLVED in the old server). With the path sketch finished:
  `FeatureManager.InsertWeldmentFeature()` (once: a `WeldmentFeature`), `CreateStructuralMemberGroup()`, its `Segments`
  = the sketch's lines and arcs as a `DispatchWrapper[]` (read back with `GetSegmentsCount`), `ApplyCornerTreatment`,
  `CornerTreatmentType` (`swSolidworksWeldmentEndCondOptions_e`: 1 miter, 2 butt1, 3 butt2), `Angle` (rad); then
  `InsertStructuralWeldment5(profilePath, 1, false, new[] { new DispatchWrapper(group) }, size)`. Profiles are
  `<weldment profiles>\<standard>\<type>.sldlfp` (`swFileLocationsWeldmentProfiles` 29, e.g.
  `ProgramData\SOLIDWORKS\SOLIDWORKS 2025\weldment profiles\iso\square tube.sldlfp`) and **each size is a configuration**
  of the file (`ISldWorks.GetConfigurationNames(path)`: "40 x 40 x 4"). The feature is `WeldMemberFeat`, one solid body
  per segment. A 400 × 300 rectangle in ISO square tube 40 x 40 x 4 gave 534.8 mm² of section per mm on every member,
  mitered members 20 mm (half the profile) longer than their segment at each end. A group holds contiguous (or parallel)
  segments: a table frame from one 3D sketch is two calls, the top rectangle (miter) and the legs (no corner treatment).

## 7. Sheet metal
- **Base flange** (VERIFIED, exact volume).
  - The sketch must be **closed and selected**; an open sketch returns null.
  - Call `InsertSheetMetalBaseFlange2(t, thickenDir=false, r, 0.02, 0.01, false, 0, 0, 1 /*DirToUse*/, null /*custom BA*/,
    false /*UseDefaultRelief*/, 0, 0.0001, 0.0001, 0.5, true, false /*merge*/, true, true)`.
  - It creates `Sheet-Metal1`, `Base-Flange1` and `Flat-Pattern1`, thickened toward −normal.
- **Flatten** (VERIFIED). On the feature of type `FlatPattern`: `SetSuppression2(1 /*unsuppress*/, 1, null)` flattens it,
  and `(0, 1, null)` folds it again.
- **Flat DXF** (VERIFIED). `IPartDoc.ExportToDWG2(path, modelPath /*saved part*/, 1 /*sheet metal*/, true, null, false,
  false, 1 + 4 /*geometry + bend lines*/, null)`. Without a layer map everything is on layer `0`: the outline is
  `Continuous` and each **bend line is a `HIDDEN` LINE** (VERIFIED on an 80 x 40 blank with one sketched bend: 5 LINEs,
  extents exactly 80 x 40 with K = 0.5).
- **Edge flange.** `InsertSheetMetalEdgeFlange2(edges[], null, 129, π/2, r, 1, 0, 1, 0.5, 0, 0, 1, null)` with edges alone
  returned null. Profile sketches are needed: the server makes them with `IModelDoc2.InsertSketchForEdgeFlange` and passes
  them to `InsertSheetMetalEdgeFlange2` (the live tests pass; the result's `method` names the variant that worked).
- **Weldments**: structural members are VERIFIED, see §6 (*Weldment structural members*).
- **Hem** (VERIFIED 2026-10-23). The edge picked or selected (mark 0), then `FeatureManager.InsertSheetMetalHem2(type,
  position, reverse, length, gap, angle, radius, miterGap, null /*the sheet's bend allowance*/, true, 0, 0, false, 0.5, 0, 0)`
  (types `swHemTypes_e`: 0 open, 1 closed, 2 teardrop, 3 rolled; positions 0 inside, 1 outside). It makes `Hem1` with a
  sketch, `HemBend1` and its flatten feature; **the faces belong to `HemBend1`**, not to the Hem. On the 2 mm test sheet a
  closed 10 mm and an open 8 mm (gap 1.5) hem, a rolled hem (r 2, 180°) and a teardrop with r 1 mm at 200° outside worked;
  **teardrops at 270°** (r 1 or 2, inside or outside) **returned nothing**.
- **Sketched bend** (VERIFIED). A sketch with one line on the sheet's face, then the sketch selected (`Select2`) and the
  fixed side of the face picked (`SelectByRay` onto the face, append), then `FeatureManager.InsertSheetMetal3dBend(angle,
  useDefaultRadius, radius, flip, bendPos /*0 centerline, 1 material inside, 2 material outside, 3 bend outside*/, null)`:
  a `SM3dBend` feature ("Sketched Bend1"). Bending the 15 mm side of the 50 x 30 x 2 plate up by 90° kept the volume, and
  nothing reached past the line any more.
- **Miter flange** (VERIFIED 2026-10-24). An open profile sketch on a plane normal to the first edge at the chain's free
  end (the sheet's end face there, or a `RefPlane` from the edge (perpendicular) and the end vertex (coincident)) holding
  one line from the vertex away from the face, then the sketch selected on **mark 0** and the edges on **mark 1**, then
  `FeatureManager.InsertSheetMetalMiterFlange(useDefaultRadius, r, ripGap, true /*default relief*/, false, 0.5, 0.0001,
  0.0001, 1 /*rectangular*/, false, flangePos /*1 material inside, 2 outside, 3 bend outside*/, 0, 0, null)`: a
  `SMMiteredFlange` ("Miter Flange1") with one `Miterbend<n>` sub-feature per edge. On the 50 x 30 x 2 plate (r 1) a 10 mm
  profile on one 50 mm edge added exactly 50 x (12 + 2π) mm³ (material inside: the base loses r + t), 50 x (16 + 2π)
  (outside) and 50 x (18 + 2π) (bend outside); two edges round a corner, three (a U) and all four (a tray) work, and the
  flat blank with K 0.5 is exact (the tray's 68.283 x 48.283). The new feature sits before `Flat-Pattern1`, which stays
  last in the tree.
- **Jog: UNRESOLVED.** `IModelDoc2.InsertSheetMetalJog(angle, radius, offset, flip, fixProjLen, 1, 1)` with the same
  selections (the fixed face on mark 0, 1 or 2) made nothing.

- **Base flange side** (VERIFIED). For a closed profile `InsertSheetMetalBaseFlange2`'s `ThickenDir` changes nothing (both values
  thicken toward −normal), and neither does `IBaseFlangeFeatureData.ReverseThickness`; `ReverseDirection` (through
  `ModifyDefinition`) moves the plate to the + side (y −2..0 → 0..2 mm).

## 8. Assembly
- **Interference** (VERIFIED). `IInterferenceDetectionMgr.GetInterferences` answers with the options the manager had when
  it was obtained: after setting `TreatCoincidenceAsInterference = true`, call `GetInterferenceCount()` first (it
  recomputes), then `GetInterferences`. Touching cubes: 0 listed before the count, 1 (volume 0) after it.
- **Mate entities** (VERIFIED). `IMateEntity2.ReferenceType` is the geometric kind (`swMateEntity2ReferenceType_e`:
  3 plane, 4 cylinder); `ReferenceType2` is a `swSelectType_e` (4 = datum plane).
- **Insert** (VERIFIED). `IAssemblyDoc.AddComponent5(path, 0, "", false, "", x, y, z)` returns `Component2`.
  - **x, y, z place the component's bounding-box center, not its origin.** A plate whose box center was at y = 17.5 had its
    origin placed at y = −17.5. Correct it with `Component2.Transform2` (a `MathTransform` built from a 16-element array:
    rotation 0..8, translation 9..11, scale 12).
  - **The first component is fixed automatically.**
  - VERIFIED again from C# (2026-10-22): a 40 x 10 x 20 part whose box center is (20, 5, -10) mm, inserted at
    (100, 200, 300) mm, got the translation (80, 195, 310) mm; `Transform2.ArrayData` = rotation 0..8, translation
    9..11 (m), scale 12.
- **Coordinate spaces in assemblies** (VERIFIED): bodies from `IComponent2.GetBodies3` report their geometry in the
  component's **part space**. That covers `IBody2.GetBodyBox`, `IFace2.GetClosestPointOn` and the surface and curve
  parameters. `IComponent2.GetBox(false, false)` is in **assembly space**. Point queries must therefore map the
  assembly point into component space with the inverse of the component transform, then map results back. The
  server's `TopoQuery` does this.
- **Mates** (VERIFIED for planes). Select entity 1 and entity 2 on mark 1, then
  `IAssemblyDoc.AddMate5(type, align, flip, d, dMax, dMin, gearNum, gearDen, angle, angMax, angMin, forPositioningOnly,
  lockRotation, widthOption, out int errorStatus)` (15). An error status of 1 means no error.
  - Types (`swMateType_e`): 0 coincident, 1 concentric, 2 perpendicular, 3 parallel, 4 tangent, 5 distance, 6 angle,
    8 symmetric, 10 gear, 11 width, 13 rack-pinion, 15 path, 16 lock, 17 screw, 18 linear coupler, 19 universal joint,
    21 slot, 22 hinge, 23 slider, 24 profile center, 25 magnetic.
  - Alignment: 0 aligned, 1 anti-aligned, 2 closest.
  - Component plane names: `"Front Plane@part-1@Assem1"`.
- **Mate dimensions** (`"D1@Distance1"`) can be driven through `IParameter`.
- **Advanced and mechanical mates** (VERIFIED 2026-10-23). Select the entities, then `IAssemblyDoc.CreateMateData(type)`,
  put the selected objects into the data and `IAssemblyDoc.CreateMate(data)`:
  - limit distance / angle: `IDistanceMateFeatureData` / `IAngleMateFeatureData` with `IsAdvancedMate = true`,
    `MinimumDistance` / `MaximumDistance` (`MinimumAngle` / `MaximumAngle`); the type stays 5 / 6 and the limits read back;
  - symmetric (8): `EntitiesToMate` = the two entities, `SymmetryPlane` = the plane (a cube's face mirrored about the
    Right plane landed at the mirror position);
  - width (11): `WidthSelection` = the two width faces, `TabSelection` = the two tab faces, `ConstraintType` 0 centred;
  - hinge (22): `EntitiesToMate` is indexed by `swHingeMateEntityType_e`: [0] the concentric pair, [1] the coincident pair;
  - gear (10): `GearRatioNumerator` = a, `GearRatioDenominator` = b; **they read back the other way round** (20:40 reads
    40:20, entities still in the order a, b). A rebuild doesn't turn the second gear when the first is moved: gear mates
    act while dragging;
  - rack and pinion (13): `EntitiesToMate` indexed by role ([0] rack: a straight edge, [1] pinion: a round face),
    `DiameterType` 1 (rack travel per revolution), `DiameterVal` (m) reads back;
  - **screw (17): UNRESOLVED.** `RevolutionVal` stays at the default 1 mm per revolution whatever is set (distance per
    revolution or revolutions per unit length, at creation or through `ModifyDefinition`), so the tool refuses screws.
  - Object arrays as plain `object[]` work for these data objects.
- **Fix/float.** Select the component, then `IAssemblyDoc.FixComponent()` / `UnfixComponent()`.
- **Interference** (VERIFIED). `IAssemblyDoc.InterferenceDetectionManager`; set
  `TreatCoincidenceAsInterference = false` and `TreatSubAssembliesAsComponents = true`; then `GetInterferenceCount()`,
  `GetInterferences()` (`.Volume` in m³, `.Components`), and `Done()`.
- **Replace.** `ReplaceComponents2(path, config, allInstances, useConfigChoice, reattachMates)`. Plane mates survived a
  replacement.
- **Components.** `GetComponents(topLevelOnly)`, `Name2`, `GetPathName()`, `GetSuppression2()` (2 resolved,
  0 suppressed), `Visible` (1 = visible), `IsFixed()`, `Transform2.ArrayData`.
- **Component patterns** (VERIFIED 2026-10-23, linear 1 x 3, 2 x 2 and along a component edge; circular about a
  component's cylindrical face). Select first, then make the definition, as SOLIDWORKS' own examples do: direction 1
  (or the circular axis) on **mark 2**, direction 2 on **mark 4**, the seed components on **mark 1**; then
  `FeatureManager.CreateDefinition(swFmLocalLPattern = 108 | swFmLocalCirPattern = 109)` → `ILocalLinearPatternFeatureData`
  (`D1TotalInstances` with the seed, `D1Spacing` m, `D1ReverseDirection`, `D2…`, `D2PatternSeedOnly = false`) or
  `ILocalCircularPatternFeatureData` (`TotalInstances`, `EqualSpacing`, `Spacing` = the total angle with equal
  spacing, else the step, rad); then `CreateFeature(data)`.
  - Filling an empty definition instead does not work: setting `SeedComponentArray` to an `object[]` makes SOLIDWORKS
    fault (RPC_E_SERVERFAULT); with a `DispatchWrapper[]` the seeds read back but `D1Axis` never takes (`GetD1AxisType`
    −1, `D1TotalInstances` back to 1) and `CreateFeature` returns null, for a plane, its `IRefPlane` and a selected edge.
  - A **reference plane** works as a direction (its normal). Along a standard plane's normal SOLIDWORKS runs **+**
    (Right +x, Top +y, Front +z), so "−x" needs `D1ReverseDirection = true`. Along an edge the sense is its own.
  - Type names: `LocalLPattern`, `LocalCirPattern`; each instance is a component feature of type `ReferencePattern`.
    Deleting the pattern feature (`DeleteSelection2(0)`) deletes its instances.
- **Exploded views** (VERIFIED 2026-10-23). `IAssemblyDoc.CreateExplodedView()` (the new name from
  `GetExplodedViewNames`), `ShowExploded2(true, name)`, then per step: the components on mark 1 and
  `IConfiguration.AddExplodeStep2(dist m, swExplodeDirectionIndex_e (0 x, 1 y, 2 z), reverse, 0, -1, false, true, false,
  out err)` and `EditRebuild3`. The manipulator axes are the assembly's: "−x 40" moved a component by (−40, 0, 0).
  - **A new view can come with steps SOLIDWORKS made itself**: with a shaft in the assembly, a cube moved 37 to 62 mm in
    z on top of its own step, depending on the layout. Delete them (`GetExplodeStep(i).Name`, `DeleteExplodeStep`) before
    adding yours.
  - Read the collapsed positions before exploding: `IComponent2.GetSpecificTransform(true)` was 40.7 mm off in z for
    that cube.
  - Delete a view by selecting it: `SelectByID2(name, "EXPLODEDVIEWS", …)` (the type name of `swSelEXPLVIEWS`), then
    `DeleteSelection2`.
- **Mirror components** (VERIFIED). `IAssemblyDoc.MirrorComponents3(plane, ComponentsToInstance, orientations,
  orientAboutCenterOfMass:false, ComponentsToMirror:null, false, null, swMirrorComponentName_Suffix, "", "", 0, false,
  false, false)` with the components as a `DispatchWrapper[]` and `swMirrorComponentOrientation_RotatePlaneY` (0) per
  component copies them as instances (no new files): a `MirrorCompFeat` feature, its copies `ReferencePattern`. A copy's
  part bounding-box centre lands on the reflection of the seed's; about the Front plane its axes came out x, y mirrored
  and z flipped (a rigid copy can't mirror all three).
- **Opposite-hand components** (VERIFIED 2026-10-24). `MirrorComponents3` with the seeds as `ComponentsToMirror` (from
  VBA) returned Empty and made nothing. What works is `FeatureManager.CreateDefinition(swFmMirrorComponent = 116)` as
  `IMirrorComponentFeatureData`: `MirrorPlane` (the plane feature), `MirrorType` 0 (bounding-box centre),
  `OppositeHandComponents` (`DispatchWrapper[]`), `CreateDerivedConfigurations = false`, `PlaceFilesInOneFolder = true`,
  `MirrorComponentsFolderLocation` (no trailing backslash), `MirroredComponentFilenames` (names without extension) with
  `NameModifierType = swMirrorComponentName_Custom` (2), `ReplaceFileLocations` (one "" per name),
  `MirrorTransferOptions = ImportSolids` (1), `BreakLinksToOriginalPart = false`, **`PreserveZAxis = false`**, then
  `CreateFeature`: a `MirrorCompFeat` whose new component references the new part file, which SOLIDWORKS writes at once
  (its path comes back lower-cased). With `PreserveZAxis = true` (SOLIDWORKS' example) the copy sat turned 180° about y
  off its mirror image; with false, a handed L at x 40..70 landed exactly on x -70..-40, every vertex reflected.

## 9. Drawing
- **Default drawing template:** US Letter (279.4 × 215.9), 1:1, third angle, **no sheet format**, units **CGS**. Set MMGS
  with document preference 263.
- **Sheet** (VERIFIED). `IDrawingDoc.SetupSheet5(name, paper, template, s1, s2, firstAngle, formatPath, w, h, "Default",
  true)`. Paper sizes: 6 A4, 7 A4 portrait, 8 A3, 9 A2, 10 A1, 11 A0, 12 user-defined. Template: 12 = custom format file,
  13 = none. Formats live at `ProgramData\SOLIDWORKS\SOLIDWORKS 2025\lang\english\sheetformat\a3 - iso.slddrt` and so on.
- **Views** (VERIFIED). `CreateDrawViewFromModelView3(modelPath, "*Front" | "*Top" | "*Right" | "*Isometric" | …, x, y, 0)`,
  with x, y the view center on the sheet **in meters**. `Create1stAngleViews2(path)` / `Create3rdAngleViews2(path)`
  creates three standard views.
- **Projected view:** select the parent `DRAWINGVIEW`, then `CreateUnfoldedViewAt3(x, y, 0, false)`. The direction follows
  the position.
- **View objects** (VERIFIED). Methods on the `IView` objects `ISheet.GetViews` / `IDrawingDoc.GetViews` return
  (`GetAnnotations`, `AutoInsertCenterMarks2`, counts) did nothing; the same calls on views from `GetFirstView` /
  `GetNextView` work. A heavily used session can also stop listing a view's annotations: restart SOLIDWORKS.
- **Session state (open issue).** After other modeling in the same session (here: a plate with a fillet, holes and a
  screenshot), `AutoInsertCenterMarks2` and `InsertModelAnnotations3` sometimes stopped adding anything, and new views
  listed no annotations, until SOLIDWORKS was restarted. Neither alone reproduced it (two screenshots, or the plate
  without a screenshot); forcing a rebuild and `UpdateViewDisplayGeometry` didn't help. Cause unknown.
- **Don't pick to confirm** (VERIFIED). A `SelectByID2` pick at a sheet point (to confirm a cut line or detail circle)
  deactivated the parent view; the entity then went to the sheet's sketch and `CreateSectionViewAt5` /
  `CreateDetailViewAt4` made nothing. Draw with the view active and `AddToDB` on, in sheet coordinates (m), and select
  the segment with `Select4(false, null)`.
- **Model edges in a view** (VERIFIED). `IView.SelectEntity(edge, append)` (or `IEntity.Select4` with `SelectData.View`)
  selects the exact edge; a pick by sheet coordinates found no edge, and a failed pick with append dropped the
  earlier selection.
- **Sheet scale** (VERIFIED). An empty sheet is rescaled when its first view is inserted (1:2 set before read 1:1 after
  the rebuild): set the scale after the views exist; views with `UseSheetScale = 1` follow it.
- **BOM of a part** (VERIFIED): bomType 1 (parts only); top-level (2) returns nothing for a part.
- **Dimension between edges in a view** (VERIFIED, 60 mm).
  - `ActivateView(name)`, select edges with `SelectByID2("", "EDGE", sheetX, sheetY, 0, …)`, then `AddDimension2(x, y, 0)`
    produces `"RD1@Drawing View1"`, a reference dimension.
  - Sheet position: the view center is the model bounding-box center times the view scale. The robust mapping is
    `IView.ModelToViewTransform`.
- **Note** (VERIFIED). `InsertNote(text)`, then `.GetAnnotation().SetPosition2(x, y, 0)`.
- **Center marks** (VERIFIED). `ActivateView`, then `IView.AutoInsertCenterMarks2(1 /*holes*/, 0, true, true, true, 0.0025,
  0.0005, true, false, 0)`.
- **Section view** (VERIFIED). Activate the parent view, set `AddToDB = true`, `CreateLine` in **sheet coordinates**,
  `Select4`, then `CreateSectionViewAt5(x, y, 0, "A", 0, null, 0)` gives `"Section View A-A"`.
- **Detail view.** A circle in the parent view, `Select4`, then `CreateDetailViewAt4(x, y, 0, 0, s1, s2, "B", 0, true,
  false, false, 0)`. The view was created but **looked empty** in the export, so check it.
- **BOM** (VERIFIED). `IView.InsertBomTable4(UseAnchorPoint=false, x, y, anchor 2 = top-right, bomType 2 = top-level,
  config, templatePath, false, 0, false)`.
  - `UseAnchorPoint=true` ignores x, y and uses the format anchor, which was outside the border on `a3 - iso`.
  - Templates: `Program Files\SOLIDWORKS Corp\SOLIDWORKS\lang\english\bom-standard.sldbomtbt`.
- **Model dimensions:** `InsertModelAnnotations3(0, 32776, true, false, false, false)` returned nothing for parts whose
  sketches had no dimensions.
- **Export:** PDF (`%PDF-1.4`), DWG (`AC1015` = R2000) and DXF through `SaveAs`. A PNG export of a drawing renders the
  current window.
- **Annotations on a model edge** (VERIFIED 2026-10-23). Activate the view and select the edge, then:
  - datum: `IModelDoc2.InsertDatumTag2()` → `IDatumTag`; `SetLabel("A")`, `GetLabel()`;
  - surface finish: `IModelDocExtension.InsertSurfaceFinishSymbol3(swSFSymType_e, swSTRAIGHT, x, y, 0, swSFNone,
    swDOT_ARROWHEAD, "", "", "", "", maxRoughness, "", "")` → `ISFSymbol`; `GetSymbol()` is the type,
    `GetText(swSFSymbolMaximumRoughness = 5)` the roughness;
  - tolerance frame: `IModelDoc2.InsertGtol()` → `IGtol`; `SetFrameSymbols2(1, "<IGTOL-POSI>", diameter, "<MOD-MMC>",
    false, "", "", "", "")` and `SetFrameValues2(1, "0.1", "", "A", "B", "C")`; `GetFrameValues(1)` = [tol1, tol2, datum1,
    datum2, datum3], `GetFrameSymbols3(1)[0]` = the symbol. The codes are the ISO ones in
    `ProgramData\SOLIDWORKS\SOLIDWORKS 2025\lang\english\gtol.sym` (FLAT, STRAIGHT, CIRC, CYL, LPROF, SPROF, PARA, PERP,
    ANGULAR, POSI, CONC, SYMMETRY, SRUN, TRUN; modifiers MMC, LMC);
  - balloon: `IModelDocExtension.InsertBOMBalloon2(CreateBalloonOptions())` → `INote`; `IsBomBalloon()`,
    `GetBomBalloonText(true)` ("1" with a BOM in the drawing).
  - Each is attached (`IAnnotation.GetAttachedEntityCount3()` = 1) and moved with `SetPosition2(x, y, 0)` (sheet m).
- **Balloons for a whole view** (VERIFIED, an assembly of two parts gave balloons 1 and 2). Select the view, then
  `IDrawingDoc.AutoBalloon5(CreateAutoBalloonOptions())` (`IgnoreMultiple = true`, item numbers) returns the notes.
- **Hole table** (VERIFIED). Activate the view; the datum origin **vertex on mark 1** (picked at its sheet position) and
  the **face on mark 2** (`IEntity.Select4` with `SelectData.View`), then `IView.InsertHoleTable3(false, x, y, TopLeft,
  "A", "standard hole table--letters.sldholtbt", swHoleTableTagOrder_XY, swHoleTable_AlphaNumericTags, null)` →
  `IHoleTableAnnotation`. X LOC / Y LOC are model mm from the origin along the sheet's x and y (the Ø20 hole of the
  100 × 60 block read 50 / 30 from the lower-left corner, −50 / −30 from the upper-right one).
- **Revision table** (VERIFIED). `ISheet.InsertRevisionTable2(false, x, y, TopRight, "standard revision block.sldrevtbt",
  swRevisionTable_CircleSymbol, true)` (null when the sheet has one: `ISheet.RevisionTable`), then per row
  `AddRevision("A")` → id, `GetRowNumberForId(id)`, and `ITableAnnotation.Text[row, descriptionColumn] = …`;
  `CurrentRevision` reads the last letter. Templates come from the file locations (`swFileLocationsHoleTableTemplates`
  33, `swFileLocationsRevisionTableTemplates` 35).
- **Sheet scale of an empty sheet** (VERIFIED again): a 250 mm assembly put on a 1:1 sheet came out 1:2 after its first
  view; set the planned scale after the views exist.
- **Leaders attach where the edge was picked** (VERIFIED). `IView.SelectEntity(edge)` selects it without a point, and a
  leader then ends at the edge's start (a view corner). `SelectByID2("", "EDGE", sheetX, sheetY, z, …)` in the active view
  selects **two** objects, the view itself (type 12) and the edge: deselect the view, and the leader ends at the pick.

## 10. Export (system preferences: always restore)
- **Through `SaveAs`, by extension:** `.step/.stp`, `.igs`, `.x_t`, `.stl` (written with an **uppercase `.STL`**
  extension), `.3mf`, `.eprt`, `.pdf`, `.png` (a real PNG of the viewport, about 1805 × 986).
  - `.obj` needs the ScanTo3D add-in (error 256).
- **STEP AP** is system integer **75** (203/214/242). The user had 203.
  - VERIFIED: setting 214 makes `SaveAs` write `FILE_SCHEMA (('AUTOMOTIVE_DESIGN'))`.
  - VERIFIED on 2025 SP1.2 Premium: 75 = 242 sticks (it reads back 242), yet `SaveAs` still writes an AP203 file
    (`CONFIG_CONTROL_DESIGN`). AP242 output depends on the installation/license, so check the schema of the file.
- **STL:**
  - Binary/ASCII is system toggle **69**. Quality is integer **78** (1 coarse, 2 fine, 3 custom).
  - **The quality is derived from deviation (double 2, m) and angle (double 3, rad)** (VERIFIED by probe):
    - setting 78 to coarse or fine overwrites 2 and 3 with that preset (coarse: 0.000595238 m and 30°);
    - writing non-preset values to 2/3 turns 78 into 3 on its own;
    - writing 78 = 3 while 2/3 hold a preset does nothing.
  - So save and restore 2, 3 and 78 as one group: write 2 and 3 back first, then 78, and verify all three. Treat
    the 2/3 changes that follow a 78 write as your own (compare-and-swap would otherwise call them external and keep
    them; that left a user's options at coarse once). `PreferenceScope` does this for `SystemPrefIds.Coupled`.
  - SOLIDWORKS stores angles with its own rounding: writing 0.174532925199433 may read back 0.17453292519943336.
    Compare doubles with a relative tolerance (1e-12), not as strings.
- **STL import** (`LoadFile4`): integer **208** `swImportStlVrmlModelType` (0 graphics, 1 surface, 2 solid) decides
  what the part gets. With 0 (the default) the part has **no bodies**. Integer **210** is the unit the mesh is read
  in. VERIFIED: 208 = 2 and 210 = 0 (mm) give a solid body.
- **Never write unrelated preferences.** The old server's STL export overwrote preference 75.

## 11. Materials, properties, equations, configurations, appearance
- **Custom properties.** `ICustomPropertyManager.Add3(name, type, value, 2 /*replace*/)`: type `swCustomInfoNumber` (3) takes **whole numbers only** ("12.5" gives result 1); `swCustomInfoDouble` (5) takes "12.5" (a dot, whatever the Windows decimal separator; a comma fails), stores "12.500000" and reads back as type 3. VERIFIED.
- **Material.** `IPartDoc.SetMaterialPropertyName2("", "SOLIDWORKS Materials", name)` **silently ignores unknown names**;
  check with `GetMaterialPropertyName2("", out db)`.
  - The material databases are UTF-16 XML (`lang\english\sldmaterials\*.sldmat`), with `<classification name>` and
    `<material name>` elements.
  - The file-locations option `swFileLocationsMaterialDatabases` (string 28) can list **only the custom folders**
    (here: `Custom Materials` and `Sustainability` under ProgramData). SOLIDWORKS still loads the standard databases
    from `<GetExecutablePath()>\lang\<GetCurrentLanguage()>\sldmaterials`, so search that folder too. VERIFIED.
  - Valid names include "AISI 1020", "AISI 304", "Plain Carbon Steel", "6061 Alloy", "6061-T6 (SS)", "7075-T6, Plate
    (SS)", "Brass", "Copper", "ABS", "ABS PC", "Nylon 101", "PC High Viscosity", "PE High Density" and "PTFE (general)".
  - There is **no "PLA"** and **no "Aluminum 6061-T6"**.
- **Custom properties.** `IModelDocExtension.CustomPropertyManager(config).Add3(name, 30 /*text*/, value, 2 /*replace*/)`
  and `Get6`/`Get4`. Linked values like `"D1@Boss-Extrude1"` resolve, formatted in the locale (`20,00`). Use raw values.
- **Equations.** `IModelDoc2.GetEquationMgr()`:
  - `Add2(-1, "\"W\" = 60mm", true)`;
  - `Equation(i)` can be **assigned** to update one (VERIFIED);
  - `GlobalVariable(i)`, `Value(i)`, `Delete(i)`, `GetCount()`;
  - always write units, since bare numbers are document units.
- **Configurations.** `IConfigurationManager.AddConfiguration2(...)` / `IModelDoc2.AddConfiguration3(name, "", "", 0)`
  creates **and activates** one. `ShowConfiguration2(name)` activates; `GetConfigurationNames()` lists them.
- **Appearance.** `IModelDoc2.MaterialPropertyValues = double[9] {r, g, b (0–1), ambient, diffuse, specular, shininess,
  transparency, emission}`, followed by `GraphicsRedraw2()`. VERIFIED red.
  - `IModelDocExtension.SetMaterialPropertyValues(v, 2, null)` threw "object disconnected".
  - Per body: `IBody2.MaterialPropertyValues2`.

## 12. Measurement and inspection
- **Mass properties.** `IModelDocExtension.CreateMassProperty2()` gives `.Mass`, `.Volume` (m³), `.SurfaceArea`,
  `.CenterOfMass` and `.PrincipalMomentsOfInertia`. Per body, `IBody2.GetMassProperties(0)` returns
  `[cx, cy, cz, volume, area, mass, …]`.
- **Part box.** `IPartDoc.GetPartBox(true)` gives `[xmin, ymin, zmin, xmax, ymax, zmax]` across all bodies;
  `IBody2.GetBodyBox()` gives one body's box. Boxes on curved bodies are padded by about 0.002 mm.
- **Measure** (VERIFIED). Select two entities, then `IModelDocExtension.CreateMeasure()`, `Calculate(null)`, and read
  `Distance`, `NormalDistance`, `DeltaX/Y/Z` and `Angle`.
- **Surface queries.** `IFace2.GetSurface()` returns an `ISurface`: `IsPlane`/`IsCylinder`/…; `CylinderParams` is
  `[point xyz, axis xyz, r]`; `IFace2.Normal` gives the plane normal. `IEdge.GetCurve()` returns an `ICurve`:
  `IsLine`/`IsCircle`, `CircleParams` (`[center xyz, axis xyz, r]`), `GetLength3(t0, t1)`.
- **Geometry check.** `IBody2.Check3` returns faults. The old server's check was fake.

- **Measure with arcs/circles** (VERIFIED). `IMeasure.Distance` follows `ArcOption`, default 0 = center: a hole's top circle
  to the Top Plane under it read 20.62 (center to the hole's bottom circle on that plane), 1 = minimum gave the true 20,
  2 = maximum 22.36. Set `ArcOption = 1` before `Calculate` for "the distance".
- **Touching faces** (VERIFIED): `Distance` is -1 (not applicable); `Angle` (rad) and `IsPerpendicular` still read.

## 13. Display, views, images
- **Named views.** `IModelDoc2.ShowNamedView2("*Isometric", -1)`, then `ViewZoomtofit2()`.
- **Display mode.** `IModelView.DisplayMode` takes `swViewDisplayMode_e` values: 1 wireframe, 2 hidden lines removed,
  3 hidden lines grayed, 4 shaded, 5 shaded with edges. The old server was off by one.
- **Hiding.** Document toggles `swDisplayPlanes = 5`, `swDisplaySketches = 196`, `swDisplayAxes = 4` and
  `swDisplayOrigins = 6` hide and show reference geometry, followed by `GraphicsRedraw2()`.
- **Background** is a *system* preference (305 appearance, 99 colour): avoid it or restore it. The user had 305 = 3 and
  99 = 13813955.
- **Images.** `IModelDoc2.SaveBMP(path, w, h)` writes **BMP**. `SaveAs` to `.png` writes a real PNG of the window.
  Selected entities render light blue, so clear the selection first.

## 14. Simulation (static, VERIFIED end to end)
- **Add-in.** `ISldWorks.LoadAddIn(@"...\SOLIDWORKS\Simulation\cosworks.dll")` returns 0 when OK; there were no dialogs.
  Then `GetAddInObject("SldWorks.Simulation")` → `.CosmosWorks` (`ICosmosWorks`) → `.ActiveDoc` → `.StudyManager`.
- **Study.** `CreateNewStudy3(name, 0 /*static*/, 0 /*solid mesh*/, out err)`. Study types: 1 frequency, 2 buckling,
  3 thermal, 5 nonlinear, 6 drop, 7 fatigue.
- **Fixture.** `study.LoadsAndRestraintsManager.AddRestraint(0 /*fixed*/, faces, null, out err)`. **The faces array must be
  passed by value as a variant array.** The VBA form was `(Array(face))`; an array variable passed by reference gave
  NoEntities (10) and InvalidArray (6). From C#, pass `new object[] { new DispatchWrapper(face) }` and verify.
- **Pressure.** `AddPressure(0 /*normal*/, faces, null, out err)`, then `PressureBeginEdit()`, `Unit = 0` (SI Pa),
  `Value = 1e5`, `PressureEndEdit()`.
- **Mesh.** `study.Mesh.Quality = 1`, `GetDefaultElementSizeAndTolerance(0, out es, out tol)` (**doubles**), then
  `CreateMesh(0, es, tol)`.
- **Run.** `RunAnalysis()` returns 0 when OK; 30 means invalid loads or boundary conditions. It blocks.
- **Results.** `GetMinMaxStress(9 /*VON*/, 0, 1, null, 3 /*N/mm²*/, out err)` and
  `GetMinMaxDisplacement(3 /*URES*/, 1, null, 0 /*mm*/, out err)` return `[nodeMin, min, nodeMax, max]`.
- **Oracle.** A 100 × 10 × 10 AISI 1020 cantilever fixed at x = 0 with 0.1 MPa on top gives σvM max 32.04 MPa (beam theory
  30) and δmax 0.07530 mm (theory 0.075).
- **Cleanup.** `ISldWorks.UnloadAddIn(path)`. Closing a document that has studies prompts "save Simulation data?".
- **Beyond static** (other study types, fixtures, loads, connectors, contacts, plots): the API survey in §17.

## 15. VBA engine facts (for `vba_run`)
- **Format and runner.** `RunMacro2(path, module, proc, 0, out err)` runs plain-text `.swb` files written in SolidWorks
  BASIC.
- **Compile and syntax errors are SILENT:** nothing runs, there's no dialog, and the error is 0.
- **Runtime errors** without a handler open a modal "Microsoft Visual Basic" dialog (Continue/End/Debug/Help).
- **Reserved names:** `Log`, `Imp`, `Eqv` and `Mod` break silently as identifiers.
- **Encoding.** The file is read in cp1252; non-Latin-1 characters become `?` (use `Chr(216)` for Ø).
- **Coding rules.** Declare variables `As Object/Variant/Double/Long/String/Boolean` and use numeric enum values.
  `Environ("TEMP")` and `Array()` work.
- **Late binding** exposes only the default interface (ellipse methods give error 438). `face.Select4` and line
  `GetStartPoint2` work.
- **Locale.** `Print` and `Format` use the decimal comma; `Str()` uses a dot (and drops the leading zero: `.025`).
- **A macro can wedge the engine** (seen 2026-10-22). After a macro created an assembly, called `AddComponent5`, and
  assigned an object to `comp.Transform2` without `Set`:
  - `RunMacro2` returned while that macro still held its output file open;
  - from then on **every** `RunMacro2` returned False with error 0, and nothing ran;
  - `GetCurrentMacroPathName` raised `RPC_E_SERVERFAULT`.

  Recovery: reset the VBA project or restart SOLIDWORKS. `vba_run` detects this with a canary macro and says so,
  instead of reporting a syntax error. Always use `Set` for object assignments.
- **The engine can also be wedged before any macro ran** (seen 2026-10-24). In a session restarted before a live-test
  run that uses no VBA, the first `vba_run` found the canary failing. Closing SOLIDWORKS then crashed it ("SOLIDWORKS
  Error Report", `SLDEXITAPP.exe`, fault module `vbe7`). While that report window is open, starting SOLIDWORKS fails
  with `CO_E_SERVER_EXEC_FAILURE` (0x80080005): the report has to be closed first (it offers to send the report, so a
  person decides). Cause unknown; see M8's infrastructure items (DESIGN §7.2).
- `Print #` output is ANSI. The `vba_run` wrapper's `Echo` escapes non-ASCII characters as `\uXXXX`, and string
  literals with non-ASCII characters are rewritten to `ChrW()`, so the macro file is pure ASCII.

## 16. Win32 dialog handling
- **Discovery.** SolidWorks dialogs are top-level `#32770` windows owned by the SLDWORKS.exe process.
- **Pressing buttons.** `PostMessage(dialog, WM_COMMAND, GetDlgCtrlID(button), buttonHwnd)` works without focus;
  `BM_CLICK` does not. For owner-drawn dialogs such as Modify, send `WM_COMMAND(IDOK=1 / IDCANCEL=2)`.
- **Seen in testing:**
  - `Microsoft Visual Basic` (run-time error: Continue/End/Debug/Help);
  - `Modify` (dimension);
  - `Simulation` ("Do you want to save Simulation data?", Yes/No).

## 17. Add-in and extended APIs (survey of 2026-10-24, for M8)
Names checked against the 2025 help and the interop assemblies; behaviour **UNVERIFIED** unless a line says otherwise.
The plan that uses them is DESIGN §7.2.
- **Where.** The help is `<SOLIDWORKS>\api\*.chm` (core, Simulation `cworksapi`, Costing, Motion, Utilities, DimXpert,
  FeatureWorks, Design Checker, Inspection, Toolbox, Routing, Visualize, 3D printing, Document Manager, PDM, eDrawings,
  Sustainability); the interop assemblies are in `api\redist`.
- **On the test machine** (Premium 2025 SP1.2): CAM, Flow Simulation, Plastics, Inspection, Visualize, Electrical,
  Composer, PDM, Manage and eDrawings are installed as products. The SOLIDWORKS folder also holds Toolbox, Utilities
  (`sldutils`), DFMXpress, DriveWorksXpress, FloXpress, ScanTo3D, FeatureWorks (`fworks`), Design Checker (`dsgnchk`),
  the Animator and the ray-trace renderer (`sldraytracerenderu.dll`).
- **Add-in ids.** Simulation is `{081FC34C-496D-42E2-8A3D-E885172C6BCB}` (`Simulation\cosworks.dll`); SOLIDWORKS CAM
  is `{CCAC6208-0E19-6455-2022-2025CEB541FA}` (`SOLIDWORKS CAM\cwaddinu.dll`). Start-up flags live in
  `HKCU\SOFTWARE\SolidWorks\AddInsStartup`; this server never writes them (it uses `LoadAddIn`/`UnloadAddIn`).
- **Toolbox: no insertion API.** `SolidWorks.Interop.swbrowser` exposes only `IApplication` and `IPDMDocManager`
  (PDM document events); `toolboxapi.chm` documents the configurator (`IToolBoxConfiguratorApplication`);
  `IAssemblyDoc.UpdateToolboxComponent(level)` refreshes existing Toolbox components from the Toolbox settings;
  `IModelDocExtension.ToolboxPartType` reads or sets whether a part is a Toolbox part. The master parts are plain
  files: 1,844 `.sldprt` under `C:\SOLIDWORKS Data\browser\<standard>\<category>\…` (ISO, DIN, ANSI, SKF, …).
- **Costing** (`sldcostingapi`). `IModelDocExtension.GetCostingManager` → `ICostManager` (`CostingModel`,
  `GetTemplatePathnames`) → `ICostPart` (`CreateCostAnalysis`, `GetCostAnalysis`, `SetCostingMethod`, the bodies) →
  `ICostAnalysis` (`GetTotalCostToManufacture`, `GetTotalCostToCharge`, `GetMaterialCost`, `GetManufacturingCost`,
  `GetSetupCost`, `LotSize`, `TotalQuantity`, `GetSpecificAnalysis`), with the specific analyses
  `ICostAnalysisMachining`, `…SheetMetal`, `…Plastic`, `…Casting`, `…3dPrinting` and `…Structural`.
- **Motion** (`swmotionstudy`). `IModelDocExtension.GetMotionStudyManager` → `IMotionStudyManager` → `IMotionStudy`,
  with results and plots (`IMotionStudyResults`, `IMotionPlotFeatureOutput`) and AVI output (`IAVIParameter`).
- **Utilities** (`gtswutilities`). `IUtilities` → `ICompareDocument`, `ICompareFeature`, `ICompareGeometry`
  (`CompareGeometry3`, `SaveCompareVolumeResults`), `IGeometryAnalysis`, `IThicknessAnalysis`, `IPowerSelect`,
  `IFeaturePaint`, `IFindReplaceAnnotations`.
- **Others.** FeatureWorks `IFeatureWorksApp`; Design Checker `ISWDesignCheck`; Inspection (`IInspectionAddinMgr`,
  `IInspectionProject`, characteristics and balloon settings); Visualize (`IVisualizeAddin`, render options); 3D printing
  `ISw3DPrinter`; Sustainability `IModelDocExtension.GetSustainability`. No API was found for DFMXpress or Smart
  Fasteners.
- **Simulation beyond static** (`cosworks`).
  - Study types (`swsAnalysisStudyType_e`): 0 static, 1 frequency, 2 buckling, 3 thermal, 4 optimization,
    5 nonlinear, 6 drop test, 7 fatigue, 8 dynamic, 9 pressure vessel, 13 topology. Which ones the licence allows is
    unverified: frequency, buckling and thermal may need Simulation Professional.
  - Fixtures (`swsRestraintType_e`): 0 fixed, 1 immovable, 2 symmetric, 3 roller, 4 hinge, 5 reference geometry,
    6 flat face, 7 cylindrical faces, 8 spherical surface, 9 cyclic symmetry.
  - `ICWLoadsAndRestraintsManager`: `AddForce3`, `AddPressure`, `AddGravity`, `AddCentrifugalForce`, `AddBearingLoad`,
    `AddRemoteLoad`, `AddTemperature`, `AddConvection`, `AddHeatFlux`, `AddHeatPower`, `AddRadiation`,
    `AddPrescribedDisplacement`, and the connectors `AddBoltConnector`, `AddPinConnector`, `AddSpringConnector`,
    `AddBearingConnector`, `AddRigidConnector`, `AddSpotWeldConnector`, `AddEdgeWeldConnector`.
  - `ICWContactManager`: `SetGlobalContact`, `CreateContactSet2`, `FindTouchingFacePairs`. `ICWMesh.ApplyMeshControl`.
  - `ICWResults`: `GetResonantFrequencies`, `GetBucklingLoadFactors`, `GetMinMaxFactorOfSafety2`, `GetMinMaxThermal`,
    `GetConnectorForces2`, `GetContactForcesAndFriction`, `CreatePlot`, `CreateStressHotSpotPlot`; `ICWPlot`:
    `SetFOSPlot`, `SetModeShape`, `GetResultsProbeManager`.
- **Core API for M8** (present in 2025): `IDimensionTolerance` (`Type`, `SetValues2`, `SetFitValues`),
  `IDrawingDoc.AddHoleCallout2`, `NewSheet4`, `CreateFlatPatternViewFromModelView3`, `InsertBreakHorizontal`,
  `CreateAuxiliaryViewAt2`, `CreateBreakOutSection`, `IView.InsertWeldmentTable`, `IModelDoc2.InsertWeldSymbol3`,
  `IBomTableAnnotation.SaveAsExcel`, `ITableAnnotation.SaveAsText2`, `IConfiguration.GetCutListItems`, `IBodyFolder`,
  `InsertWeldmentTrimFeature2`, `InsertEndCapFeature3`, `InsertGussetFeature3`, `InsertFilletBeadFeature3`,
  `GetSectionProperties2`, `GetPackAndGo` / `SavePackAndGo` (`IPackAndGo`), `InsertNewVirtualPart`, `EditPart2`,
  `CreateSaveBodyFeature`, `IPartDoc.InsertPart3`, `IPartDoc.MirrorPart2`, `InsertDwgOrDxfFile2`, `InsertSketchText`,
  `InsertSplitLineProject` / `InsertSplitLineSil`, `IThreadFeatureData`, `swFmFillPattern` (105), `swFmCurvePattern`
  (103), `InsertMoldPartingLine`, `InsertMoldShutOffSurface`, `InsertFillSurface2`, `ISldWorks.GetRayTraceRenderer`,
  `IModelViewManager.CreateSectionViewData`, `InsertFamilyTableNew`, `IAssemblyDoc.DissolveSubAssembly`,
  `IHoleSeriesFeatureData`, `IConfigurationManager.AddSpeedPak2`.
