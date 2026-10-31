using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Services;
using Xunit;

namespace SolidWorksMcp.LiveTests;

/// <summary>
/// Surfaces, curves and shape features against a live SOLIDWORKS (skipped without SWMCP_LIVE_TESTS=1): fills, trims,
/// extends and untrims; projected, composite and XYZ curves; wraps, domes and indents. Oracles: exact areas,
/// lengths and volumes.
/// </summary>
public class SurfaceShapeLiveTests(LiveFixture fx) : LiveTest(fx)
{
    private const double A = 1e-3; // mm² / mm³
    private const double L = 1e-3; // mm

    private SketchService Sk => new(S);
    private FeatureService Fs => new(S);
    private SurfaceService Sf => new(S);
    private ReferenceService Ref => new(S);
    private PartFeatureService Pf => new(S);

    private static EntityRef At(params double[] p) => new() { At = p };
    private static EntityRef Edge(params double[] p) => new() { Type = "edge", At = p };
    private static EntityRef Face(params double[] p) => new() { Type = "face", At = p };

    /// <summary>A 40 × 20 rectangle (x ±20, z ±10) on the Top plane extruded 10 up as a surface: an open tube.</summary>
    private async Task<string> TubeAsync(string name)
    {
        string doc = await NewPartAsync(name);
        await Sk.StartAsync(doc, new EntityRef { Name = "top" }, false);
        await Sk.AddAsync(doc, [new SketchEntity { Kind = "rectangle", Center = [0, 0], WidthMm = 40, HeightMm = 20 }]);
        await Sf.ExtrudeAsync(new SurfaceExtrudeRequest { Doc = doc, DepthMm = 10 });
        return doc;
    }

    private static EntityRef[] Loop(double y) => [Edge(0, y, 10), Edge(0, y, -10), Edge(20, y, 0), Edge(-20, y, 0)];

    [Fact]
    public async Task A_fill_patches_the_open_end_of_a_tube_with_the_openings_area()
    {
        string doc = await TubeAsync("fill_patch");
        var r = await Sf.FillAsync(new SurfaceFillRequest { Doc = doc, Edges = Loop(10) });
        Assert.Contains(r.Verified.Checks!, c => c.Contains("equals the planar loop's area 800 mm²"));
        Assert.Equal(ErrorCodes.InvalidArgument, (await Assert.ThrowsAsync<SwException>(() =>
            Sf.FillAsync(new SurfaceFillRequest { Doc = doc, Edges = Loop(0).Take(3).ToArray() }))).Code); // not a closed loop
    }

    [Fact]
    public async Task Two_merged_fills_close_the_tube_into_a_solid()
    {
        string doc = await TubeAsync("fill_solid");
        await Sf.FillAsync(new SurfaceFillRequest { Doc = doc, Edges = Loop(10), Merge = true });
        var r = await Sf.FillAsync(new SurfaceFillRequest { Doc = doc, Edges = Loop(0), Merge = true, Solid = true });
        Assert.True(r.SolidFormed);
        Assert.Equal(40 * 20 * 10, r.Model.VolumeMm3, A);
    }

    [Fact]
    public async Task Trim_and_extend_change_a_planar_surface_by_exact_areas()
    {
        string doc = await NewPartAsync("trim_extend");
        await Sk.StartAsync(doc, new EntityRef { Name = "top" }, false);
        await Sk.AddAsync(doc, [new SketchEntity { Kind = "rectangle", Center = [0, 0], WidthMm = 100, HeightMm = 60 }]);
        await Sf.PlanarAsync(doc, null, null, false);

        var t = await Sf.TrimAsync(new SurfaceTrimRequest { Doc = doc, Kind = "trim", Tool = new EntityRef { Name = "right" }, KeepAt = [[25, 0, 0]] });
        Assert.Contains(t.Verified.Checks!, c => c.Contains("surface area 6000 → 3000 mm²"));

        var e = await Sf.TrimAsync(new SurfaceTrimRequest { Doc = doc, Kind = "extend", Edges = [Edge(0, 0, 0)], DistanceMm = 20, Linear = true });
        Assert.Contains(e.Verified.Checks!, c => c.Contains("grew 1200 mm² = edge length × distance"));
        Assert.Equal(ErrorCodes.InvalidArgument, (await Assert.ThrowsAsync<SwException>(() =>
            Sf.TrimAsync(new SurfaceTrimRequest { Doc = doc, Kind = "trim", Tool = new EntityRef { Name = "right" } }))).Code); // no pieces
    }

