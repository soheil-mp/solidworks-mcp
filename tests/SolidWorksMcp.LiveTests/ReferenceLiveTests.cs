using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Services;
using Xunit;

namespace SolidWorksMcp.LiveTests;

/// <summary>
/// Reference geometry (ref_plane, ref_axis, ref_point, ref_coordsys) and measurement (body_list, model_measure,
/// model_mass, model_check) against a live SOLIDWORKS, with analytic oracles (DESIGN §6.3).
/// </summary>
public class ReferenceLiveTests(LiveFixture fx) : LiveTest(fx)
{
    private const double L = 1e-6; // mm: positions and distances read back
    private const double U = 1e-6; // unit-vector components
    private const double V = 1e-3; // mm³ / mm² for planar oracles

    private ReferenceService Ref => new(S);
    private MeasureService Measure => new(S);

    // ------------------------------------------------------------------ fixtures

    /// <summary>A 100 (x) × 20 (y) × 60 (z) block on the top plane: x ∈ [-50, 50], y ∈ [0, 20], z ∈ [-30, 30].</summary>
    private async Task<string> BlockAsync(string name)
    {
        string doc = await NewPartAsync(name);
        var sk = new SketchService(S);
        await sk.StartAsync(doc, new EntityRef { Name = "top" }, false);
        await sk.AddAsync(doc, [new SketchEntity { Kind = "rectangle", Center = [0, 0], WidthMm = 100, HeightMm = 60 }]);
        var r = await new FeatureService(S).ExtrudeAsync(new ExtrudeRequest { Doc = doc, DepthMm = 20 });
        Assert.Equal(120000, r.Verified.VolumeDeltaMm3, V);
        return doc;
    }

    /// <summary>The block with a Ø10 hole through it along y at x = z = 0.</summary>
    private async Task<string> BlockWithHoleAsync(string name)
    {
        string doc = await BlockAsync(name);
        var sk = new SketchService(S);
        await sk.StartAsync(doc, new EntityRef { Name = "top" }, false);
        await sk.AddAsync(doc, [new SketchEntity { Kind = "circle", Center = [0, 0], DiameterMm = 10 }]);
        var r = await new FeatureService(S).ExtrudeAsync(new ExtrudeRequest { Doc = doc, Cut = true, End = "through_all" });
        Assert.Equal(-Math.PI * 25 * 20, r.Verified.VolumeDeltaMm3, 1e-2);
        return doc;
    }

    /// <summary>The block plus a separate 20 × 10 × 20 box (4000 mm³) at x ∈ [190, 210], y ∈ [0, 10], z ∈ [-10, 10].</summary>
    private async Task<string> TwoBodiesAsync(string name)
    {
        string doc = await BlockAsync(name);
        var sk = new SketchService(S);
        await sk.StartAsync(doc, new EntityRef { Name = "top" }, false);
        await sk.AddAsync(doc, [new SketchEntity { Kind = "rectangle", Center = [200, 0], WidthMm = 20, HeightMm = 20 }]);
        var r = await new FeatureService(S).ExtrudeAsync(new ExtrudeRequest { Doc = doc, DepthMm = 10, Merge = false });
        Assert.Equal(4000, r.Verified.VolumeDeltaMm3, V);
        return doc;
    }

    private static EntityRef Face(double x, double y, double z) => new() { Type = "face", At = [x, y, z] };
    private static EntityRef Edge(double x, double y, double z) => new() { Type = "edge", At = [x, y, z] };
    private static EntityRef Vertex(double x, double y, double z) => new() { Type = "vertex", At = [x, y, z] };

