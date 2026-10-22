using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Geometry;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Verify;

namespace SolidWorksMcp.Core.Services;

/// <summary>Where a reference feature lies, in model space (mm and unit vectors). Every reference tool returns one.</summary>
public sealed class RefFrame
{
    /// <summary>Plane: a point on it (its frame origin); axis: its start point; point: the point; coordsys: its origin.</summary>
    public required double[] OriginMm { get; init; }

    /// <summary>Plane: its normal (the side a positive offset from it goes to).</summary>
    public double[]? Normal { get; init; }

    /// <summary>Plane: signed distance from the model origin along <see cref="Normal"/>.</summary>
    public double? OffsetMm { get; init; }

    /// <summary>Axis: unit direction.</summary>
    public double[]? Direction { get; init; }

    /// <summary>Axis: its end point.</summary>
    public double[]? EndMm { get; init; }

    /// <summary>Plane: the in-plane x axis of its frame; coordinate system: its x axis.</summary>
    public double[]? XAxis { get; init; }

    /// <summary>Coordinate system: its y axis.</summary>
    public double[]? YAxis { get; init; }

    /// <summary>Coordinate system: its z axis.</summary>
    public double[]? ZAxis { get; init; }
}

/// <summary>Result of the reference-geometry tools: the new feature, where it lies, and the verification evidence.</summary>
public sealed class RefResult : ToolResult
{
    public required FeatureRef Feature { get; init; }

    /// <summary>Other features the call created (the 3D sketch that holds points given as coordinates).</summary>
    public IReadOnlyList<FeatureRef>? AlsoCreated { get; init; }

    public required RefFrame Frame { get; init; }
    public required Verified Verified { get; init; }
    public IReadOnlyList<DimensionInfo>? Dimensions { get; init; }
    public IReadOnlyList<ResolvedInfo>? Resolved { get; init; }
}

/// <summary>ref_plane options. Which fields apply depends on <see cref="Kind"/> (see the tool description).</summary>
public sealed record RefPlaneRequest
{
    public string? Doc { get; init; }

    /// <summary>offset | angle | three_points | parallel_point | mid_plane | normal_to_curve.</summary>
    public string Kind { get; init; } = "";

    public EntityRef? Base { get; init; }
    public double? OffsetMm { get; init; }
    public double? AngleDeg { get; init; }
    public EntityRef? Axis { get; init; }
    public double[][]? Points { get; init; }
    public EntityRef? Curve { get; init; }
    public double[]? Point { get; init; }
    public EntityRef? Second { get; init; }
    public bool Flip { get; init; }
    public string? Name { get; init; }
}

/// <summary>
/// Reference geometry (DESIGN §4.6): planes, axes, points and coordinate systems in parts. References are selected with
/// the marks each API expects; points given as coordinates become points of a helper 3D sketch, which stays in the tree
/// because the new feature references them. Every feature is checked against the requested geometry as SOLIDWORKS
/// reports it (IRefPlane.Transform and corner points, IRefAxis.GetRefAxisParams, IRefPoint.GetRefPoint, the
/// coordinate-system transform); a mismatch rolls the call back (feature and helper sketch).
/// </summary>
public sealed class ReferenceService(SwSession session)
{
    private const double LinTol = 1e-7;     // m: positions read back from SOLIDWORKS (0.1 µm)
    private const double AngTol = 1e-6;     // rad
    private const double OnCurveTol = 1e-5; // m: how far a given point may be from the curve it must lie on (0.01 mm)