    [Fact]
    public async Task Untrim_fills_a_surfaces_hole_with_its_area()
    {
        string doc = await NewPartAsync("untrim");
        await Sk.StartAsync(doc, new EntityRef { Name = "top" }, false);
        await Sk.AddAsync(doc,
        [
            new SketchEntity { Kind = "rectangle", Center = [0, 0], WidthMm = 100, HeightMm = 60 },
            new SketchEntity { Kind = "circle", Center = [0, 0], DiameterMm = 20 },
        ]);
        await Sf.PlanarAsync(doc, null, null, false);
        var u = await Sf.TrimAsync(new SurfaceTrimRequest { Doc = doc, Kind = "untrim", Faces = [Face(30, 0, 0)] });
        Assert.Contains(u.Verified.Checks!, c => c.Contains("= the holes' area"));
        Assert.Equal(6000, Assert.Single(u.Bodies!).AreaMm2, A); // the whole 100 × 60 sheet again
    }

    [Fact]
    public async Task Composite_and_xyz_curves_have_their_lengths_and_points()
    {
        string doc = await NewPartAsync("curves");
        await Sk.StartAsync(doc, new EntityRef { Name = "top" }, false);
        await Sk.AddAsync(doc, [new SketchEntity { Kind = "rectangle", Center = [0, 0], WidthMm = 100, HeightMm = 60 }]);
        await Fs.ExtrudeAsync(new ExtrudeRequest { Doc = doc, DepthMm = 20 });

        var c = await Ref.CurveAsync(new RefCurveRequest { Doc = doc, Kind = "composite", Entities = [Edge(0, 20, 30), Edge(50, 20, 0), Edge(0, 20, -30)] });
        Assert.Equal(260, c.LengthMm, L);
        Assert.Equal(3, c.Segments);
        Assert.Contains(new[] { c.StartMm!, c.EndMm! }, p => Math.Abs(p[0] + 50) < L && Math.Abs(p[2] - 30) < L);

        double[][] pts = [[0, 30, 0], [10, 35, 0], [20, 30, 5], [30, 40, 10]];
        var x = await Ref.CurveAsync(new RefCurveRequest { Doc = doc, Kind = "xyz", Points = pts });
        Assert.Contains(x.Verified.Checks!, s => s.StartsWith("passes through all 4 points", StringComparison.Ordinal));
        Assert.True(x.LengthMm > 11.180 + 9.0 + 14.177);
    }

    [Fact]
    public async Task Projected_curves_wrap_a_line_onto_a_cylinder_and_meet_a_second_sketch()
    {
        string doc = await NewPartAsync("projected");
        // A cylinder r 30 along z (z 0..60) under the Top plane: its top at y = -10.
        await Sk.StartAsync(doc, new EntityRef { Name = "front" }, false);
        await Sk.AddAsync(doc, [new SketchEntity { Kind = "circle", Center = [0, -40], DiameterMm = 60 }]);
        await Fs.ExtrudeAsync(new ExtrudeRequest { Doc = doc, DepthMm = 60 });
        // A 40 mm line on the Top plane at z = 10 (sketch y = -z).
        var s1 = await Sk.StartAsync(doc, new EntityRef { Name = "top" }, false);
        await Sk.AddAsync(doc, [new SketchEntity { Kind = "line", From = [-20, -10], To = [20, -10] }]);
        await Sk.FinishAsync(doc);
        var p = await Ref.CurveAsync(new RefCurveRequest { Doc = doc, Kind = "projected", Sketch = s1.Sketch, Faces = [Face(0, -10, 10)] });
        // The line's extrusion crosses the cylinder twice: the arc on its top and the one on its bottom.
        Assert.Equal(2, p.Segments);
        Assert.Equal(2 * 2 * 30 * Math.Asin(20.0 / 30), p.LengthMm, L);

        // Sketch on sketch: a line along x on Top and a sloped line on Front meet in that sloped line.
        var a = await Sk.StartAsync(doc, new EntityRef { Name = "top" }, false);
        await Sk.AddAsync(doc, [new SketchEntity { Kind = "line", From = [0, 0], To = [100, 0] }]);
        await Sk.FinishAsync(doc);
        var b = await Sk.StartAsync(doc, new EntityRef { Name = "front" }, false);
        await Sk.AddAsync(doc, [new SketchEntity { Kind = "line", From = [0, 0], To = [100, 50] }]);
        await Sk.FinishAsync(doc);
        var q = await Ref.CurveAsync(new RefCurveRequest { Doc = doc, Kind = "projected", Sketch = a.Sketch, OntoSketch = b.Sketch });
        Assert.Equal(Math.Sqrt(100 * 100 + 50 * 50), q.LengthMm, L);
    }