    private static void Near(double[] expected, double[]? actual, double tol = L)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.Length, actual!.Length);
        for (int i = 0; i < expected.Length; i++) Assert.Equal(expected[i], actual[i], tol);
    }

    /// <summary>The unit vector is parallel or anti-parallel to <paramref name="expected"/> (normalized here).</summary>
    private static void Along(double[] expected, double[]? actual)
    {
        Assert.NotNull(actual);
        double len = Math.Sqrt(expected.Sum(v => v * v));
        double dot = expected.Zip(actual!, (a, b) => a / len * b).Sum();
        Assert.Equal(1, Math.Abs(dot), U);
    }

    /// <summary>Signed distance (mm) of a model point from the plane of a frame.</summary>
    private static double PlaneDistance(RefFrame f, double[] p) =>
        p.Select((v, i) => (v - f.OriginMm[i]) * f.Normal![i]).Sum();

    /// <summary>Distance (mm) of a model point from the line of an axis frame.</summary>
    private static double LineDistance(RefFrame f, double[] p)
    {
        var v = p.Select((x, i) => x - f.OriginMm[i]).ToArray();
        double along = v.Select((x, i) => x * f.Direction![i]).Sum();
        return Math.Sqrt(v.Select((x, i) => x - along * f.Direction![i]).Sum(x => x * x));
    }

    private async Task<int> FeatureCount(string doc) => (await new InspectService(S).FeatureListAsync(doc, false, 500, 0)).Total;

    private static async Task<SwException> Fails(string code, Func<Task> call)
    {
        var ex = await Assert.ThrowsAsync<SwException>(call);
        Assert.Equal(code, ex.Code);
        return ex;
    }

    // ------------------------------------------------------------------ ref_plane

    [Fact]
    public async Task Offset_plane_from_top_lies_at_y_25()
    {
        string doc = await NewPartAsync("plane_offset");
        var r = await Ref.PlaneAsync(new RefPlaneRequest { Doc = doc, Kind = "offset", Base = new EntityRef { Name = "top" }, OffsetMm = 25 });
        Assert.Equal("RefPlane", r.Feature.Type);
        Near([0, 1, 0], r.Frame.Normal, U);
        Assert.Equal(25, r.Frame.OffsetMm!.Value, L);
        Assert.Equal(25, r.Frame.OriginMm[1], L);
        var tree = await new InspectService(S).FeatureListAsync(doc, false, 500, 0);
        Assert.Contains(tree.Features, f => f.Name == r.Feature.Name && f.Type == "RefPlane");
    }

    [Fact]
    public async Task Negative_offset_and_flip_both_put_the_plane_below_top()
    {
        string doc = await NewPartAsync("plane_below");
        var neg = await Ref.PlaneAsync(new RefPlaneRequest { Doc = doc, Kind = "offset", Base = new EntityRef { Name = "top" }, OffsetMm = -10 });
        Assert.Equal(0, PlaneDistance(neg.Frame, [3, -10, 7]), L);
        Along([0, 1, 0], neg.Frame.Normal);
        var flip = await Ref.PlaneAsync(new RefPlaneRequest { Doc = doc, Kind = "offset", Base = new EntityRef { Name = "top" }, OffsetMm = 10, Flip = true });
        Assert.Equal(0, PlaneDistance(flip.Frame, [3, -10, 7]), L);
    }

    [Fact]
    public async Task Offset_from_a_face_goes_outward_and_is_renamed()
    {
        string doc = await BlockAsync("plane_face");
        var r = await Ref.PlaneAsync(new RefPlaneRequest { Doc = doc, Kind = "offset", Base = Face(0, 20, 0), OffsetMm = 5, Name = "Datum25" });
        Assert.Equal("Datum25", r.Feature.Name);
        Along([0, 1, 0], r.Frame.Normal);
        Assert.Equal(0, PlaneDistance(r.Frame, [0, 25, 0]), L); // outward: above the top face (y = 20), not inside the block
        Assert.Equal(0, r.Verified.VolumeDeltaMm3, V);
    }

    [Fact]
    public async Task Angle_plane_turns_about_an_edge_of_the_base()
    {
        string doc = await BlockAsync("plane_angle");
        // Base: the top face (y = 20); axis: its front edge along x at y = 20, z = 30.
        var r = await Ref.PlaneAsync(new RefPlaneRequest { Doc = doc, Kind = "angle", Base = Face(0, 20, 0), Axis = Edge(0, 20, 30), AngleDeg = 30 });
        Assert.Equal(0, r.Frame.Normal![0], U);
        Assert.Equal(Math.Cos(Math.PI / 6), Math.Abs(r.Frame.Normal[1]), U);
        Assert.Equal(0, PlaneDistance(r.Frame, [-40, 20, 30]), L);
        Assert.Equal(0, PlaneDistance(r.Frame, [40, 20, 30]), L);
    }

    [Fact]
    public async Task Three_point_plane_passes_through_the_points_kept_in_a_3d_sketch()
    {
        string doc = await NewPartAsync("plane_3pt");
        double[][] pts = [[0, 0, 0], [10, 0, 0], [0, 10, 10]];
        var r = await Ref.PlaneAsync(new RefPlaneRequest { Doc = doc, Kind = "three_points", Points = pts });
        Along([0, -1, 1], r.Frame.Normal);
        foreach (var p in pts) Assert.Equal(0, PlaneDistance(r.Frame, p), L);
        Assert.Contains(r.AlsoCreated!, f => f.Type == "3DProfileFeature");
    }

    [Fact]
    public async Task Parallel_plane_through_a_point()
    {
        string doc = await NewPartAsync("plane_parallel");
        var r = await Ref.PlaneAsync(new RefPlaneRequest { Doc = doc, Kind = "parallel_point", Base = new EntityRef { Name = "front" }, Point = [5, 5, 12] });
        Along([0, 0, 1], r.Frame.Normal);
        Assert.Equal(12, Math.Abs(r.Frame.OffsetMm!.Value), L);
        Assert.Equal(0, PlaneDistance(r.Frame, [5, 5, 12]), L);
    }

    [Fact]
    public async Task Mid_plane_between_top_and_bottom_faces_is_at_y_10()
    {
        string doc = await BlockAsync("plane_mid");
        var r = await Ref.PlaneAsync(new RefPlaneRequest { Doc = doc, Kind = "mid_plane", Base = Face(0, 20, 0), Second = Face(0, 0, 0) });
        Along([0, 1, 0], r.Frame.Normal);
        Assert.Equal(0, PlaneDistance(r.Frame, [0, 10, 0]), L);
    }

    [Fact]
    public async Task Plane_normal_to_an_edge_through_a_point_on_it()
    {
        string doc = await BlockAsync("plane_normal");
        var r = await Ref.PlaneAsync(new RefPlaneRequest { Doc = doc, Kind = "normal_to_curve", Base = Edge(0, 20, 30), Point = [20, 20, 30] });
        Along([1, 0, 0], r.Frame.Normal);
        Assert.Equal(0, PlaneDistance(r.Frame, [20, 0, 0]), L);
        Assert.Contains(r.AlsoCreated!, f => f.Type == "3DProfileFeature");
    }

    [Fact]
    public async Task Plane_normal_to_an_edge_defaults_to_its_start_vertex()
    {
        string doc = await BlockAsync("plane_normal_start");
        var r = await Ref.PlaneAsync(new RefPlaneRequest { Doc = doc, Kind = "normal_to_curve", Base = Edge(0, 20, 30) });
        Along([1, 0, 0], r.Frame.Normal);
        Assert.Equal(50, Math.Abs(r.Frame.OffsetMm!.Value), L); // one end of the edge: x = ±50
        Assert.Null(r.AlsoCreated); // the vertex is referenced directly: no helper sketch
    }

    [Fact]
    public async Task Plane_arguments_are_validated_before_anything_is_created()
    {
        string doc = await BlockWithHoleAsync("plane_invalid");
        int before = await FeatureCount(doc);
        await Fails(ErrorCodes.InvalidArgument, () => Ref.PlaneAsync(new RefPlaneRequest { Doc = doc, Kind = "offset", Base = new EntityRef { Name = "top" } }));
        await Fails(ErrorCodes.InvalidArgument, () => Ref.PlaneAsync(new RefPlaneRequest { Doc = doc, Kind = "sideways", Base = new EntityRef { Name = "top" }, OffsetMm = 5 }));
        await Fails(ErrorCodes.InvalidArgument, () => Ref.PlaneAsync(new RefPlaneRequest
        {
            Doc = doc, Kind = "three_points", Points = [[0, 0, 0], [10, 0, 0], [20, 0, 0]],
        }));
        // The axis must lie in the base: a vertical edge (along y) of the block doesn't lie in the top face.
        await Fails(ErrorCodes.InvalidArgument, () => Ref.PlaneAsync(new RefPlaneRequest
        {
            Doc = doc, Kind = "angle", Base = Face(20, 20, 10), Axis = Edge(50, 10, 30), AngleDeg = 45,
        }));
        // The hole's wall is not planar.
        await Fails(ErrorCodes.InvalidArgument, () => Ref.PlaneAsync(new RefPlaneRequest { Doc = doc, Kind = "offset", Base = Face(5, 10, 0), OffsetMm = 5 }));
        // A point off the curve.
        await Fails(ErrorCodes.InvalidArgument, () => Ref.PlaneAsync(new RefPlaneRequest
        {
            Doc = doc, Kind = "normal_to_curve", Base = Edge(0, 20, 30), Point = [0, 25, 30],
        }));
        Assert.Equal(before, await FeatureCount(doc));
    }

    [Fact]
    public async Task Reference_geometry_refuses_while_a_sketch_is_open()
    {
        string doc = await NewPartAsync("plane_sketch_open");
        var sk = new SketchService(S);
        await sk.StartAsync(doc, new EntityRef { Name = "front" }, false);
        await Fails(ErrorCodes.SketchState, () => Ref.PlaneAsync(new RefPlaneRequest { Doc = doc, Kind = "offset", Base = new EntityRef { Name = "top" }, OffsetMm = 5 }));
        await Fails(ErrorCodes.SketchState, () => Ref.PointAsync(doc, "xyz", [1, 2, 3], null, null));
        await sk.FinishAsync(doc);
    }

    // ------------------------------------------------------------------ ref_axis

    [Fact]
    public async Task Axis_from_front_and_right_planes_is_the_y_axis()
    {
        string doc = await NewPartAsync("axis_planes");
        var r = await Ref.AxisAsync(doc, "two_planes", [new EntityRef { Name = "front" }, new EntityRef { Name = "right" }], null, null);
        Assert.Equal("RefAxis", r.Feature.Type);
        Along([0, 1, 0], r.Frame.Direction);
        Assert.Equal(0, LineDistance(r.Frame, [0, 0, 0]), L);
    }

    [Fact]
    public async Task Axis_through_two_points()
    {
        string doc = await NewPartAsync("axis_points");
        var r = await Ref.AxisAsync(doc, "two_points", null, [[0, 0, 0], [10, 10, 0]], "Diagonal");
        Assert.Equal("Diagonal", r.Feature.Name);
        Along([1, 1, 0], r.Frame.Direction);
        Assert.Equal(0, LineDistance(r.Frame, [5, 5, 0]), L);
        Assert.Contains(r.AlsoCreated!, f => f.Type == "3DProfileFeature");
    }

    [Fact]
    public async Task Axis_of_a_hole_is_its_cylinder_axis()
    {
        string doc = await BlockWithHoleAsync("axis_cylinder");
        var r = await Ref.AxisAsync(doc, "cylinder", [Face(5, 10, 0)], null, null);
        Along([0, 1, 0], r.Frame.Direction);
        Assert.Equal(0, LineDistance(r.Frame, [0, 7, 0]), L);
    }

    [Fact]
    public async Task Axis_along_an_edge()
    {
        string doc = await BlockAsync("axis_edge");
        var r = await Ref.AxisAsync(doc, "edge", [Edge(0, 20, 30)], null, null);
        Along([1, 0, 0], r.Frame.Direction);
        Assert.Equal(0, LineDistance(r.Frame, [0, 20, 30]), L);
    }

    [Fact]
    public async Task Axis_arguments_are_validated()
    {
        string doc = await BlockWithHoleAsync("axis_invalid");
        int before = await FeatureCount(doc);
        // The top plane and the block's bottom face are parallel: no intersection line.
        await Fails(ErrorCodes.InvalidArgument, () => Ref.AxisAsync(doc, "two_planes", [new EntityRef { Name = "top" }, Face(20, 0, 10)], null, null));
        // A circular edge is not straight.
        await Fails(ErrorCodes.InvalidArgument, () => Ref.AxisAsync(doc, "edge", [Edge(5, 20, 0)], null, null));
        await Fails(ErrorCodes.InvalidArgument, () => Ref.AxisAsync(doc, "two_points", null, [[1, 1, 1], [1, 1, 1]], null));
        Assert.Equal(before, await FeatureCount(doc));
    }

    // ------------------------------------------------------------------ ref_point

    [Fact]
    public async Task Point_at_coordinates()
    {
        string doc = await NewPartAsync("point_xyz");
        var r = await Ref.PointAsync(doc, "xyz", [10, 20, 30], null, null);
        Assert.Equal("RefPoint", r.Feature.Type);
        Near([10, 20, 30], r.Frame.OriginMm);
        Assert.Contains(r.AlsoCreated!, f => f.Type == "3DProfileFeature");
    }

    [Fact]
    public async Task Point_at_the_center_of_a_hole_edge()
    {
        string doc = await BlockWithHoleAsync("point_arc");
        var r = await Ref.PointAsync(doc, "arc_center", null, [Edge(5, 20, 0)], null);
        Near([0, 20, 0], r.Frame.OriginMm);
    }

    [Fact]
    public async Task Point_at_the_center_of_the_top_face()
    {
        string doc = await BlockAsync("point_face");
        var r = await Ref.PointAsync(doc, "face_center", null, [Face(10, 20, 10)], null);
        Near([0, 20, 0], r.Frame.OriginMm);
    }

    [Fact]
    public async Task Point_at_the_intersection_of_two_edges()
    {
        string doc = await BlockAsync("point_intersection");
        var r = await Ref.PointAsync(doc, "intersection", null, [Edge(0, 20, 30), Edge(50, 20, 0)], null);
        Near([50, 20, 30], r.Frame.OriginMm);
    }

    [Fact]
    public async Task Point_arguments_are_validated()
    {
        string doc = await BlockAsync("point_invalid");
        int before = await FeatureCount(doc);
        await Fails(ErrorCodes.InvalidArgument, () => Ref.PointAsync(doc, "xyz", null, null, null));
        await Fails(ErrorCodes.InvalidArgument, () => Ref.PointAsync(doc, "arc_center", null, [Edge(0, 20, 30)], null));
        // The front and back top edges are parallel.
        await Fails(ErrorCodes.InvalidArgument, () => Ref.PointAsync(doc, "intersection", null, [Edge(0, 20, 30), Edge(0, 20, -30)], null));
        Assert.Equal(before, await FeatureCount(doc));
    }

    // ------------------------------------------------------------------ ref_coordsys

    [Fact]
    public async Task Coordinate_system_at_a_point_has_the_model_axes()
    {
        string doc = await NewPartAsync("csys_point");
        var r = await Ref.CoordSysAsync(doc, [10, 0, 0], null, null, null, "Shifted");
        Assert.Equal("Shifted", r.Feature.Name);
        Near([10, 0, 0], r.Frame.OriginMm);
        Near([1, 0, 0], r.Frame.XAxis, U);
        Near([0, 1, 0], r.Frame.YAxis, U);
        Near([0, 0, 1], r.Frame.ZAxis, U);
    }

    [Fact]
    public async Task Coordinate_system_on_a_corner_with_an_edge_as_x()
    {
        string doc = await BlockAsync("csys_corner");
        // Origin: the corner (50, 20, 30); x: the edge along z at x = 50, y = 20.
        var r = await Ref.CoordSysAsync(doc, null, Vertex(50, 20, 30), Edge(50, 20, 0), null, null);
        Near([50, 20, 30], r.Frame.OriginMm);
        Along([0, 0, 1], r.Frame.XAxis);
        Assert.Equal(2, r.Resolved!.Count);
    }

    [Fact]
    public async Task Coordinate_system_arguments_are_validated()
    {
        string doc = await BlockWithHoleAsync("csys_invalid");
        int before = await FeatureCount(doc);
        await Fails(ErrorCodes.InvalidArgument, () => Ref.CoordSysAsync(doc, null, null, null, null, null));
        await Fails(ErrorCodes.InvalidArgument, () => Ref.CoordSysAsync(doc, [0, 0, 0], Vertex(50, 20, 30), null, null, null));
        await Fails(ErrorCodes.InvalidArgument, () => Ref.CoordSysAsync(doc, [0, 0, 0], null, Edge(5, 20, 0), null, null));
        // x and y must be perpendicular: two parallel edges.
        await Fails(ErrorCodes.InvalidArgument, () => Ref.CoordSysAsync(doc, [0, 0, 0], null, Edge(0, 20, 30), Edge(0, 20, -30), null));
        Assert.Equal(before, await FeatureCount(doc));
    }

    // ------------------------------------------------------------------ body_list

    [Fact]
    public async Task Body_list_reports_the_block()
    {
        string doc = await BlockAsync("bodies_block");
        var r = await Measure.BodyListAsync(doc);
        Assert.Equal(1, r.Total);
        Assert.Equal(1, r.Solids);
        var b = Assert.Single(r.Bodies);
        Assert.StartsWith("B", b.Id);
        Assert.Equal("solid", b.Type);
        Assert.Equal(120000, b.VolumeMm3!.Value, V);
        Assert.Equal(2 * (100 * 20 + 100 * 60 + 20 * 60), b.AreaMm2, V);
        Near([100, 20, 60], b.Box!.Size, 1e-2);
        Assert.Equal(6, b.Faces);
        Assert.Equal(12, b.Edges);
        Assert.Null(b.Hidden);
        // The id is stable across listings.
        Assert.Equal(b.Id, Assert.Single((await Measure.BodyListAsync(doc)).Bodies).Id);
    }

    [Fact]
    public async Task Body_list_of_a_two_body_part()
    {
        string doc = await TwoBodiesAsync("bodies_two");
        var r = await Measure.BodyListAsync(doc);
        Assert.Equal(2, r.Solids);
        Assert.Equal([4000, 120000], r.Bodies.Select(b => Math.Round(b.VolumeMm3!.Value, 3)).Order().ToArray());
    }

    [Fact]
    public async Task Body_list_of_a_closed_document_is_DOC_CLOSED()
    {
        await Fails(ErrorCodes.DocClosed, () => Measure.BodyListAsync("D99999"));
    }

    // ------------------------------------------------------------------ model_measure

    [Fact]
    public async Task Distance_between_top_and_bottom_faces_is_20()
    {
        string doc = await BlockAsync("measure_distance");
        var r = await Measure.MeasureAsync(doc, Face(0, 20, 0), Face(0, 0, 0));
        Assert.Equal(20, r.DistanceMm!.Value, L);
        if (r.NormalDistanceMm is double nd) Assert.Equal(20, nd, L);
        Assert.True(r.Parallel);
        Assert.Equal(2, r.Resolved.Count);
    }

    [Fact]
    public async Task Angle_between_adjacent_faces_is_90()
    {
        string doc = await BlockAsync("measure_angle");
        var r = await Measure.MeasureAsync(doc, Face(0, 20, 0), Face(0, 10, 30));
        Assert.Equal(90, r.AngleDeg!.Value, 1e-6);
        // SOLIDWORKS reports no distance for faces that touch (-1, "not applicable"; VERIFIED live).
        Assert.True(r.DistanceMm is null or 0, $"distance {r.DistanceMm}");
        Assert.True(r.Perpendicular);
    }

    [Fact]
    public async Task Edge_length_face_area_and_vertex_point()
    {
        string doc = await BlockAsync("measure_single");
        var edge = await Measure.MeasureAsync(doc, Edge(0, 20, 30), null);
        Assert.Equal(100, edge.LengthMm!.Value, L);
        Assert.Null(edge.DistanceMm);
        var face = await Measure.MeasureAsync(doc, Face(0, 20, 0), null);
        Assert.Equal(6000, face.AreaMm2!.Value, V);
        if (face.PerimeterMm is double per) Assert.Equal(320, per, L);
        var vertex = await Measure.MeasureAsync(doc, Vertex(50, 20, 30), null);
        Near([50, 20, 30], vertex.PointMm);
    }

    [Fact]
    public async Task Hole_edge_radius_and_distance_to_a_plane()
    {
        string doc = await BlockWithHoleAsync("measure_circle");
        var r = await Measure.MeasureAsync(doc, Edge(5, 20, 0), null);
        Assert.Equal(5, r.RadiusMm ?? r.DiameterMm!.Value / 2, L);
        Assert.NotNull(r.ArcOption);
        // The top circle of the hole lies 20 mm above the top plane.
        var d = await Measure.MeasureAsync(doc, Edge(5, 20, 0), new EntityRef { Name = "top" });
        Assert.Equal(20, d.NormalDistanceMm ?? d.DistanceMm!.Value, L);
    }

    [Fact]
    public async Task Measure_rejects_bad_references()
    {
        string doc = await BlockAsync("measure_invalid");
        await Fails(ErrorCodes.InvalidArgument, () => Measure.MeasureAsync(doc, new EntityRef { At = [0, 20, 0] }, null)); // 'at' needs a type here
        await Fails(ErrorCodes.InvalidArgument, () => Measure.MeasureAsync(doc, Face(0, 20, 0), Face(1, 20, 1)));          // the same face twice
        await Fails(ErrorCodes.InvalidArgument, () => Measure.MeasureAsync(doc, null, null));
        await Fails(ErrorCodes.EntityNotFound, () => Measure.MeasureAsync(doc, new EntityRef { Id = "E999999" }, null));
    }

    // ------------------------------------------------------------------ model_mass

    [Fact]
    public async Task Block_mass_properties_match_the_oracle()
    {
        string doc = await BlockAsync("mass_block");
        var r = await Measure.MassAsync(doc, null);
        Assert.Equal("document", r.Of);
        Assert.Equal(120000, r.VolumeMm3, V);
        Assert.Equal(18400, r.AreaMm2, V);
        Near([0, 10, 0], r.CenterOfMassMm);
        Assert.True(r.DensityKgM3 > 0);
        double m = r.DensityKgM3 * 120000e-9;
        Assert.Equal(m, r.MassKg, 1e-9);
        // Box about its center: I = m/12 (b² + c²) in kg·mm².
        double ixx = m / 12 * (20 * 20 + 60 * 60), iyy = m / 12 * (100 * 100 + 60 * 60), izz = m / 12 * (100 * 100 + 20 * 20);
        var expected = new[] { ixx, iyy, izz }.Order().ToArray();
        var principal = r.PrincipalMomentsKgMm2!.Order().ToArray();
        for (int i = 0; i < 3; i++) Assert.Equal(expected[i], principal[i], expected[i] * 1e-6);
        var t = r.MomentsAtCenterKgMm2!;
        Assert.Equal(ixx, t[0][0], ixx * 1e-6);
        Assert.Equal(iyy, t[1][1], iyy * 1e-6);
        Assert.Equal(izz, t[2][2], izz * 1e-6);
        Assert.Equal(0, t[0][1], ixx * 1e-6);
        Assert.Contains(r.Checks!, c => c.Contains("volume matches"));
        Assert.Equal(1, r.Bodies);
    }

    [Fact]
    public async Task Mass_of_one_body_in_a_two_body_part()
    {
        string doc = await TwoBodiesAsync("mass_body");
        var whole = await Measure.MassAsync(doc, null);
        Assert.Equal(124000, whole.VolumeMm3, V);
        Near([4000.0 * 200 / 124000, (120000.0 * 10 + 4000.0 * 5) / 124000, 0], whole.CenterOfMassMm, 1e-4);

        var bodies = await Measure.BodyListAsync(doc);
        var block = bodies.Bodies.Single(b => Math.Abs(b.VolumeMm3!.Value - 120000) < V);
        var one = await Measure.MassAsync(doc, new EntityRef { Id = block.Id });
        Assert.Equal(120000, one.VolumeMm3, V);
        Near([0, 10, 0], one.CenterOfMassMm);
        Assert.Contains(block.Id, one.Of);
        Assert.Contains(one.Checks!, c => c.StartsWith("restricted to"));

        var small = await Measure.MassAsync(doc, new EntityRef { At = [200, 10, 0] }); // the small box, by a point on its top face
        Assert.Equal(4000, small.VolumeMm3, V);
        Near([200, 5, 0], small.CenterOfMassMm);
    }

    [Fact]
    public async Task Mass_with_an_open_sketch_is_SKETCH_STATE()
    {
        string doc = await BlockAsync("mass_sketch");
        var sk = new SketchService(S);
        await sk.StartAsync(doc, new EntityRef { Name = "front" }, false);
        await Fails(ErrorCodes.SketchState, () => Measure.MassAsync(doc, null));
        await sk.FinishAsync(doc);
        Assert.Equal(120000, (await Measure.MassAsync(doc, null)).VolumeMm3, V);
    }

    [Fact]
    public async Task Mass_of_an_empty_part_or_unknown_body_fails()
    {
        string empty = await NewPartAsync("mass_empty");
        await Fails(ErrorCodes.EntityNotFound, () => Measure.MassAsync(empty, null));
        string doc = await BlockAsync("mass_unknown");
        await Fails(ErrorCodes.EntityNotFound, () => Measure.MassAsync(doc, new EntityRef { Name = "NoSuchBody" }));
    }

    // ------------------------------------------------------------------ model_check

    [Fact]
    public async Task Clean_block_has_no_faults()
    {
        string doc = await BlockWithHoleAsync("check_clean");
        var r = await Measure.CheckAsync(doc, null);
        Assert.True(r.Valid);
        Assert.Equal(0, r.FaultCount);
        var b = Assert.Single(r.Bodies);
        Assert.True(b.Valid);
        Assert.Empty(b.Faults);
    }

    [Fact]
    public async Task Check_of_one_body_in_a_two_body_part()
    {
        string doc = await TwoBodiesAsync("check_one");
        var all = await Measure.CheckAsync(doc, null);
        Assert.Equal(2, all.Bodies.Count);
        var one = await Measure.CheckAsync(doc, new EntityRef { Id = all.Bodies[0].Id });
        Assert.Equal(all.Bodies[0].Id, Assert.Single(one.Bodies).Id);
        Assert.True(one.Valid);
    }

    [Fact]
    public async Task Check_of_an_empty_part_or_unknown_body_fails()
    {
        string empty = await NewPartAsync("check_empty");
        await Fails(ErrorCodes.EntityNotFound, () => Measure.CheckAsync(empty, null));
        string doc = await BlockAsync("check_unknown");
        await Fails(ErrorCodes.EntityNotFound, () => Measure.CheckAsync(doc, new EntityRef { Name = "NoSuchBody" }));
    }

    [Fact]
    public async Task Section_of_the_block_and_of_it_with_a_hole_match_the_formulas_and_faces_off_one_plane_are_refused()
    {
        // The block's top face (y = 20), 100 along x by 60 along z: A = 6000 mm², I = b h³/12 about each centroidal axis.
        var sec = new SectionService(S);
        string doc = await BlockAsync("sec_block");
        var r = await sec.SectionAsync(doc, [Face(0, 20, 0)]);
        Assert.Equal(6000, r.AreaMm2, 1e-6);
        Assert.Equal([0, 20, 0], r.CentroidMm);
        Assert.Equal(60 * Math.Pow(100, 3) / 12, r.Principal[0].InertiaMm4, 0.01); // about z: 5 000 000 mm⁴
        Assert.Equal(1, Math.Abs(r.Principal[0].Direction[2]), 1e-9);
        Assert.Equal(100 * Math.Pow(60, 3) / 12, r.Principal[1].InertiaMm4, 0.01); // about x: 1 800 000 mm⁴
        Assert.Equal(60 * 100 * 100 / 6.0, r.Principal[0].ModulusMm3, 0.01); // b h²/6 = I/50
        Assert.Equal(r.Principal[0].InertiaMm4 + r.Principal[1].InertiaMm4, r.PolarMm4, 0.01);
        Assert.Equal(Math.Sqrt(5e6 / 6000), r.Principal[0].RadiusOfGyrationMm, 1e-6);
        // With the Ø10 hole through its centre: π·10⁴/64 less about both axes.
        string holed = await BlockWithHoleAsync("sec_hole");
        var h = await sec.SectionAsync(holed, [Face(30, 20, 0)]);
        Assert.Equal(6000 - Math.PI * 25, h.AreaMm2, 1e-3);
        Assert.Equal(5e6 - Math.PI * 1e4 / 64, h.Principal[0].InertiaMm4, 0.05);
        Assert.Equal(1.8e6 - Math.PI * 1e4 / 64, h.Principal[1].InertiaMm4, 0.05);
        // The top and the bottom are parallel but not one plane; a side face isn't parallel at all.
        await Fails(ErrorCodes.InvalidArgument, () => sec.SectionAsync(doc, [Face(0, 20, 0), Face(0, 0, 0)]));
        await Fails(ErrorCodes.InvalidArgument, () => sec.SectionAsync(doc, [Face(0, 20, 0), Face(50, 10, 0)]));
    }
}