    // swRefPlaneReferenceConstraints_e (docs/solidworks-api-notes.md §5).
    private const int Parallel = (int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_Parallel;
    private const int Perpendicular = (int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_Perpendicular;
    private const int Coincident = (int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_Coincident;
    private const int Distance = (int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_Distance;
    private const int Angle = (int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_Angle;
    private const int MidPlane = (int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_MidPlane;
    private const int FlipBit = (int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_OptionFlip;

    // IFeatureManager.InsertCoordinateSystem selection marks: origin 1, x axis 2, y axis 4 (z axis 8).
    private const int OriginMark = 1, XAxisMark = 2, YAxisMark = 4;

    private static readonly FeatureSpec PlaneSpec = new("ref_plane", ["RefPlane"], VolumeRule.Any, RequireFaces: false);
    private static readonly FeatureSpec RefAxisSpec = new("ref_axis", ["RefAxis"], VolumeRule.Any, RequireFaces: false);
    private static readonly FeatureSpec RefPointSpec = new("ref_point", ["RefPoint"], VolumeRule.Any, RequireFaces: false);
    private static readonly FeatureSpec CoordSysSpec = new("ref_coordsys", ["CoordSys", "CoordinateSystem"], VolumeRule.Any, RequireFaces: false);

    private static readonly string[] PlaneKinds = ["offset", "angle", "three_points", "parallel_point", "mid_plane", "normal_to_curve"];
    private static readonly string[] AxisKinds = ["two_planes", "two_points", "cylinder", "edge"];
    private static readonly string[] PointKinds = ["xyz", "arc_center", "face_center", "intersection"];

    // ------------------------------------------------------------------ ref_plane

    public Task<RefResult> PlaneAsync(RefPlaneRequest q, CancellationToken ct = default) =>
        session.RunAsync("ref_plane", ctx =>
        {
            string kind = Kind(q.Kind, PlaneKinds);
            var d = (IModelDoc2)ctx.Part();
            NoOpenSketch(d);
            WarnUnused(ctx, kind, PlaneArgs(kind),
                ("base", q.Base is not null), ("offsetMm", q.OffsetMm is not null), ("angleDeg", q.AngleDeg is not null),
                ("axis", q.Axis is not null), ("points", q.Points is not null), ("curve", q.Curve is not null),
                ("point", q.Point is not null), ("second", q.Second is not null), ("flip", q.Flip));
            var resolved = new List<ResolvedInfo>();
            var plan = PlanPlane(ctx, d, q, kind, resolved);
            var before = Snapshot.Take(d);
            bool flipped = false;
            while (true)
            {
                int c1 = flipped ? plan.C1 ^ FlipBit : plan.C1;
                var state = new PlaneCheckState();
                try
                {
                    var o = WithHint(PlaneHint(kind), () =>
                    {
                        object? api = Create(ctx, d, before, plan.Picks,
                            () => (object?)d.FeatureManager.InsertRefPlane(c1, plan.V1, plan.C2, plan.V2, plan.C3, plan.V3));
                        return FeatureVerifier.Check(ctx, d, before, PlaneSpec, (main, _, checks) =>
                        {
                            var problems = CheckPlane(ctx, main, plan, state, checks);
                            if (problems.Count == 0 && flipped)
                                checks.Add("SOLIDWORKS first put the plane on the other side; it was re-created with the flip option");
                            return problems;
                        }, false, api);
                    });
                    Rename(ctx, o.Main, q.Name);
                    return Result(ctx, d, o, PlaneFrame(state.Geom ?? ReadPlane(ctx, (IRefPlane)o.Main.Specific()!, "the new plane")), resolved);
                }
                catch (SwException) when (plan.RetryFlipped && state.WrongSide && !flipped && NothingNew(d, before))
                {
                    flipped = true; // the verifier rolled the plane back: try the other side once
                }
            }
        }, RunOptions.Write(q.Doc), ct);

    private static string[] PlaneArgs(string kind) => kind switch
    {
        "offset" => ["base", "offsetMm", "flip"],
        "angle" => ["base", "axis", "angleDeg", "flip"],
        "three_points" => ["points"],
        "parallel_point" => ["base", "point"],
        "mid_plane" => ["base", "second"],
        _ => ["curve", "base", "point"], // normal_to_curve: the curve comes as 'curve' or as 'base'
    };

    private static string PlaneHint(string kind) => kind switch
    {
        "offset" => "base: a plane ({name:'top'}) or a planar face ({id:'F3'}); offsetMm non-zero (negative, or flip, for the other side).",
        "angle" => "base: a plane or planar face; axis: a straight edge or axis lying in it (often an edge of the base face).",
        "three_points" => "points: three non-collinear model points [x,y,z] in mm.",
        "parallel_point" => "base: a plane or planar face; point: [x,y,z] model mm the plane must pass through.",
        "mid_plane" => "base and second: two parallel planes/faces (the plane midway) or two crossing ones (their bisector).",
        _ => "base: the curve (an edge, e.g. {type:'edge', at:[x,y,z]}); point: [x,y,z] model mm on it (default: its start).",
    };

    /// <summary>What to select (in mark order), the InsertRefPlane constraints, and how to check the result.</summary>
    private sealed class PlanePlan
    {
        public required List<Pick> Picks { get; init; }
        public required int C1 { get; init; }
        public double V1 { get; init; }
        public int C2 { get; init; }
        public double V2 { get; init; }
        public int C3 { get; init; }
        public double V3 { get; init; }

        /// <summary>Offset only: if SOLIDWORKS puts the plane on the other side, re-create it once with the flip option toggled.</summary>
        public bool RetryFlipped { get; init; }

        /// <summary>Checks the new plane: returns problems (empty = ok) and appends passed checks.</summary>
        public required Func<PlaneGeom, List<string>, PlaneCheckState, List<string>> Expect { get; init; }
    }

    private sealed class PlaneCheckState
    {
        public bool WrongSide { get; set; }

        /// <summary>The new plane as read for the check (reused for the result's frame).</summary>
        public PlaneGeom? Geom { get; set; }
    }

    private static PlanePlan PlanPlane(SwContext ctx, IModelDoc2 d, RefPlaneRequest q, string kind, List<ResolvedInfo> resolved)
    {
        Resolved Ref(EntityRef? r, string arg, string what, params EntityKind[] kinds)
        {
            if (r is null) throw SwException.InvalidArgument($"kind '{kind}' needs '{arg}' ({what}).", PlaneHint(kind));
            var x = EntityResolver.Resolve(ctx, d, r, arg, kinds);
            resolved.Add(x.Info(arg));
            return x;
        }

        switch (kind)
        {
            case "offset":
            {
                if (q.OffsetMm is not double off || !double.IsFinite(off) || off == 0)
                    throw SwException.InvalidArgument("kind 'offset' needs offsetMm (non-zero; negative = the other side of the base).", PlaneHint(kind));
                var b = Ref(q.Base, "base", "a plane or planar face", EntityKind.Plane, EntityKind.Face);
                var bp = PlaneOf(ctx, b, "base");
                double dist = Units.M(Math.Abs(off));
                double side = (off < 0) ^ q.Flip ? -1 : 1; // +1: along the base normal (faces: outward, away from the material)
                return new PlanePlan
                {
                    Picks = [new Pick { Entity = b, Mark = 0 }],
                    C1 = Distance | (side < 0 ? FlipBit : 0),
                    V1 = dist,
                    RetryFlipped = true,
                    Expect = (g, checks, st) =>
                    {
                        if (!g.Normal.IsParallelTo(bp.Normal, AngTol)) return [$"it is not parallel to {b.Label}"];
                        double s = bp.SignedDistance(g.Origin);
                        if (Math.Abs(Math.Abs(s) - dist) > LinTol)
                            return [$"it lies {Mm(Math.Abs(s))} mm from {b.Label}, not {Mm(dist)} mm"];
                        if (s * side < 0)
                        {
                            st.WrongSide = true;
                            return [$"it lies on the {(s > 0 ? "+" : "-")}normal side of {b.Label}, not the requested side"];
                        }
                        checks.Add($"parallel to {b.Label}, {Mm(s)} mm along its normal [{FmtDir(bp.Normal)}]");
                        return [];
                    },
                };
            }
            case "angle":
            {
                if (q.AngleDeg is not double deg || !double.IsFinite(deg) || deg <= 0 || deg >= 180)
                    throw SwException.InvalidArgument("kind 'angle' needs angleDeg between 0 and 180 (exclusive).", PlaneHint(kind));
                var b = Ref(q.Base, "base", "a plane or planar face", EntityKind.Plane, EntityKind.Face);
                var bp = PlaneOf(ctx, b, "base");
                var a = Ref(q.Axis, "axis", "a straight edge or an axis", EntityKind.Edge, EntityKind.Axis);
                var al = LineOf(a, "axis");
                double tilt = Math.Abs(al.Dir.Dot(bp.Normal));
                if (tilt > 1e-6)
                    throw SwException.InvalidArgument($"The axis ({a.Label}) must lie in or parallel to the base ({b.Label}); it makes {Units.Deg(Math.Asin(Math.Min(1, tilt)))}° with it.",
                        "Pick an edge or axis parallel to the base plane, e.g. an edge of the face used as base.");
                double rad = Units.Rad(deg);
                double acute = Math.Min(rad, Math.PI - rad);
                return new PlanePlan
                {
                    Picks = [new Pick { Entity = b, Mark = 0 }, new Pick { Entity = a, Mark = 1 }],
                    C1 = Angle | (q.Flip ? FlipBit : 0),
                    V1 = rad,
                    C2 = Coincident,
                    Expect = (g, checks, _) =>
                    {
                        var problems = new List<string>();
                        if (Math.Abs(g.Normal.Dot(al.Dir)) > AngTol || Math.Abs(g.SignedDistance(al.Point)) > LinTol)
                            problems.Add($"it does not contain {a.Label}");
                        double got = Math.Acos(Math.Min(1, Math.Abs(g.Normal.Dot(bp.Normal))));
                        if (Math.Abs(got - acute) > AngTol)
                            problems.Add($"it makes {Units.Deg(got)}° with {b.Label}, not {Units.Deg(acute)}°");
                        if (problems.Count == 0) checks.Add($"contains {a.Label}, {Units.Deg(acute)}° to {b.Label}");
                        return problems;
                    },
                };
            }
            case "three_points":
            {
                var pts = Points(q.Points, "points", 3, kind);
                var u = pts[1] - pts[0];
                var v = pts[2] - pts[0];
                if (u.Length < 1e-9 || v.Length < 1e-9 || u.Cross(v).Length < 1e-9 * u.Length * v.Length)
                    throw SwException.InvalidArgument("The three points are collinear (or two coincide), so they don't define a plane.", PlaneHint(kind));
                return new PlanePlan
                {
                    Picks = pts.Select((p, i) => new Pick { At = p, Mark = i }).ToList(),
                    C1 = Coincident,
                    C2 = Coincident,
                    C3 = Coincident,
                    Expect = (g, checks, _) => OnPlane(g, pts, checks, "passes through the 3 points"),
                };
            }
            case "parallel_point":
            {
                var p = PointArg(q.Point, "point", kind);
                var b = Ref(q.Base, "base", "a plane or planar face", EntityKind.Plane, EntityKind.Face);
                var bp = PlaneOf(ctx, b, "base");
                return new PlanePlan
                {
                    Picks = [new Pick { Entity = b, Mark = 0 }, new Pick { At = p, Mark = 1 }],
                    C1 = Parallel,
                    C2 = Coincident,
                    Expect = (g, checks, _) =>
                    {
                        if (!g.Normal.IsParallelTo(bp.Normal, AngTol)) return [$"it is not parallel to {b.Label}"];
                        return OnPlane(g, [p], checks, $"parallel to {b.Label} through [{Fmt(Units.Mm(p))}] mm");
                    },
                };
            }
            case "mid_plane":
            {
                var a = Ref(q.Base, "base", "a plane or planar face", EntityKind.Plane, EntityKind.Face);
                var ap = PlaneOf(ctx, a, "base");
                var b = Ref(q.Second, "second", "the other plane or planar face", EntityKind.Plane, EntityKind.Face);
                var bp = PlaneOf(ctx, b, "second");
                bool parallel = ap.Normal.IsParallelTo(bp.Normal, AngTol);
                if (parallel && Math.Abs(ap.SignedDistance(bp.Origin)) < LinTol)
                    throw SwException.InvalidArgument($"{a.Label} and {b.Label} are coincident, so there is no plane between them.", PlaneHint(kind));
                var axis = parallel ? null : Intersection(ap, bp);
                return new PlanePlan
                {
                    Picks = [new Pick { Entity = a, Mark = 0 }, new Pick { Entity = b, Mark = 1 }],
                    C1 = MidPlane,
                    C2 = MidPlane,
                    Expect = (g, checks, _) =>
                    {
                        if (axis is LineGeom l)
                        {
                            // Non-parallel references: a bisector through their intersection line.
                            double ta = Math.Acos(Math.Min(1, Math.Abs(g.Normal.Dot(ap.Normal))));
                            double tb = Math.Acos(Math.Min(1, Math.Abs(g.Normal.Dot(bp.Normal))));
                            if (Math.Abs(g.Normal.Dot(l.Dir)) > AngTol || Math.Abs(g.SignedDistance(l.Point)) > LinTol)
                                return [$"it does not contain the intersection of {a.Label} and {b.Label}"];
                            if (Math.Abs(ta - tb) > AngTol) return [$"it is not the bisector ({Units.Deg(ta)}° / {Units.Deg(tb)}° to the two)"];
                            checks.Add($"bisects {a.Label} and {b.Label} ({Units.Deg(ta)}° to each)");
                            return [];
                        }
                        if (!g.Normal.IsParallelTo(ap.Normal, AngTol)) return [$"it is not parallel to {a.Label}"];
                        double sa = g.SignedDistance(ap.Origin), sb = g.SignedDistance(bp.Origin);
                        if (Math.Abs(sa + sb) > 2 * LinTol || sa * sb >= 0)
                            return [$"it is not midway ({Mm(sa)} mm and {Mm(sb)} mm to the two)"];
                        checks.Add($"midway between {a.Label} and {b.Label} ({Mm(Math.Abs(sa))} mm to each)");
                        return [];
                    },
                };
            }
            default: // normal_to_curve
            {
                Vec3? given = q.Point is null ? null : PointArg(q.Point, "point", kind);
                if (q.Curve is not null && q.Base is not null) ctx.Warn("'base' is ignored because 'curve' is given.");
                var c = Ref(q.Curve ?? q.Base, q.Curve is null ? "base" : "curve", "the curve: an edge", EntityKind.Edge);
                var e = (IEdge)c.Obj;
                var geom = Topology.Describe(e);
                Vec3 p;
                Pick through;
                if (given is Vec3 want)
                {
                    var cp = e.ClosestPoint(want);
                    if (cp.Length < 3) throw new SwException(ErrorCodes.Internal, $"{c.Label} reports no closest point.");
                    p = Vec3.From(cp);
                    double off = p.DistanceTo(want);
                    if (off > OnCurveTol)
                        throw SwException.InvalidArgument($"'point' is {Mm(off)} mm from {c.Label}; it must lie on the curve (nearest point [{Fmt(Units.Mm(p))}] mm).",
                            PlaneHint(kind));
                    through = new Pick { At = p, Mark = 1 };
                }
                else if (e.StartVertex() is IVertex sv)
                {
                    p = Vec3.From(sv.Point());
                    through = new Pick { Entity = new Resolved { Obj = sv, Kind = EntityKind.Vertex, Label = $"the start vertex of {c.Label}" }, Mark = 1 };
                }
                else
                {
                    p = geom.Start; // closed curve: a point on it
                    through = new Pick { At = p, Mark = 1 };
                }
                var tangent = Tangent(geom, p);
                return new PlanePlan
                {
                    Picks = [new Pick { Entity = c, Mark = 0 }, through],
                    C1 = Perpendicular,
                    C2 = Coincident,
                    Expect = (g, checks, _) =>
                    {
                        var problems = OnPlane(g, [p], checks, $"passes through [{Fmt(Units.Mm(p))}] mm on {c.Label}");
                        if (tangent is Vec3 t)
                        {
                            if (!g.Normal.IsParallelTo(t, AngTol)) problems.Add($"its normal [{FmtDir(g.Normal)}] is not the curve's tangent [{FmtDir(t)}]");
                            else if (problems.Count == 0) checks.Add("normal to the curve there");
                        }
                        else if (problems.Count == 0) checks.Add("normal direction not checked (freeform curve)");
                        return problems;
                    },
                };
            }
        }
    }

    private static List<string> CheckPlane(SwContext ctx, IFeature main, PlanePlan plan, PlaneCheckState state, List<string> checks)
    {
        if (main.Specific() is not IRefPlane rp) return [$"'{main.Name}' is not a reference plane"];
        var g = ReadPlane(ctx, rp, $"'{main.Name}'");
        state.Geom = g;
        return plan.Expect(g, checks, state);
    }

    private static RefFrame PlaneFrame(PlaneGeom g) => new()
    {
        OriginMm = Clean(Units.Mm(g.Origin)),
        Normal = Clean(Units.Unit(g.Normal)),
        OffsetMm = Mm(g.Origin.Dot(g.Normal)),
        XAxis = g.XAxis is Vec3 x ? Clean(Units.Unit(x)) : null,
    };

    // ------------------------------------------------------------------ ref_axis

    public Task<RefResult> AxisAsync(string? doc, string kind, IReadOnlyList<EntityRef>? entities, double[][]? points, string? name,
        CancellationToken ct = default) =>
        session.RunAsync("ref_axis", ctx =>
        {
            string k = Kind(kind, AxisKinds);
            var d = (IModelDoc2)ctx.Part();
            NoOpenSketch(d);
            if (k != "two_points" && points is not null) ctx.Warn($"'points' is not used by kind '{k}' (ignored).");
            var resolved = new List<ResolvedInfo>();
            List<Pick> picks;
            LineGeom want;
            string what;
            switch (k)
            {
                case "two_planes":
                {
                    var rs = Entities(ctx, d, entities, 2, k, "2 planes or planar faces", resolved, EntityKind.Plane, EntityKind.Face);
                    var p1 = PlaneOf(ctx, rs[0], "entities[0]");
                    var p2 = PlaneOf(ctx, rs[1], "entities[1]");
                    want = Intersection(p1, p2) ?? throw SwException.InvalidArgument(
                        $"{rs[0].Label} and {rs[1].Label} are parallel, so they don't intersect in a line.", "Pick two planes that cross.");
                    what = $"the intersection of {rs[0].Label} and {rs[1].Label}";
                    picks = rs.Select(r => new Pick { Entity = r, Mark = 0 }).ToList();
                    break;
                }
                case "two_points":
                {
                    List<Vec3> pts;
                    if (points is not null)
                    {
                        if (entities is { Count: > 0 }) ctx.Warn("'entities' is ignored because 'points' is given.");
                        pts = Points(points, "points", 2, k);
                        picks = pts.Select(p => new Pick { At = p, Mark = 0 }).ToList();
                    }
                    else
                    {
                        var rs = Entities(ctx, d, entities, 2, k, "2 vertices or points ({type:'vertex', at:[x,y,z]}), or pass 'points'", resolved,
                            EntityKind.Vertex, EntityKind.Point, EntityKind.SketchPoint, EntityKind.Origin);
                        pts = rs.Select((r, i) => PointOf(r, $"entities[{i}]")).ToList();
                        picks = rs.Select(r => new Pick { Entity = r, Mark = 0 }).ToList();
                    }
                    if (pts[0].DistanceTo(pts[1]) < 1e-9) throw SwException.InvalidArgument("The two points coincide, so they don't define an axis.");
                    want = new LineGeom(pts[0], (pts[1] - pts[0]).Normalized());
                    what = $"the line through [{Fmt(Units.Mm(pts[0]))}] and [{Fmt(Units.Mm(pts[1]))}] mm";
                    break;
                }
                case "cylinder":
                {
                    var rs = Entities(ctx, d, entities, 1, k, "a cylindrical or conical face", resolved, EntityKind.Face);
                    var fg = Topology.Describe((IFace2)rs[0].Obj);
                    if (fg.Surface is not ("cylinder" or "cone") || fg.AxisOrigin is not Vec3 o || fg.Axis is not Vec3 ax)
                        throw SwException.InvalidArgument($"{rs[0].Label} is a {fg.Surface} face; kind 'cylinder' needs a cylindrical or conical face.");
                    want = new LineGeom(o, ax.Normalized());
                    what = $"the axis of {rs[0].Label}";
                    picks = [new Pick { Entity = rs[0], Mark = 0 }];
                    break;
                }
                default: // edge
                {
                    var rs = Entities(ctx, d, entities, 1, k, "a straight edge", resolved, EntityKind.Edge);
                    want = LineOf(rs[0], "entities[0]");
                    what = rs[0].Label;
                    picks = [new Pick { Entity = rs[0], Mark = 0 }];
                    break;
                }
            }
            var before = Snapshot.Take(d);
            RefFrame? frame = null;
            var o2 = WithHint(AxisHint(k), () =>
            {
                object? api = Create(ctx, d, before, picks, () => d.InsertAxis2(true) ? true : null);
                return FeatureVerifier.Check(ctx, d, before, RefAxisSpec, (main, _, checks) =>
                {
                    var (problems, f) = CheckAxis(main, want, what, checks);
                    frame = f;
                    return problems;
                }, false, api);
            });
            Rename(ctx, o2.Main, name);
            return Result(ctx, d, o2, frame!, resolved);
        }, RunOptions.Write(doc), ct);

    private static string AxisHint(string kind) => kind switch
    {
        "two_planes" => "entities: two crossing planes or planar faces, e.g. [{name:'front'},{name:'right'}].",
        "two_points" => "points: [[x,y,z],[x,y,z]] model mm, or entities: two vertices/points.",
        "cylinder" => "entities: one cylindrical or conical face, e.g. [{type:'face', at:[x,y,z]}] on the hole wall.",
        _ => "entities: one straight edge, e.g. [{type:'edge', at:[x,y,z]}].",
    };

    /// <summary>
    /// Checks the new axis against the wanted line. GetRefAxisParams is read as [start, end]; if that reading doesn't
    /// match, the [point, direction] reading is tried (the layout is not yet verified live) and the result says so.
    /// </summary>
    private static (List<string> Problems, RefFrame? Frame) CheckAxis(IFeature main, LineGeom want, string what, List<string> checks)
    {
        if (main.Specific() is not IRefAxis ax) return ([$"'{main.Name}' is not a reference axis"], null);
        var p = ax.RefAxisEnds();
        if (p.Length < 6) return ([$"'{main.Name}' reports no axis parameters"], null);
        var a = Vec3.From(p, 0);
        var b = Vec3.From(p, 3);
        List<string>? first = null;
        foreach (bool pointDir in new[] { false, true })
        {
            var dir = pointDir ? b : b - a;
            if (dir.Length < 1e-12) continue;
            var got = new LineGeom(a, dir.Normalized());
            var problems = new List<string>();
            if (!got.Dir.IsParallelTo(want.Dir, AngTol)) problems.Add($"its direction [{FmtDir(got.Dir)}] is not along [{FmtDir(want.Dir)}]");
            double off = got.DistanceTo(want.Point);
            if (off > LinTol) problems.Add($"it passes {Mm(off)} mm from {what}");
            if (problems.Count == 0)
            {
                checks.Add($"along {what}");
                if (pointDir) checks.Add("axis parameters read as [point, direction]");
                return ([], new RefFrame
                {
                    OriginMm = Clean(Units.Mm(a)),
                    EndMm = pointDir ? null : Clean(Units.Mm(b)),
                    Direction = Clean(Units.Unit(got.Dir)),
                });
            }
            first ??= problems;
        }
        return (first ?? [$"'{main.Name}' reports a degenerate axis"], null);
    }

    // ------------------------------------------------------------------ ref_point

    public Task<RefResult> PointAsync(string? doc, string kind, double[]? at, IReadOnlyList<EntityRef>? entities, string? name,
        CancellationToken ct = default) =>
        session.RunAsync("ref_point", ctx =>
        {
            string k = Kind(kind, PointKinds);
            Vec3? xyz = k == "xyz" ? PointArg(at, "at", k) : null;
            var d = (IModelDoc2)ctx.Part();
            NoOpenSketch(d);
            if (k == "xyz" && entities is { Count: > 0 }) ctx.Warn("'entities' is not used by kind 'xyz' (ignored).");
            if (k != "xyz" && at is not null) ctx.Warn($"'at' is not used by kind '{k}' (ignored).");
            var resolved = new List<ResolvedInfo>();
            List<Pick> picks;
            int type;
            Func<Vec3, List<string>, List<string>> expect;
            switch (k)
            {
                case "xyz":
                {
                    var p = xyz!.Value;
                    picks = [new Pick { At = p, Mark = 0 }];
                    type = (int)swRefPointType_e.swRefPointSketchPoint;
                    expect = (got, checks) => Near(got, p, LinTol, $"[{Fmt(Units.Mm(p))}] mm", checks);
                    break;
                }
                case "arc_center":
                {
                    var rs = Entities(ctx, d, entities, 1, k, "a circular edge", resolved, EntityKind.Edge);
                    var g = Topology.Describe((IEdge)rs[0].Obj);
                    if (g.Curve != "circle" || g.Center is not Vec3 center)
                        throw SwException.InvalidArgument($"{rs[0].Label} is a {g.Curve} edge; kind 'arc_center' needs an arc or circle.");
                    picks = [new Pick { Entity = rs[0], Mark = 0 }];
                    type = (int)swRefPointType_e.swRefPointCenterEdge;
                    expect = (got, checks) => Near(got, center, LinTol, $"the center of {rs[0].Label}", checks);
                    break;
                }
                case "face_center":
                {
                    var rs = Entities(ctx, d, entities, 1, k, "a face", resolved, EntityKind.Face);
                    var f = (IFace2)rs[0].Obj;
                    var box = f.Box();
                    PlaneGeom? plane = Topology.SurfaceKind(f.Surface()) == "plane" ? PlaneOf(ctx, rs[0], "entities[0]") : null;
                    picks = [new Pick { Entity = rs[0], Mark = 0 }];
                    type = (int)swRefPointType_e.swRefPointFaceCenter;
                    // The face's center: it lies inside the face's box, and on the face's plane when planar.
                    expect = (got, checks) =>
                    {
                        var problems = new List<string>();
                        if (box.Length == 6 && !InBox(got, box, 1e-6)) problems.Add($"[{Fmt(Units.Mm(got))}] mm lies outside the box of {rs[0].Label}");
                        if (plane is PlaneGeom pg && Math.Abs(pg.SignedDistance(got)) > LinTol)
                            problems.Add($"it lies {Mm(Math.Abs(pg.SignedDistance(got)))} mm off the plane of {rs[0].Label}");
                        if (problems.Count == 0) checks.Add($"inside {rs[0].Label}'s extents{(plane is null ? "" : ", on its plane")}");
                        return problems;
                    };
                    break;
                }
                default: // intersection
                {
                    var rs = Entities(ctx, d, entities, 2, k, "2 edges or axes", resolved, EntityKind.Edge, EntityKind.Axis);
                    if (StraightLine(rs[0]) is LineGeom l0 && StraightLine(rs[1]) is LineGeom l1)
                    {
                        var n = l0.Dir.Cross(l1.Dir);
                        if (n.Length < 1e-9)
                            throw SwException.InvalidArgument($"{rs[0].Label} and {rs[1].Label} are parallel, so they don't intersect.", PointHint(k));
                        double gap = Math.Abs((l1.Point - l0.Point).Dot(n.Normalized()));
                        if (gap > LinTol)
                            throw SwException.InvalidArgument($"{rs[0].Label} and {rs[1].Label} don't intersect (they pass {Mm(gap)} mm apart).", PointHint(k));
                    }
                    picks = rs.Select(r => new Pick { Entity = r, Mark = 0 }).ToList();
                    type = (int)swRefPointType_e.swRefPointIntersection;
                    expect = (got, checks) =>
                    {
                        var problems = new List<string>();
                        foreach (var r in rs)
                        {
                            double dist = DistanceTo(r, got);
                            if (dist > 1e-6) problems.Add($"it lies {Mm(dist)} mm from {r.Label}");
                        }
                        if (problems.Count == 0) checks.Add($"on both {rs[0].Label} and {rs[1].Label}");
                        return problems;
                    };
                    break;
                }
            }
            var before = Snapshot.Take(d);
            Vec3 pos = default;
            var o = WithHint(PointHint(k), () =>
            {
                object? api = Create(ctx, d, before, picks,
                    () => (object?)d.FeatureManager.InsertReferencePoint(type, (int)swRefPointAlongCurveType_e.swRefPointAlongCurveDistance, 0, 1));
                return FeatureVerifier.Check(ctx, d, before, RefPointSpec, (main, _, checks) =>
                {
                    if (main.Specific() is not IRefPoint rp || rp.RefPointXyz() is not { Length: >= 3 } loc) return [$"'{main.Name}' reports no position"];
                    pos = Vec3.From(loc);
                    return expect(pos, checks);
                }, false, api);
            });
            Rename(ctx, o.Main, name);
            return Result(ctx, d, o, new RefFrame { OriginMm = Clean(Units.Mm(pos)) }, resolved);
        }, RunOptions.Write(doc), ct);

    private static string PointHint(string kind) => kind switch
    {
        "xyz" => "at: [x,y,z] model mm.",
        "arc_center" => "entities: one circular edge, e.g. [{type:'edge', at:[x,y,z]}] on the circle.",
        "face_center" => "entities: one face, e.g. [{id:'F3'}].",
        _ => "entities: two edges or axes that cross.",
    };

    // ------------------------------------------------------------------ ref_coordsys

    /// <summary>A coordinate system at <paramref name="origin"/> (mm) or at <paramref name="originRef"/>, with optional axis directions.</summary>
    public Task<RefResult> CoordSysAsync(string? doc, double[]? origin, EntityRef? originRef, EntityRef? xAxis, EntityRef? yAxis, string? name,
        CancellationToken ct = default) =>
        session.RunAsync("ref_coordsys", ctx =>
        {
            if ((origin is null) == (originRef is null))
                throw SwException.InvalidArgument("Pass the origin either as [x, y, z] (mm) or as a vertex / point reference, not both or neither.",
                    "E.g. origin:[10,0,0] or origin:{id:'V3'}.");
            Vec3? at = origin is null ? null : Units.PointM(origin, "origin");
            var d = (IModelDoc2)ctx.Part();
            NoOpenSketch(d);
            var resolved = new List<ResolvedInfo>();
            var picks = new List<Pick>();
            Vec3 o;
            if (at is Vec3 a)
            {
                o = a;
                picks.Add(new Pick { At = a, Mark = OriginMark });
            }
            else
            {
                var r = EntityResolver.Resolve(ctx, d, originRef, "origin", EntityKind.Vertex, EntityKind.Point, EntityKind.SketchPoint, EntityKind.Origin);
                resolved.Add(r.Info("origin"));
                o = PointOf(r, "origin");
                picks.Add(new Pick { Entity = r, Mark = OriginMark });
            }
            LineGeom? lx = null, ly = null;
            if (xAxis is not null)
            {
                var r = EntityResolver.Resolve(ctx, d, xAxis, "xAxis", EntityKind.Edge, EntityKind.Axis);
                resolved.Add(r.Info("xAxis"));
                lx = LineOf(r, "xAxis");
                picks.Add(new Pick { Entity = r, Mark = XAxisMark });
            }
            if (yAxis is not null)
            {
                var r = EntityResolver.Resolve(ctx, d, yAxis, "yAxis", EntityKind.Edge, EntityKind.Axis);
                resolved.Add(r.Info("yAxis"));
                ly = LineOf(r, "yAxis");
                picks.Add(new Pick { Entity = r, Mark = YAxisMark });
            }
            if (lx is LineGeom x0 && ly is LineGeom y0 && Math.Abs(x0.Dir.Dot(y0.Dir)) > 1e-6)
                throw SwException.InvalidArgument($"xAxis and yAxis must be perpendicular; they make {Units.Deg(Math.Acos(Math.Min(1, Math.Abs(x0.Dir.Dot(y0.Dir)))))}°.");

            var before = Snapshot.Take(d);
            RefFrame? frame = null;
            var outcome = WithHint("origin: [x,y,z] mm or a vertex/point; xAxis/yAxis: straight edges or axes (perpendicular to each other).", () =>
            {
                object? api = Create(ctx, d, before, picks, () => (object?)d.FeatureManager.InsertCoordinateSystem(false, false, false));
                return FeatureVerifier.Check(ctx, d, before, CoordSysSpec, (main, _, checks) =>
                {
                    var (problems, f) = CheckCoordSys(d, main, o, lx, ly, checks);
                    frame = f;
                    return problems;
                }, false, api);
            });
            Rename(ctx, outcome.Main, name);
            return Result(ctx, d, outcome, frame!, resolved);
        }, RunOptions.Write(doc), ct);

    /// <summary>
    /// Reads the new coordinate system's transform and checks origin and axes. The transform is expected to map
    /// coordinate-system coordinates to model coordinates; the inverse reading is accepted too (and reported), since
    /// that convention is not yet verified live.
    /// </summary>
    private static (List<string> Problems, RefFrame? Frame) CheckCoordSys(IModelDoc2 d, IFeature main, Vec3 origin, LineGeom? lx, LineGeom? ly,
        List<string> checks)
    {
        var t = d.RefCoordSysXform(main.Name);
        if (t.Length < 13 && (object?)main.GetDefinition() is ICoordinateSystemFeatureData def)
            t = ComArgs.Doubles((object?)def.Transform?.ArrayData);
        if (t.Length < 13) return ([$"'{main.Name}' reports no transform"], null);
        var direct = new Xform(t);
        List<string>? first = null;
        foreach (var (xf, how) in new[] { (direct, (string?)null), (direct.Inverse(), "transform read as model-to-local") })
        {
            var o = xf.ApplyPoint(Vec3.Zero);
            var ex = xf.ApplyVector(Vec3.UnitX).Normalized();
            var ey = xf.ApplyVector(Vec3.UnitY).Normalized();
            var ez = xf.ApplyVector(Vec3.UnitZ).Normalized();
            var problems = new List<string>();
            if (o.DistanceTo(origin) > LinTol) problems.Add($"its origin is [{Fmt(Units.Mm(o))}] mm, not [{Fmt(Units.Mm(origin))}]");
            if (Math.Abs(ex.Dot(ey)) > AngTol || ex.Cross(ey).Dot(ez) < 1 - 1e-6) problems.Add("its axes are not a right-handed orthonormal frame");
            if (lx is LineGeom x && !ex.IsParallelTo(x.Dir, AngTol)) problems.Add($"its x axis [{FmtDir(ex)}] is not along [{FmtDir(x.Dir)}]");
            if (ly is LineGeom y && !ey.IsParallelTo(y.Dir, AngTol)) problems.Add($"its y axis [{FmtDir(ey)}] is not along [{FmtDir(y.Dir)}]");
            if (lx is null && ly is null && (ex.Dot(Vec3.UnitX) < 1 - 1e-9 || ey.Dot(Vec3.UnitY) < 1 - 1e-9))
                problems.Add($"its axes [{FmtDir(ex)}], [{FmtDir(ey)}] are not the model's x and y");
            if (problems.Count == 0)
            {
                checks.Add($"origin at [{Fmt(Units.Mm(o))}] mm" + (lx is null && ly is null ? ", axes along the model's" : ", axes along the given references"));
                if (how is not null) checks.Add(how);
                return ([], new RefFrame
                {
                    OriginMm = Clean(Units.Mm(o)),
                    XAxis = Clean(Units.Unit(ex)),
                    YAxis = Clean(Units.Unit(ey)),
                    ZAxis = Clean(Units.Unit(ez)),
                });
            }
            first ??= problems;
        }
        return (first!, null);
    }

    // ------------------------------------------------------------------ geometry of references

    /// <summary>A plane in model space (m): a point on it, its unit normal and (reference planes) its frame's x axis.</summary>
    private readonly record struct PlaneGeom(Vec3 Origin, Vec3 Normal, Vec3? XAxis = null)
    {
        public double SignedDistance(Vec3 p) => (p - Origin).Dot(Normal);
    }

    /// <summary>A line in model space (m): a point on it and its unit direction.</summary>
    private readonly record struct LineGeom(Vec3 Point, Vec3 Dir)
    {
        public double DistanceTo(Vec3 p)
        {
            var v = p - Point;
            return (v - Dir * v.Dot(Dir)).Length;
        }
    }

    /// <summary>The plane of a reference plane or planar face (faces: the outward normal).</summary>
    private static PlaneGeom PlaneOf(SwContext ctx, Resolved r, string arg)
    {
        if (r.Obj is IFace2 f)
        {
            if (Topology.SurfaceKind(f.Surface()) != "plane")
                throw SwException.InvalidArgument($"'{arg}' resolved to {r.Label}, which is not planar.", "Pass a plane ({name:'top'}) or a planar face.");
            var n = f.NormalVec();
            if (n.Length < 3) throw new SwException(ErrorCodes.Internal, $"{r.Label} reports no normal.");
            return new PlaneGeom(Topology.InteriorPoint(f), new Vec3(n[0], n[1], n[2]).Normalized());
        }
        if (r.Obj is IFeature feat && feat.Specific() is IRefPlane rp) return ReadPlane(ctx, rp, r.Label);
        throw SwException.InvalidArgument($"'{arg}' resolved to {r.Label}; it needs a plane or a planar face.");
    }

    /// <summary>
    /// A reference plane in model space. Its transform gives the frame: row 3 is the normal and the translation a
    /// point on the plane (the convention EntityResolver.StandardPlane uses). The displayed corner points, which don't
    /// depend on that convention, cross-check it; if they disagree, the corners' plane is used (with the transform's
    /// sense of the normal) and a warning says so.
    /// </summary>
    private static PlaneGeom ReadPlane(SwContext ctx, IRefPlane rp, string label)
    {
        PlaneGeom? fromXform = null;
        var t = rp.RefPlaneXform();
        if (t.Length >= 12)
        {
            var n = new Vec3(t[6], t[7], t[8]);
            var x = new Vec3(t[0], t[1], t[2]);
            if (n.Length > 1e-12) fromXform = new PlaneGeom(new Vec3(t[9], t[10], t[11]), n.Normalized(), x.Length < 1e-12 ? null : x.Normalized());
        }
        var fromCorners = CornerPlane(rp);
        if (fromXform is PlaneGeom g)
        {
            if (fromCorners is not PlaneGeom c) return g;
            if (g.Normal.IsParallelTo(c.Normal, AngTol) && Math.Abs(g.SignedDistance(c.Origin)) <= LinTol) return g;
            double sense = c.Normal.Dot(g.Normal);
            ctx.Warn($"{label}: its transform and its corner points describe different planes; the corner points were used " +
                     $"(normal [{FmtDir(c.Normal)}], {Mm(Math.Abs(g.SignedDistance(c.Origin)))} mm from the transform's origin).");
            return new PlaneGeom(c.Origin, sense < -1e-6 ? -c.Normal : c.Normal);
        }
        if (fromCorners is PlaneGeom only)
        {
            ctx.Warn($"{label}: no transform; its plane was taken from its corner points.");
            return only;
        }
        throw new SwException(ErrorCodes.VerifyFailed, $"{label} reports neither a transform nor corner points.");
    }

    /// <summary>The plane through the displayed corner points (null when they can't be read or don't lie in one plane).</summary>
    private static PlaneGeom? CornerPlane(IRefPlane rp)
    {
        List<Vec3> c;
        try { c = rp.RefPlaneCorners(); }
        catch (Exception e) when (!ComArgs.IsDisconnect(e)) { return null; }
        if (c.Count < 3) return null;
        var center = c.Aggregate(Vec3.Zero, (s, p) => s + p) / c.Count;
        var best = Vec3.Zero;
        for (int i = 1; i < c.Count; i++)
            for (int j = i + 1; j < c.Count; j++)
            {
                var n = (c[i] - c[0]).Cross(c[j] - c[0]);
                if (n.Length > best.Length) best = n;
            }
        if (best.Length < 1e-15) return null;
        var normal = best.Normalized();
        return c.All(p => Math.Abs((p - center).Dot(normal)) <= LinTol) ? new PlaneGeom(center, normal) : null;
    }

    /// <summary>The line of a straight edge or a reference axis.</summary>
    private static LineGeom LineOf(Resolved r, string arg)
    {
        if (r.Obj is IEdge e)
        {
            var g = Topology.Describe(e);
            if (g.Curve != "line" || g.Direction is not Vec3 dir || dir.Length < 1e-12)
                throw SwException.InvalidArgument($"'{arg}' resolved to {r.Label}, a {g.Curve} edge; it needs a straight edge or an axis.");
            return new LineGeom(g.Start, dir.Normalized());
        }
        if (r.Obj is IFeature f && f.Specific() is IRefAxis ax)
            return AxisLine(ax) ?? throw new SwException(ErrorCodes.Internal, $"{r.Label} reports no axis line.");
        throw SwException.InvalidArgument($"'{arg}' resolved to {r.Label}; it needs a straight edge or an axis.");
    }

    /// <summary>The line of a straight edge or an axis, or null for curved edges.</summary>
    private static LineGeom? StraightLine(Resolved r) => r.Obj switch
    {
        IEdge e when Topology.Describe(e) is { Curve: "line", Direction: Vec3 dir } g && dir.Length > 1e-12 => new LineGeom(g.Start, dir.Normalized()),
        IFeature f when f.Specific() is IRefAxis ax => AxisLine(ax),
        _ => null,
    };

    /// <summary>An existing axis, from GetRefAxisParams read as [start, end].</summary>
    private static LineGeom? AxisLine(IRefAxis ax)
    {
        var p = ax.RefAxisEnds();
        if (p.Length < 6) return null;
        var a = Vec3.From(p, 0);
        var dir = Vec3.From(p, 3) - a;
        return dir.Length < 1e-12 ? null : new LineGeom(a, dir.Normalized());
    }

    /// <summary>The position of a vertex, reference point, sketch point or the origin.</summary>
    private static Vec3 PointOf(Resolved r, string arg)
    {
        switch (r.Obj)
        {
            case IVertex v:
                return Vec3.From(v.Point());
            case ISketchPoint sp:
                return SketchPointModel(sp);
            case IFeature when r.Kind == EntityKind.Origin:
                return Vec3.Zero;
            case IFeature f when f.Specific() is IRefPoint rp:
                var loc = rp.RefPointXyz();
                if (loc.Length >= 3) return Vec3.From(loc);
                throw new SwException(ErrorCodes.Internal, $"{r.Label} reports no position.");
        }
        throw SwException.InvalidArgument($"'{arg}' resolved to {r.Label}; it needs a vertex, a reference point, a sketch point or the origin.");
    }

    /// <summary>A sketch point in model coordinates (2D sketch points are stored in sketch coordinates).</summary>
    private static Vec3 SketchPointModel(ISketchPoint sp)
    {
        var p = new Vec3(sp.X, sp.Y, sp.Z);
        try
        {
            if (sp.GetSketch() is ISketch sk && !sk.Is3D()) return SketchService.SketchToModel(sk).ApplyPoint(p);
        }
        catch (COMException e) when (!ComArgs.IsDisconnect(e)) { /* e.g. the origin's point: already in model coordinates */ }
        return p;
    }

    /// <summary>How far a point is from an edge or axis (straight edges and axes: the infinite line; curves: the edge).</summary>
    private static double DistanceTo(Resolved r, Vec3 p) =>
        StraightLine(r) is LineGeom l ? l.DistanceTo(p) : r.Obj is IEdge e ? Topology.DistanceToEdge(e, p) : double.MaxValue;

    /// <summary>The intersection line of two planes, or null when they are parallel.</summary>
    private static LineGeom? Intersection(PlaneGeom a, PlaneGeom b)
    {
        var dir = a.Normal.Cross(b.Normal);
        double l2 = dir.Dot(dir);
        if (l2 < 1e-18) return null;
        double ha = a.Normal.Dot(a.Origin), hb = b.Normal.Dot(b.Origin);
        // The point of the line nearest the origin: it satisfies na·p = ha and nb·p = hb.
        var p = (b.Normal.Cross(dir) * ha + dir.Cross(a.Normal) * hb) / l2;
        return new LineGeom(p, dir.Normalized());
    }

    /// <summary>The unit tangent of a line or circle edge at a point on it (null for freeform curves).</summary>
    private static Vec3? Tangent(EdgeGeom g, Vec3 p)
    {
        if (g.Curve == "line") return g.Direction;
        if (g.Curve == "circle" && g.Center is Vec3 c && g.Axis is Vec3 ax)
        {
            var t = ax.Cross(p - c);
            return t.Length > 1e-12 ? t.Normalized() : null;
        }
        return null;
    }

    private static List<string> OnPlane(PlaneGeom g, IEnumerable<Vec3> pts, List<string> checks, string ok)
    {
        var problems = pts.Where(p => Math.Abs(g.SignedDistance(p)) > LinTol)
            .Select(p => $"[{Fmt(Units.Mm(p))}] mm lies {Mm(Math.Abs(g.SignedDistance(p)))} mm off the plane").ToList();
        if (problems.Count == 0) checks.Add(ok);
        return problems;
    }

    private static List<string> Near(Vec3 got, Vec3 want, double tol, string what, List<string> checks)
    {
        double dist = got.DistanceTo(want);
        if (dist > tol) return [$"it is at [{Fmt(Units.Mm(got))}] mm, {Mm(dist)} mm from {what}"];
        checks.Add($"at {what}");
        return [];
    }

    private static bool InBox(Vec3 p, double[] b, double pad) =>
        p.X >= b[0] - pad && p.X <= b[3] + pad && p.Y >= b[1] - pad && p.Y <= b[4] + pad && p.Z >= b[2] - pad && p.Z <= b[5] + pad;

    // ------------------------------------------------------------------ creation: helper points, selection, rollback

    /// <summary>One reference to select for the API call: a resolved entity, or a coordinate that becomes a 3D-sketch point.</summary>
    private sealed class Pick
    {
        public Resolved? Entity { get; init; }
        public Vec3? At { get; init; }
        public int Mark { get; init; }

        /// <summary>The helper sketch point for <see cref="At"/>, once created.</summary>
        public ISketchPoint? SketchPoint { get; set; }

        public string Label => Entity?.Label ?? $"the sketch point at [{Fmt(Units.Mm(At!.Value))}] mm";
    }

    /// <summary>
    /// Creates the helper sketch points, selects the references in mark order and calls the API. Any failure rolls
    /// back everything created since <paramref name="before"/> (the helper sketch included).
    /// </summary>
    private static object? Create(SwContext ctx, IModelDoc2 d, Snapshot before, List<Pick> picks, Func<object?> api)
    {
        try
        {
            MakeSketchPoints(d, picks);
            SelectPicks(d, picks);
            return api();
        }
        catch (Exception ex) when (!ComArgs.IsDisconnect(ex))
        {
            string rb = SafeRollback(ctx, d, before);
            if (ex is SwException sw) throw new SwException(sw.Code, $"{sw.Message} {rb}", sw.Hint, sw.InnerException);
            throw;
        }
        finally
        {
            try { d.ClearSelection2(true); } catch (COMException e) when (!ComArgs.IsDisconnect(e)) { /* best effort */ }
        }
    }

    private static string SafeRollback(SwContext ctx, IModelDoc2 d, Snapshot before)
    {
        try
        {
            CloseOpenSketch(d);
            return FeatureVerifier.Rollback(ctx, d, before);
        }
        catch (Exception e) when (!ComArgs.IsDisconnect(e))
        {
            return $"Rollback failed ({e.Message}); check feature_list.";
        }
    }

    /// <summary>
    /// The verified recipe for points at coordinates: a new 3D sketch, CreatePoint at model coordinates (m), close.
    /// Inference is off while drawing (AddToDB), so the points never snap to nearby geometry.
    /// </summary>
    private static void MakeSketchPoints(IModelDoc2 d, List<Pick> picks)
    {
        var want = picks.Where(p => p.At is not null).ToList();
        if (want.Count == 0) return;
        var sm = d.SketchManager;
        d.ClearSelection2(true);
        sm.Insert3DSketch(true);
        if (sm.ActiveSketch is not ISketch sketch)
            throw new SwException(ErrorCodes.FeatureFailed, "SOLIDWORKS did not open a 3D sketch for the points.");
        string name = ((IFeature)sketch).Name;
        bool addToDb = sm.AddToDB;
        try
        {
            sm.AddToDB = true;
            foreach (var p in want)
            {
                var at = p.At!.Value;
                var sp = sm.CreatePoint(at.X, at.Y, at.Z)
                         ?? throw new SwException(ErrorCodes.FeatureFailed, $"Could not create {p.Label} in {name}.");
                var got = new Vec3(sp.X, sp.Y, sp.Z);
                if (got.DistanceTo(at) > 1e-9)
                    throw new SwException(ErrorCodes.VerifyFailed, $"{p.Label} landed at [{Fmt(Units.Mm(got))}] mm in {name}.");
                p.SketchPoint = sp;
            }
        }
        finally
        {
            sm.AddToDB = addToDb;
            CloseOpenSketch(d);
        }
        if (sm.ActiveSketch is not null) throw new SwException(ErrorCodes.SketchState, $"The helper sketch '{name}' did not close.", "Check dialog_list.");
    }

    private static void CloseOpenSketch(IModelDoc2 d)
    {
        var sm = d.SketchManager;
        if (sm.ActiveSketch is not ISketch open) return;
        if (open.Is3D()) sm.Insert3DSketch(true);
        else sm.InsertSketch(true);
    }

    /// <summary>
    /// Selects every pick with its mark. Sketch points are selected the verified way (SelectByID2 EXTSKETCHPOINT at
    /// their coordinates); if that picks something else, the sketch points are selected directly instead.
    /// </summary>
    private static void SelectPicks(IModelDoc2 d, IReadOnlyList<Pick> picks)
    {
        string? failed = TrySelect(d, picks, byCoordinates: true);
        if (failed is not null && picks.Any(p => p.At is not null)) failed = TrySelect(d, picks, byCoordinates: false);
        if (failed is not null)
            throw new SwException(ErrorCodes.FeatureFailed, $"Could not select {failed}.", "Check the references (feature_list, face_list, edge_list) and retry.");
    }

    /// <returns>Null when every pick is selected with its mark; else the label of the first that failed.</returns>
    private static string? TrySelect(IModelDoc2 d, IReadOnlyList<Pick> picks, bool byCoordinates)
    {
        d.ClearSelection2(true);
        var sel = d.Sel();
        for (int i = 0; i < picks.Count; i++)
        {
            var p = picks[i];
            bool ok;
            if (p.Entity is not null)
            {
                ok = p.Entity.Select(d, true, p.Mark);
            }
            else if (byCoordinates)
            {
                var at = p.At!.Value;
                ok = d.Extension.SelectByID2("", "EXTSKETCHPOINT", at.X, at.Y, at.Z, true, p.Mark, null, 0);
            }
            else
            {
                var data = sel.CreateSelectData();
                data.Mark = p.Mark;
                ok = p.SketchPoint is not null && p.SketchPoint.Select4(true, data);
            }
            if (ok) ok = sel.GetSelectedObjectCount2(-1) == i + 1 && (p.At is not Vec3 want || SelectedPointIs(sel, i + 1, want));
            if (!ok) return p.Label;
        }
        return null;
    }

    /// <summary>True when selection <paramref name="index"/> (1-based) is a sketch point at <paramref name="want"/> (m).</summary>
    private static bool SelectedPointIs(ISelectionMgr sel, int index, Vec3 want) =>
        (object?)sel.GetSelectedObject6(index, -1) is ISketchPoint sp && SketchPointModel(sp).DistanceTo(want) < 1e-8;

    // ------------------------------------------------------------------ shared helpers

    private static RefResult Result(SwContext ctx, IModelDoc2 d, FeatureOutcome o, RefFrame frame, List<ResolvedInfo> resolved)
    {
        var fr = FeatureVerifier.Result(ctx, d, o, resolved: resolved.Count == 0 ? null : resolved);
        return new RefResult
        {
            Doc = fr.Doc,
            Feature = fr.Feature,
            AlsoCreated = fr.AlsoCreated,
            Frame = frame,
            Verified = fr.Verified,
            Dimensions = fr.Dimensions is { Count: > 0 } dims ? dims : null,
            Resolved = fr.Resolved,
        };
    }

    /// <summary>True when the tree holds no feature created since <paramref name="before"/> (a rollback was complete).</summary>
    private static bool NothingNew(IModelDoc2 d, Snapshot before) => Snapshot.Take(d).Features.Keys.All(before.Features.ContainsKey);

    /// <summary>Adds <paramref name="hint"/> to errors that carry none (the verifier's messages say what failed, not what to change).</summary>
    private static T WithHint<T>(string hint, Func<T> f)
    {
        try
        {
            return f();
        }
        catch (SwException e) when (e.Hint is null)
        {
            throw new SwException(e.Code, e.Message, hint, e.InnerException);
        }
    }

    private static string Kind(string? kind, string[] valid)
    {
        string k = (kind ?? "").Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
        if (valid.Contains(k)) return k;
        throw SwException.InvalidArgument($"Unknown kind '{kind}'.", $"Use {string.Join(", ", valid)}.");
    }

    private static void NoOpenSketch(IModelDoc2 d)
    {
        if (d.SketchManager.ActiveSketch is ISketch open)
            throw new SwException(ErrorCodes.SketchState, $"Sketch '{((IFeature)open).Name}' is open.",
                "Finish it with sketch_finish first; reference geometry is created outside sketches.");
    }

    private static void WarnUnused(SwContext ctx, string kind, string[] uses, params (string Name, bool Given)[] args)
    {
        foreach (var (n, given) in args)
            if (given && !uses.Contains(n)) ctx.Warn($"'{n}' is not used by kind '{kind}' (ignored).");
    }

    private static List<Resolved> Entities(SwContext ctx, IModelDoc2 d, IReadOnlyList<EntityRef>? refs, int count, string kind, string what,
        List<ResolvedInfo> resolved, params EntityKind[] kinds)
    {
        if (refs is null || refs.Count == 0) throw SwException.InvalidArgument($"kind '{kind}' needs 'entities': {what}.");
        var list = EntityResolver.ResolveAll(ctx, d, refs, "entities", kinds);
        if (list.Count != count)
            throw SwException.InvalidArgument($"kind '{kind}' needs exactly {count} entit{(count == 1 ? "y" : "ies")} ({what}); 'entities' resolved to {list.Count}: " +
                $"{string.Join(", ", list.Take(6).Select(x => x.Label))}.");
        for (int i = 0; i < list.Count; i++) resolved.Add(list[i].Info($"entities[{i}]"));
        return list;
    }

    private static Vec3 PointArg(double[]? p, string arg, string kind)
    {
        if (p is null) throw SwException.InvalidArgument($"kind '{kind}' needs '{arg}': [x, y, z] in model mm.");
        return Units.PointM(p, arg);
    }

    private static List<Vec3> Points(double[][]? pts, string arg, int count, string kind)
    {
        if (pts is null || pts.Length != count)
            throw SwException.InvalidArgument($"kind '{kind}' needs '{arg}' with exactly {count} points [x, y, z] (model mm); got {pts?.Length ?? 0}.");
        return pts.Select((p, i) => Units.PointM(p, $"{arg}[{i}]")).ToList();
    }

    private static void Rename(SwContext ctx, IFeature f, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        f.Name = name;
        if (f.Name != name) ctx.Warn($"Could not rename '{f.Name}' to '{name}' (the name may be in use).");
    }

    private static double Mm(double m) => Clean(Units.Mm(m));

    private static double Clean(double v) => v == 0 ? 0 : v; // no "-0" in results

    private static double[] Clean(double[] v) => v.Select(Clean).ToArray();

    private static string FmtDir(Vec3 unit) => TopoQuery.Fmt(Clean(Units.Unit(unit)));

    private static string Fmt(double[] mm) => TopoQuery.Fmt(Clean(mm));
}