    [Fact]
    public async Task A_dome_on_a_round_face_adds_the_spherical_cap_and_a_deboss_removes_area_times_depth()
    {
        string doc = await NewPartAsync("dome_wrap");
        await Sk.StartAsync(doc, new EntityRef { Name = "top" }, false);
        await Sk.AddAsync(doc, [new SketchEntity { Kind = "circle", Center = [0, 0], DiameterMm = 40 }]);
        await Fs.ExtrudeAsync(new ExtrudeRequest { Doc = doc, DepthMm = 30 });
        var dome = await Pf.DeformAsync(new DeformRequest { Doc = doc, Kind = "dome", Faces = [Face(0, 30, 0)], DepthMm = 5 });
        double cap = Math.PI * 5 * (3 * 400 + 25) / 6;
        Assert.Equal(cap, dome.Verified.VolumeDeltaMm3, 1e-4 * cap); // SOLIDWORKS' dome is within 0.01 % of a sphere

        // A 10 (around) × 20 (along y) rectangle on the plane tangent at x = 20, debossed 1 mm into the r 20 cylinder:
        // the wrap keeps arc lengths, so the pocket is the annular sector (θ/2)(R² − (R − t)²)·h with θ = 10/20.
        await Ref.PlaneAsync(new RefPlaneRequest { Doc = doc, Kind = "offset", Base = new EntityRef { Name = "right" }, OffsetMm = 20 });
        var s = await Sk.StartAsync(doc, new EntityRef { Name = "Plane1" }, false);
        Assert.Equal(1, s.Frame!.YAxis[1], 1e-9);
        await Sk.AddAsync(doc, [new SketchEntity { Kind = "rectangle", Center = [0, 15], WidthMm = 10, HeightMm = 20 }]);
        await Sk.FinishAsync(doc);
        var w = await Pf.DeformAsync(new DeformRequest { Doc = doc, Kind = "wrap", Type = "deboss", Sketch = s.Sketch, Faces = [Face(20, 15, 0)], DepthMm = 1 });
        Assert.Equal(-0.25 * (400 - 361) * 20, w.Verified.VolumeDeltaMm3, 0.05);
    }

    [Fact]
    public async Task An_indent_cut_takes_the_tools_overlap_out_of_the_block()
    {
        string doc = await NewPartAsync("indent");
        await Sk.StartAsync(doc, new EntityRef { Name = "top" }, false);
        await Sk.AddAsync(doc, [new SketchEntity { Kind = "rectangle", Center = [0, 0], WidthMm = 100, HeightMm = 60 }]);
        await Fs.ExtrudeAsync(new ExtrudeRequest { Doc = doc, DepthMm = 20 });
        // A Ø20 rod from y = 10 to 40 as a second body: 10 mm of it inside the block.
        await new ReferenceService(S).PlaneAsync(new RefPlaneRequest { Doc = doc, Kind = "offset", Base = new EntityRef { Name = "top" }, OffsetMm = 10 });
        await Sk.StartAsync(doc, new EntityRef { Name = "Plane1" }, false);
        await Sk.AddAsync(doc, [new SketchEntity { Kind = "circle", Center = [0, 0], DiameterMm = 20 }]);
        await Fs.ExtrudeAsync(new ExtrudeRequest { Doc = doc, DepthMm = 30, Merge = false });
        var r = await Pf.DeformAsync(new DeformRequest { Doc = doc, Kind = "indent", Body = At(40, 20, 20), Tool = At(10, 30, 0), Cut = true });
        Assert.Contains(r.Verified.Checks!, c => c.Contains("the cut removed the overlap"));
        Assert.Equal(100 * 60 * 20 - Math.PI * 100 * 10 + Math.PI * 100 * 30, r.Model.VolumeMm3, A); // block − overlap + the whole rod
    }
}
