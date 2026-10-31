using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Geometry;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Services;
using Xunit;

namespace SolidWorksMcp.LiveTests;

/// <summary>
/// The assembly tools against a live SOLIDWORKS (skipped without SWMCP_LIVE_TESTS=1). Every test builds and saves its own
/// parts (in a fresh temp folder), creates its own assemblies and closes everything again (assemblies first). Oracles:
/// SOLIDWORKS' own assembly-space component boxes and interference volumes (two 20 mm cubes 10 mm apart overlap by
/// 4 000 mm³), Measure distances, and the analytic placement R·p + origin.
/// </summary>
public class AssemblyLiveTests(LiveFixture fx) : LiveTest(fx)
{
    private const double P = 1e-3;  // mm: placements read back (the service checks 1e-6 m itself)
    private const double V = 1e-2;  // mm³: interference volumes of planar overlaps
    private static readonly double[][] Identity = [[1, 0, 0], [0, 1, 0], [0, 0, 1]];
    private static readonly double[][] RotZ90 = [[0, -1, 0], [1, 0, 0], [0, 0, 1]]; // assembly = R·part: x → y, y → -x

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"swmcp_asm_{Guid.NewGuid():N}");
    private readonly string _tag = Guid.NewGuid().ToString("N")[..6];
    private readonly List<string> _asms = [];

    private AssemblyService Asm => new(S);
    private DocumentService Docs => new(S);
    private SketchService Sk => new(S);
    private FeatureService Fs => new(S);

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        Directory.CreateDirectory(_dir);
    }

    public override async ValueTask DisposeAsync()
    {
        if (LiveFixture.Enabled)
        {
            // Assemblies first, newest first (a subassembly after its parent): a part an open assembly references can't be closed.
            foreach (var h in Enumerable.Reverse(_asms))
            {
                try { await Docs.CloseAsync(h, save: false, discard: true); }
                catch (SwException) { /* closed by the test */ }
            }
        }
        try { await base.DisposeAsync(); }
        finally
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* best effort */ }
        }
    }

    // ------------------------------------------------------------------ fixtures

    /// <summary>An open, saved part: its handle, file and file name (component names are "&lt;Name&gt;-1", "-2", …).</summary>
    private sealed record PartFile(string Doc, string Path, string Name);

    private async Task<PartFile> SaveAsync(string doc, string name)
    {
        var r = await Docs.SaveAsync(doc, "as", Path.Combine(_dir, $"{name}_{_tag}.sldprt"), overwrite: false);
        Assert.True(File.Exists(r.SavedTo), $"{r.SavedTo} was not written");
        return new PartFile(doc, r.SavedTo!, Path.GetFileNameWithoutExtension(r.SavedTo!));
    }

    /// <summary>A cube of side <paramref name="s"/> centred on its origin (x, y, z from -s/2 to s/2).</summary>
    private async Task<PartFile> CubeAsync(string name, double s = 20)
    {
        string doc = await NewPartAsync(name);
        await Sk.StartAsync(doc, new EntityRef { Name = "front" }, false);
        await Sk.AddAsync(doc, [new SketchEntity { Kind = "rectangle", Center = [0, 0], WidthMm = s, HeightMm = s }]);
        var r = await Fs.ExtrudeAsync(new ExtrudeRequest { Doc = doc, End = "mid_plane", DepthMm = s });
        Assert.Equal(s * s * s, r.Verified.VolumeDeltaMm3, V);
        AssertBox(r.Model.Box, [-s / 2, -s / 2, -s / 2], [s / 2, s / 2, s / 2]);
        return await SaveAsync(doc, name);
    }

    /// <summary>A 40 (x) × 10 (y) × 20 (z) box with a corner on its origin (x 0..40, y 0..10, z 0..20; box centre (20, 5, 10)).</summary>
    private async Task<PartFile> BoxAsync(string name)
    {
        string doc = await NewPartAsync(name);
        await Sk.StartAsync(doc, new EntityRef { Name = "front" }, false);
        await Sk.AddAsync(doc, [new SketchEntity { Kind = "rectangle", Corners = [[0, 0], [40, 10]] }]);
        var r = await Fs.ExtrudeAsync(new ExtrudeRequest { Doc = doc, DepthMm = 20 });
        Assert.Equal(8000, r.Verified.VolumeDeltaMm3, V);
        AssertBox(r.Model.Box, [0, 0, 0], [40, 10, 20]);
        return await SaveAsync(doc, name);
    }

    /// <summary>A shaft r 5 along +y from its origin (y 0..30).</summary>
    private async Task<PartFile> ShaftAsync(string name)
    {
        string doc = await NewPartAsync(name);
        await Sk.StartAsync(doc, new EntityRef { Name = "top" }, false);
        await Sk.AddAsync(doc, [new SketchEntity { Kind = "circle", Center = [0, 0], RadiusMm = 5 }]);
        var r = await Fs.ExtrudeAsync(new ExtrudeRequest { Doc = doc, DepthMm = 30 });
        Assert.Equal(Math.PI * 25 * 30, r.Verified.VolumeDeltaMm3, 0.05);
        return await SaveAsync(doc, name);
    }

    /// <summary>
    /// A handed L, 10 thick (y 0..10): the outline x 0..30, z 0..20 with the corner x 20..30, z 10..20 cut away, so no turn
    /// of it is its mirror image (12 vertices, 5000 mm³).
    /// </summary>
    private async Task<PartFile> LAsync(string name)
    {
        string doc = await NewPartAsync(name);
        await Sk.StartAsync(doc, new EntityRef { Name = "top" }, false);
        await Sk.AddAsync(doc, [new SketchEntity { Kind = "polyline", Closed = true, Points = [[0, 0, 0], [30, 0, 0], [30, 0, 10], [20, 0, 10], [20, 0, 20], [0, 0, 20]] }]);
        var r = await Fs.ExtrudeAsync(new ExtrudeRequest { Doc = doc, DepthMm = 10 });
        Assert.Equal(5000, r.Verified.VolumeDeltaMm3, V);
        AssertBox(r.Model.Box, [0, 0, 0], [30, 10, 20]);
        return await SaveAsync(doc, name);
    }

    /// <summary>A 40 × 10 × 40 plate (y 0..10, centred in x and z) with an r 5 bore along y through its origin.</summary>
    private async Task<PartFile> PlateAsync(string name)
    {
        string doc = await NewPartAsync(name);
        await Sk.StartAsync(doc, new EntityRef { Name = "top" }, false);
        await Sk.AddAsync(doc, [new SketchEntity { Kind = "rectangle", Center = [0, 0], WidthMm = 40, HeightMm = 40 }]);
        await Fs.ExtrudeAsync(new ExtrudeRequest { Doc = doc, DepthMm = 10 });
        await Sk.StartAsync(doc, new EntityRef { Name = "top" }, false);
        await Sk.AddAsync(doc, [new SketchEntity { Kind = "circle", Center = [0, 0], RadiusMm = 5 }]);
        var r = await Fs.ExtrudeAsync(new ExtrudeRequest { Doc = doc, Cut = true, End = "through_all" });
        Assert.Equal(16000 - Math.PI * 25 * 10, r.Model.VolumeMm3, 0.05);
        return await SaveAsync(doc, name);
    }

    /// <summary>A new owned assembly (closed with discard after the test, before the parts).</summary>
    private async Task<string> NewAssemblyAsync(string name)
    {
        var r = await Docs.NewAsync("assembly", "mmgs", null, $"{name}_{_tag}");
        _asms.Add(r.Document.Handle);
        Assert.Equal("assembly", r.Document.Type);
        return r.Document.Handle;
    }

    private Task<AsmInsertResult> InsertAsync(string asm, string path, double[]? originMm = null, AsmRotation? rotation = null,
        bool? fixedState = null, string? configuration = null) =>
        Asm.InsertAsync(new AsmInsertRequest
        {
            Doc = asm, Path = path, OriginMm = originMm, Rotation = rotation, Fixed = fixedState, Configuration = configuration,
        });

    private Task<AsmMateAddResult> Mate(string asm, string type, EntityRef a, EntityRef b, double? distanceMm = null,
        double? angleDeg = null, string alignment = "closest") =>
        Asm.MateAddAsync(new AsmMateRequest
        {
            Doc = asm, Type = type, A = a, B = b, DistanceMm = distanceMm, AngleDeg = angleDeg, Alignment = alignment,
        });

    private Task<AsmComponentSetResult> Set(AsmComponentSetRequest q) => Asm.ComponentSetAsync(q);

    private Task<AsmMoveResult> Move(AsmMoveRequest q) => Asm.MoveAsync(q);

    private async Task<AsmComponentInfo> ComponentAsync(string asm, string name) =>
        (await Asm.ComponentListAsync(asm, false, 100, 0)).Components.Single(c => c.Name == name);

    private static AsmRotation Rot(double[] axis, double angleDeg, double[]? pointMm = null) =>
        new() { Axis = axis, AngleDeg = angleDeg, PointMm = pointMm };

    private static EntityRef Plane(string component, string plane) => new() { Component = component, Name = plane };

    private static void AssertBox(BoxMm? box, double[] min, double[] max, double tol = 0.01)
    {
        Assert.NotNull(box);
        for (int i = 0; i < 3; i++)
        {
            Assert.True(Math.Abs(min[i] - box.Min[i]) <= tol && Math.Abs(max[i] - box.Max[i]) <= tol,
                $"box [{string.Join(", ", box.Min)}] .. [{string.Join(", ", box.Max)}], expected [{string.Join(", ", min)}] .. [{string.Join(", ", max)}]");
        }
    }

    private static void AssertPoint(double[] want, double[]? got, double tol = P)
    {
        Assert.NotNull(got);
        Assert.True(got.Length == 3 && want.Zip(got).All(x => Math.Abs(x.First - x.Second) <= tol),
            $"got [{string.Join(", ", got)}], expected [{string.Join(", ", want)}]");
    }

    private static void AssertRows(double[][] want, double[][]? got, double tol = 1e-9)
    {
        Assert.NotNull(got);
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                Assert.True(Math.Abs(want[i][j] - got[i][j]) <= tol,
                    $"rotation [{string.Join("; ", got.Select(r => string.Join(", ", r)))}], expected [{string.Join("; ", want.Select(r => string.Join(", ", r)))}]");
    }

    private static void AssertFailed(SwException ex) =>
        Assert.True(ex.Code is ErrorCodes.FeatureFailed or ErrorCodes.RebuildError or ErrorCodes.VerifyFailed,
            $"expected a refused/rolled-back mate, got {ex.ToAgentText()}");

    // ------------------------------------------------------------------ asm_insert

    [Fact]
    public async Task Insert_puts_the_origin_at_originMm_not_the_box_center_and_reports_the_auto_fix()
    {
        var box = await BoxAsync("box");
        string asm = await NewAssemblyAsync("asm_insert");
        // AddComponent5 alone would put the box centre (20, 5, 10) at the point; the origin must land there instead.
        var r = await InsertAsync(asm, box.Path, [100, 200, 300]);
        string name = r.Component.Name;
        Assert.Equal($"{box.Name}-1", name);
        Assert.Equal(1, r.ComponentCount);
        Assert.Equal(box.Path, r.File, ignoreCase: true);
        AssertPoint([100, 200, 300], r.Component.OriginMm);
        AssertRows(Identity, r.Component.Rotation);
        AssertBox(r.Component.Box, [100, 200, 300], [140, 210, 320]); // SOLIDWORKS' assembly-space box
        Assert.True(r.Component.Fixed); // the first component is fixed by SOLIDWORKS, and that is reported
        Assert.Contains("fixed", r.Message);
        Assert.Contains(r.Verified, c => c.StartsWith("component count 0 → 1", StringComparison.Ordinal));
        Assert.Contains(r.Verified, c => c.StartsWith("placed with", StringComparison.Ordinal));

        // By handle (an open, saved document): the second instance floats.
        var r2 = await InsertAsync(asm, box.Doc, [0, 0, 0]);
        Assert.Equal($"{box.Name}-2", r2.Component.Name);
        Assert.False(r2.Component.Fixed);
        Assert.Null(r2.Message);
        AssertPoint([0, 0, 0], r2.Component.OriginMm);
        AssertBox(r2.Component.Box, [0, 0, 0], [40, 10, 20]);

        var list = await Asm.ComponentListAsync(asm, false, 100, 0);
        Assert.Equal(2, list.Total);
        var first = list.Components.Single(c => c.Name == name);
        Assert.Equal(box.Path, first.Path, ignoreCase: true);
        Assert.Equal("resolved", first.Suppression);
        Assert.True(first.Visible);
        Assert.True(first.Fixed);
        Assert.Null(first.Children);
        Assert.Equal(0, first.Mates);
        AssertPoint([100, 200, 300], first.OriginMm);
        var page = await Asm.ComponentListAsync(asm, false, 1, 0);
        Assert.Single(page.Components);
        Assert.True(page.Truncated);
    }

    [Fact]
    public async Task Insert_with_a_rotation_puts_a_known_vertex_where_the_rotation_maps_it()
    {
        // The corner (40, 10, 20) of the box, turned 90° about +z and moved to (100, 0, 0), belongs at (90, 40, 20).
        // SOLIDWORKS' Measure and component box are independent of the ArrayData convention; the resolver's pick (the 'resolved'
        // echo) maps through Geometry/Xform, which assumes row vectors: this test settles that convention.
        var box = await BoxAsync("rotbox");
        string asm = await NewAssemblyAsync("asm_rot");
        var r = await InsertAsync(asm, box.Path, [100, 0, 0], Rot([0, 0, 1], 90), fixedState: false);
        string name = r.Component.Name;
        Assert.False(r.Component.Fixed); // the auto-fixed first component, floated on request
        Assert.Null(r.Message);
        AssertPoint([100, 0, 0], r.Component.OriginMm);
        AssertRows(RotZ90, r.Component.Rotation);
        AssertBox(r.Component.Box, [90, 0, 0], [100, 40, 20]);

        var m = await new MeasureService(S).MeasureAsync(asm,
            new EntityRef { Type = "vertex", Component = name, At = [40, 10, 20] }, new EntityRef { Name = "origin" });
        Assert.Equal(Math.Sqrt(90 * 90 + 40 * 40 + 20 * 20), m.DistanceMm!.Value, P);
        AssertPoint([90, 40, 20], m.DeltaMm?.Select(Math.Abs).ToArray());
        var pick = m.Resolved[0].PickMm;
        Assert.True(pick is not null && Math.Abs(pick[0] - 90) < P && Math.Abs(pick[1] - 40) < P && Math.Abs(pick[2] - 20) < P,
            $"Geometry/Xform maps the corner to [{string.Join(", ", pick ?? [])}], SOLIDWORKS has it at (90, 40, 20); " +
            "[110, -40, 20] would mean ArrayData holds the rotation for column vectors (Xform must transpose it).");
    }

    [Fact]
    public async Task Insert_refuses_unsaved_missing_and_foreign_files_and_needs_an_assembly()
    {
        string unsaved = await NewPartAsync("unsaved");
        var cube = await CubeAsync("negins");
        string asm = await NewAssemblyAsync("asm_negins");

        var ex1 = await Assert.ThrowsAsync<SwException>(() => InsertAsync(asm, unsaved));
        Assert.Equal(ErrorCodes.InvalidArgument, ex1.Code);
        Assert.Contains("saved", ex1.Message);
        var ex2 = await Assert.ThrowsAsync<SwException>(() => InsertAsync(asm, Path.Combine(_dir, "missing.sldprt")));
        Assert.Equal(ErrorCodes.IoError, ex2.Code);
        var ex3 = await Assert.ThrowsAsync<SwException>(() => InsertAsync(asm, Path.Combine(_dir, "part.step")));
        Assert.Equal(ErrorCodes.InvalidArgument, ex3.Code);
        var ex4 = await Assert.ThrowsAsync<SwException>(() => InsertAsync(asm, cube.Path, rotation: Rot([0, 0, 0], 90)));
        Assert.Equal(ErrorCodes.InvalidArgument, ex4.Code);
        var ex5 = await Assert.ThrowsAsync<SwException>(() => InsertAsync(asm, cube.Path, configuration: "NoSuchConfig"));
        Assert.Equal(ErrorCodes.InvalidArgument, ex5.Code);
        Assert.Equal(0, (await Asm.ComponentListAsync(asm, false, 100, 0)).Total);

        // The assembly tools refuse a part document.
        Assert.Equal(ErrorCodes.WrongDocType, (await Assert.ThrowsAsync<SwException>(() => InsertAsync(cube.Doc, cube.Path))).Code);
        Assert.Equal(ErrorCodes.WrongDocType, (await Assert.ThrowsAsync<SwException>(() => Asm.ComponentListAsync(cube.Doc, false, 100, 0))).Code);
        Assert.Equal(ErrorCodes.WrongDocType, (await Assert.ThrowsAsync<SwException>(() => Asm.MateListAsync(cube.Doc, 100, 0))).Code);
        Assert.Equal(ErrorCodes.WrongDocType, (await Assert.ThrowsAsync<SwException>(() => Asm.InterferenceAsync(cube.Doc))).Code);
    }

    [Fact]
    public async Task Subassemblies_are_inserted_and_listed_with_their_children()
    {
        var cube = await CubeAsync("subcube");
        string sub = await NewAssemblyAsync("sub");
        await InsertAsync(sub, cube.Path, [0, 0, 0]);
        await InsertAsync(sub, cube.Path, [30, 0, 0]);
        var saved = await Docs.SaveAsync(sub, "as", Path.Combine(_dir, $"sub_{_tag}.sldasm"), overwrite: false);

        string top = await NewAssemblyAsync("top");
        var s = await InsertAsync(top, saved.SavedTo!, [0, 0, 100]);
        Assert.Equal(2, s.Component.Children);
        AssertPoint([0, 0, 100], s.Component.OriginMm);
        AssertBox(s.Component.Box, [-10, -10, 90], [40, 10, 110]);
        await InsertAsync(top, cube.Path, [-50, 0, 0]);

        Assert.Equal(2, (await Asm.ComponentListAsync(top, false, 100, 0)).Total);
        var all = await Asm.ComponentListAsync(top, allLevels: true, 100, 0);
        Assert.Equal(4, all.Total);
        var inner = all.Components.Where(c => c.Parent == s.Component.Name).ToList();
        Assert.Equal(2, inner.Count);
        Assert.All(inner, c => Assert.StartsWith(s.Component.Name + "/", c.Name));
        // A component inside a subassembly moves with it.
        var ex = await Assert.ThrowsAsync<SwException>(() => Move(new() { Doc = top, Component = inner[0].Name, TranslateMm = [1, 0, 0] }));
        Assert.Equal(ErrorCodes.InvalidArgument, ex.Code);
    }

    // ------------------------------------------------------------------ asm_interference

    [Fact]
    public async Task Interference_of_two_overlapping_cubes_is_4000_mm3_and_none_when_they_only_touch()
    {
        var a = await CubeAsync("cubeA");
        var b = await CubeAsync("cubeB");
        string asm = await NewAssemblyAsync("asm_intf");
        string na = (await InsertAsync(asm, a.Path, [0, 0, 0])).Component.Name;
        string nb = (await InsertAsync(asm, b.Path, [10, 0, 0])).Component.Name;

        var r = await Asm.InterferenceAsync(asm);
        Assert.Equal(1, r.Count);
        var hit = Assert.Single(r.Interferences);
        Assert.Equal(4000, hit.VolumeMm3, V); // 10 × 20 × 20
        Assert.Equal(4000, r.TotalVolumeMm3, V);
        Assert.Contains(na, hit.Components);
        Assert.Contains(nb, hit.Components);
        AssertPoint([5, 0, 0], hit.CenterMm, 0.01); // the overlap x 0..10 (from the overlap body: best guess)
        Assert.Equal(1, (await Asm.InterferenceAsync(asm, [nb])).Count);
        Assert.Equal(1, (await Asm.InterferenceAsync(asm, [na, nb])).Count);

        // Face to face: coincident faces are not an interference unless asked for.
        await Move(new() { Doc = asm, Component = nb, TranslateMm = [20, 0, 0], Absolute = true });
        var clear = await Asm.InterferenceAsync(asm);
        Assert.Equal(0, clear.Count);
        Assert.Empty(clear.Interferences);
        Assert.Equal(0, clear.TotalVolumeMm3, V);
        var touching = await Asm.InterferenceAsync(asm, null, includeCoincident: true);
        Assert.True(touching.IncludeCoincident);
        Assert.True(touching.Count >= 1, "with includeCoincident the touching faces should be reported");
        Assert.All(touching.Interferences, i => Assert.Equal(0, i.VolumeMm3, V));
    }

    // ------------------------------------------------------------------ asm_mate_add / asm_mate_list

    [Fact]
    public async Task Plane_mates_coincident_and_distance_reproduce_the_4000_mm3_overlap()
    {
        var a = await CubeAsync("mateA");
        var b = await CubeAsync("mateB");
        string asm = await NewAssemblyAsync("asm_mates");
        string na = (await InsertAsync(asm, a.Path, [0, 0, 0])).Component.Name;
        string nb = (await InsertAsync(asm, b.Path, [37, 23, -15])).Component.Name;

        var m1 = await Mate(asm, "coincident", Plane(na, "front"), Plane(nb, "front"));
        Assert.Equal("coincident", m1.Mate.Type);
        Assert.Null(m1.Mate.Error);
        Assert.Contains("planes coincide (0 mm apart)", m1.Verified);
        Assert.Contains(nb, m1.Moved!);
        Assert.All(m1.Resolved!, x => Assert.Equal("plane", x.Type));
        var m2 = await Mate(asm, "coincident", Plane(na, "top"), Plane(nb, "top"));
        Assert.Contains("planes coincide (0 mm apart)", m2.Verified);
        var m3 = await Mate(asm, "distance", Plane(na, "right"), Plane(nb, "right"), distanceMm: 10);
        Assert.Equal("distance", m3.Mate.Type);
        Assert.Equal(10, m3.Mate.DistanceMm!.Value, P);
        Assert.NotNull(m3.Mate.Dimension); // D1@Distance1: dimension_set drives it
        Assert.Contains("planes 10 mm apart", m3.Verified);

        var bInfo = m3.Components.Single(c => c.Name == nb);
        Assert.Equal(10, Math.Abs(bInfo.OriginMm![0]), P);
        Assert.Equal(0, bInfo.OriginMm[1], P);
        Assert.Equal(0, bInfo.OriginMm[2], P);
        Assert.Equal("fully", bInfo.Constrained); // GetConstrainedStatus after three plane mates (best guess)
        Assert.Equal(4000, (await Asm.InterferenceAsync(asm)).TotalVolumeMm3, V);

        var list = await Asm.MateListAsync(asm, 100, 0);
        Assert.Equal(3, list.Total);
        Assert.Equal(0, list.InError);
        Assert.Equal(["coincident", "coincident", "distance"], list.Mates.Select(x => x.Type));
        Assert.All(list.Mates, x =>
        {
            Assert.Equal(2, x.Entities.Count);
            Assert.All(x.Entities, e => Assert.Equal("plane", e.Kind));
            Assert.Equal(new[] { na, nb }.Order(StringComparer.Ordinal), x.Entities.Select(e => e.Component ?? "").Order(StringComparer.Ordinal));
            Assert.Null(x.Suppressed);
            Assert.Null(x.Error);
        });
        Assert.Equal(10, list.Mates[2].DistanceMm!.Value, P);
        Assert.Equal(3, (await ComponentAsync(asm, nb)).Mates);
        Assert.Single((await Asm.MateListAsync(asm, 1, 2)).Mates);
    }

    [Fact]
    public async Task Concentric_mate_puts_the_shaft_on_the_bore_axis_and_tangent_lays_one_on_the_plate()
    {
        var plate = await PlateAsync("plate");
        var shaft = await ShaftAsync("shaft");
        string asm = await NewAssemblyAsync("asm_conc");
        string np = (await InsertAsync(asm, plate.Path, [0, 0, 0])).Component.Name;
        // Lying along z, away from the bore: the mate has to turn and move it.
        string ns = (await InsertAsync(asm, shaft.Path, [25, 40, -30], Rot([1, 0, 0], 90))).Component.Name;

        var r = await Mate(asm, "concentric",
            new EntityRef { Component = np, At = [5, 5, 0] },   // the bore, in the plate's coordinates
            new EntityRef { Component = ns, At = [5, 15, 0] }); // the shaft's side, in the shaft's coordinates
        Assert.Equal("concentric", r.Mate.Type);
        Assert.Contains("axes coincide", r.Verified);
        Assert.All(r.Resolved!, x => Assert.Equal("face", x.Type));
        Assert.Contains(ns, r.Moved!);
        var s = r.Components.Single(c => c.Name == ns);
        // The shaft axis (its y axis through its origin) is now the bore axis, the assembly y axis.
        Assert.Equal(0, s.OriginMm![0], P);
        Assert.Equal(0, s.OriginMm[2], P);
        Assert.Equal(1, Math.Abs(s.Rotation![1][1]), 1e-9);
        // r 5 in an r 5 bore: touching, no overlap volume.
        Assert.Equal(0, (await Asm.InterferenceAsync(asm, [np, ns])).TotalVolumeMm3, 1e-3);

        // A second shaft, tangent to the plate's top face (y = 10): its axis ends up 5 mm from it, parallel to it.
        string ns2 = (await InsertAsync(asm, shaft.Path, [0, 60, 60], Rot([1, 0, 0], 90))).Component.Name;
        var t = await Mate(asm, "tangent", new EntityRef { Component = np, At = [15, 10, 15] }, new EntityRef { Component = ns2, At = [5, 15, 0] });
        Assert.Equal("tangent", t.Mate.Type);
        Assert.Contains(t.Verified, c => c.StartsWith("cylinder touches the plane", StringComparison.Ordinal));
        var s2 = t.Components.Single(c => c.Name == ns2);
        Assert.Equal(0, s2.Rotation![1][1], 1e-9); // the shaft axis is horizontal
        Assert.Equal(5, Math.Abs(s2.OriginMm![1] - 10), P);
    }

    [Fact]
    public async Task Parallel_perpendicular_and_angle_mates_orient_the_component()
    {
        var a = await CubeAsync("angA");
        var b = await CubeAsync("angB");
        string asm = await NewAssemblyAsync("asm_angle");
        string na = (await InsertAsync(asm, a.Path, [0, 0, 0])).Component.Name;
        string nb = (await InsertAsync(asm, b.Path, [60, 0, 0])).Component.Name;

        var par = await Mate(asm, "parallel", Plane(na, "top"), Plane(nb, "top"));
        Assert.Equal("parallel", par.Mate.Type);
        Assert.Contains("planes parallel", par.Verified);
        var ang = await Mate(asm, "angle", Plane(na, "front"), Plane(nb, "front"), angleDeg: 30);
        Assert.Equal("angle", ang.Mate.Type);
        Assert.Contains(ang.Verified, c => c.Contains("planes at 30°", StringComparison.Ordinal));
        var rows = ang.Components.Single(c => c.Name == nb).Rotation!;
        Assert.Equal(1, Math.Abs(rows[1][1]), 1e-9);                       // still upright: the top planes stay parallel
        Assert.Equal(Math.Cos(Units.Rad(30)), Math.Abs(rows[2][2]), 1e-9); // turned 30° about y
        var listed = (await Asm.MateListAsync(asm, 100, 0)).Mates.Single(x => x.Type == "angle");
        Assert.True(Math.Abs(listed.AngleDeg!.Value - 30) < 1e-6 || Math.Abs(listed.AngleDeg.Value - 150) < 1e-6, $"angle {listed.AngleDeg}");

        // A third instance, tilted 45° about z: perpendicular turns its top plane square to A's right plane.
        string nc = (await InsertAsync(asm, b.Path, [0, 60, 0], Rot([0, 0, 1], 45))).Component.Name;
        var per = await Mate(asm, "perpendicular", Plane(na, "right"), Plane(nc, "top"));
        Assert.Equal("perpendicular", per.Mate.Type);
        Assert.Contains("planes perpendicular", per.Verified);
        Assert.Equal(0, per.Components.Single(c => c.Name == nc).Rotation![0][1], 1e-9); // its y axis has no x component
    }

    [Fact]
    public async Task Lock_mate_keeps_the_relative_position_of_two_components()
    {
        var a = await CubeAsync("lockA");
        var b = await CubeAsync("lockB");
        string asm = await NewAssemblyAsync("asm_lock");
        string na = (await InsertAsync(asm, a.Path, [0, 0, 0])).Component.Name;
        string nb = (await InsertAsync(asm, b.Path, [30, 5, -7], Rot([0, 1, 0], 30))).Component.Name;

        var r = await Mate(asm, "lock", new EntityRef { Name = na }, new EntityRef { Name = nb });
        Assert.Equal("lock", r.Mate.Type);
        Assert.Contains(r.Verified, c => c.Contains("keep their relative position", StringComparison.Ordinal));
        Assert.Null(r.Moved);
        var info = await ComponentAsync(asm, nb);
        AssertPoint([30, 5, -7], info.OriginMm);
        Assert.Equal("fully", info.Constrained); // best guess for a component locked to a fixed one
    }

    [Fact]
    public async Task Mates_that_cant_hold_are_refused_rolled_back_and_nothing_moves()
    {
        var a = await CubeAsync("negA");
        var b = await CubeAsync("negB");
        string asm = await NewAssemblyAsync("asm_neg");
        string na = (await InsertAsync(asm, a.Path, [0, 0, 0])).Component.Name;
        string nb = (await InsertAsync(asm, b.Path, [50, 0, 0])).Component.Name;

        // Refused before any COM call.
        Assert.Equal(ErrorCodes.InvalidArgument,
            (await Assert.ThrowsAsync<SwException>(() => Mate(asm, "distance", Plane(na, "front"), Plane(nb, "front")))).Code);
        Assert.Equal(ErrorCodes.InvalidArgument,
            (await Assert.ThrowsAsync<SwException>(() => Mate(asm, "glue", Plane(na, "front"), Plane(nb, "front")))).Code);
        Assert.Equal(ErrorCodes.InvalidArgument,
            (await Assert.ThrowsAsync<SwException>(() => Mate(asm, "angle", Plane(na, "front"), Plane(nb, "front"), angleDeg: 270))).Code);
        // Refused after resolving: one component on both sides, or no component at all.
        Assert.Equal(ErrorCodes.InvalidArgument,
            (await Assert.ThrowsAsync<SwException>(() => Mate(asm, "coincident", Plane(nb, "front"), Plane(nb, "top")))).Code);
        Assert.Equal(ErrorCodes.InvalidArgument,
            (await Assert.ThrowsAsync<SwException>(() => Mate(asm, "coincident", new EntityRef { Name = "front" }, new EntityRef { Name = "top" }))).Code);
        Assert.Equal(ErrorCodes.EntityNotFound,
            (await Assert.ThrowsAsync<SwException>(() => Mate(asm, "coincident", Plane(na, "front"), Plane("nope-1", "front")))).Code);

        // Planes can't be concentric: SOLIDWORKS refuses; nothing is left and B stays where it was.
        AssertFailed(await Assert.ThrowsAsync<SwException>(() => Mate(asm, "concentric", Plane(na, "front"), Plane(nb, "front"))));
        Assert.Equal(0, (await Asm.MateListAsync(asm, 100, 0)).Total);
        AssertPoint([50, 0, 0], (await ComponentAsync(asm, nb)).OriginMm);

        // Over-defined: three coincident plane mates place B completely; a 10 mm distance between the right planes conflicts.
        await Mate(asm, "coincident", Plane(na, "front"), Plane(nb, "front"));
        await Mate(asm, "coincident", Plane(na, "top"), Plane(nb, "top"));
        await Mate(asm, "coincident", Plane(na, "right"), Plane(nb, "right"));
        AssertPoint([0, 0, 0], (await ComponentAsync(asm, nb)).OriginMm);
        AssertFailed(await Assert.ThrowsAsync<SwException>(() => Mate(asm, "distance", Plane(na, "right"), Plane(nb, "right"), distanceMm: 10)));
        var after = await Asm.MateListAsync(asm, 100, 0);
        Assert.Equal(3, after.Total);
        Assert.Equal(0, after.InError);
        AssertPoint([0, 0, 0], (await ComponentAsync(asm, nb)).OriginMm);
    }

    // ------------------------------------------------------------------ asm_component_set

    [Fact]
    public async Task Component_set_floats_hides_suppresses_and_returns_the_previous_state()
    {
        var a = await CubeAsync("setA");
        var b = await CubeAsync("setB");
        string asm = await NewAssemblyAsync("asm_set");
        string na = (await InsertAsync(asm, a.Path, [0, 0, 0])).Component.Name;
        string nb = (await InsertAsync(asm, b.Path, [10, 0, 0])).Component.Name; // overlapping A by 4 000 mm³

        var fl = await Set(new() { Doc = asm, Component = na, Fixed = false });
        Assert.True(fl.Previous.Fixed);
        Assert.False(fl.Component.Fixed);
        Assert.Contains("floating", fl.Changed);
        Assert.False((await ComponentAsync(asm, na)).Fixed);
        Assert.Equal(["nothing: it was already as requested"], (await Set(new() { Doc = asm, Component = na, Fixed = false })).Changed);
        var fx = await Set(new() { Doc = asm, Component = nb, Fixed = true });
        Assert.False(fx.Previous.Fixed);
        Assert.True(fx.Component.Fixed);

        var hide = await Set(new() { Doc = asm, Component = nb, Visible = false });
        Assert.True(hide.Previous.Visible);
        Assert.False(hide.Component.Visible);
        Assert.False((await ComponentAsync(asm, nb)).Visible);
        Assert.True((await Set(new() { Doc = asm, Component = nb, Visible = true })).Component.Visible);

        // Suppressed, B has no geometry: the overlap disappears, and comes back with B.
        Assert.Equal(4000, (await Asm.InterferenceAsync(asm)).TotalVolumeMm3, V);
        var sup = await Set(new() { Doc = asm, Component = nb, Suppressed = true });
        Assert.Equal("resolved", sup.Previous.Suppression);
        Assert.Equal("suppressed", sup.Component.Suppression);
        Assert.Equal(0, (await Asm.InterferenceAsync(asm)).Count);
        var unsup = await Set(new() { Doc = asm, Component = nb, Suppressed = false });
        Assert.Equal("suppressed", unsup.Previous.Suppression);
        Assert.Equal("resolved", unsup.Component.Suppression);
        AssertPoint([10, 0, 0], unsup.Component.OriginMm);
        Assert.Equal(4000, (await Asm.InterferenceAsync(asm)).TotalVolumeMm3, V);

        var both = await Set(new() { Doc = asm, Component = na, Fixed = true, Visible = false });
        Assert.Equal(2, both.Changed.Count);
        Assert.True(both.Component.Fixed);
        Assert.False(both.Component.Visible);

        Assert.Equal(ErrorCodes.EntityNotFound,
            (await Assert.ThrowsAsync<SwException>(() => Set(new() { Doc = asm, Component = "nope-1", Fixed = true }))).Code);
        Assert.Equal(ErrorCodes.InvalidArgument,
            (await Assert.ThrowsAsync<SwException>(() => Set(new() { Doc = asm, Component = na }))).Code);
        Assert.Equal(ErrorCodes.InvalidArgument,
            (await Assert.ThrowsAsync<SwException>(() => Set(new() { Doc = asm, Component = na, Configuration = "NoSuchConfig" }))).Code);
        Assert.Equal(ErrorCodes.InvalidArgument,
            (await Assert.ThrowsAsync<SwException>(() => Set(new() { Doc = asm, Component = na, Suppressed = true, Resolved = true }))).Code);
    }

    [Fact]
    public async Task Configurations_are_chosen_at_insert_and_switched_by_component_set()
    {
        // A 20 mm cube whose configuration "Tall" is 40 mm deep (z -20..20): the component box tells which one is used.
        string doc = await NewPartAsync("cfg");
        await Sk.StartAsync(doc, new EntityRef { Name = "front" }, false);
        await Sk.AddAsync(doc, [new SketchEntity { Kind = "rectangle", Center = [0, 0], WidthMm = 20, HeightMm = 20 }]);
        var ex = await Fs.ExtrudeAsync(new ExtrudeRequest { Doc = doc, End = "mid_plane", DepthMm = 20 });
        string depth = ex.Dimensions!.First(d => d.Unit == "mm").Name;
        var props = new PropertiesService(S);
        string original = (await props.ConfigListAsync(doc)).Active;
        await props.ConfigAddAsync(doc, "Tall", activate: true);
        await new FeatureManagementService(S).DimensionSetAsync(doc, [new DimensionValue { Name = depth, Value = 40 }], "this");
        await props.ConfigActivateAsync(doc, original);
        var part = await SaveAsync(doc, "cfg");

        string asm = await NewAssemblyAsync("asm_cfg");
        var std = await InsertAsync(asm, part.Path, [0, 0, 0]);
        Assert.Equal(original, std.Component.Configuration);
        AssertBox(std.Component.Box, [-10, -10, -10], [10, 10, 10]);
        var tall = await InsertAsync(asm, part.Path, [100, 0, 0], configuration: "tall"); // matched case-insensitively
        Assert.Equal("Tall", tall.Component.Configuration);
        AssertBox(tall.Component.Box, [90, -10, -20], [110, 10, 20]);
        AssertPoint([100, 0, 0], tall.Component.OriginMm);

        var sw = await Set(new() { Doc = asm, Component = std.Component.Name, Configuration = "Tall" });
        Assert.Equal(original, sw.Previous.Configuration);
        Assert.Equal("Tall", sw.Component.Configuration);
        AssertBox(sw.Component.Box, [-10, -10, -20], [10, 10, 20]);

        var bad = await Assert.ThrowsAsync<SwException>(() => InsertAsync(asm, part.Path, [0, 0, 50], configuration: "Nope"));
        Assert.Equal(ErrorCodes.InvalidArgument, bad.Code);
        Assert.Contains("Tall", bad.Hint);
        Assert.Equal(2, (await Asm.ComponentListAsync(asm, false, 100, 0)).Total);
    }

    // ------------------------------------------------------------------ asm_component_move

    [Fact]
    public async Task Move_translates_and_rotates_a_floating_component_and_refuses_a_fixed_one()
    {
        var a = await CubeAsync("moveA");
        var b = await BoxAsync("moveB");
        string asm = await NewAssemblyAsync("asm_move");
        string na = (await InsertAsync(asm, a.Path, [0, 0, 0])).Component.Name;
        string nb = (await InsertAsync(asm, b.Path, [50, 0, 0])).Component.Name;

        var t = await Move(new() { Doc = asm, Component = nb, TranslateMm = [5, -3, 2] });
        AssertPoint([55, -3, 2], t.Component.OriginMm);
        AssertPoint([5, -3, 2], t.TranslationMm);
        AssertPoint([50, 0, 0], t.PreviousOriginMm);
        Assert.Equal(0, t.RotatedDeg, 1e-6);
        AssertBox(t.Component.Box, [55, -3, 2], [95, 7, 22]);

        // 90° about +z through its own origin: the origin stays, the box swings to -x.
        var r = await Move(new() { Doc = asm, Component = nb, Rotation = Rot([0, 0, 1], 90) });
        AssertPoint([55, -3, 2], r.Component.OriginMm);
        AssertRows(RotZ90, r.Component.Rotation);
        Assert.Equal(90, r.RotatedDeg, 1e-6);
        AssertBox(r.Component.Box, [45, -3, 2], [55, 37, 22]);

        // Another 90° about the assembly z axis (pointMm at the origin): the origin swings to (3, 55, 2); 180° in all.
        var p = await Move(new() { Doc = asm, Component = nb, Rotation = Rot([0, 0, 1], 90, [0, 0, 0]) });
        AssertPoint([3, 55, 2], p.Component.OriginMm);
        AssertRows([[-1, 0, 0], [0, -1, 0], [0, 0, 1]], p.Component.Rotation);

        // Absolute: the identity orientation at (0, 100, 0).
        var abs = await Move(new() { Doc = asm, Component = nb, TranslateMm = [0, 100, 0], Rotation = Rot([1, 0, 0], 0), Absolute = true });
        AssertPoint([0, 100, 0], abs.Component.OriginMm);
        AssertRows(Identity, abs.Component.Rotation);
        AssertBox(abs.Component.Box, [0, 100, 0], [40, 110, 20]);

        // With a mate the move still happens, and the result warns that the next rebuild pulls it back.
        await Mate(asm, "coincident", Plane(na, "front"), Plane(nb, "front"));
        var w = await Move(new() { Doc = asm, Component = nb, TranslateMm = [0, 0, 5] });
        AssertPoint([0, 100, 5], w.Component.OriginMm);
        Assert.Contains(w.Warnings!, x => x.Contains("mate", StringComparison.Ordinal));

        // Refusals: the fixed first component, nothing to do, pointMm with absolute.
        var fixedEx = await Assert.ThrowsAsync<SwException>(() => Move(new() { Doc = asm, Component = na, TranslateMm = [1, 0, 0] }));
        Assert.Equal(ErrorCodes.InvalidArgument, fixedEx.Code);
        Assert.Contains("fixed", fixedEx.Message);
        AssertPoint([0, 0, 0], (await ComponentAsync(asm, na)).OriginMm);
        Assert.Equal(ErrorCodes.InvalidArgument, (await Assert.ThrowsAsync<SwException>(() => Move(new() { Doc = asm, Component = nb }))).Code);
        Assert.Equal(ErrorCodes.InvalidArgument, (await Assert.ThrowsAsync<SwException>(() =>
            Move(new() { Doc = asm, Component = nb, Rotation = Rot([0, 0, 1], 10, [0, 0, 0]), Absolute = true }))).Code);
    }

    // ------------------------------------------------------------------ asm_replace

    [Fact]
    public async Task Replace_swaps_the_file_keeps_the_plane_mates_and_refuses_bad_files()
    {
        var a = await CubeAsync("repA");
        var b = await CubeAsync("repB");
        var c = await CubeAsync("repC", 30);
        string asm = await NewAssemblyAsync("asm_replace");
        string na = (await InsertAsync(asm, a.Path, [0, 0, 0])).Component.Name;
        string nb = (await InsertAsync(asm, b.Path, [50, 0, 0])).Component.Name;
        await Mate(asm, "coincident", Plane(na, "front"), Plane(nb, "front"));
        await Mate(asm, "coincident", Plane(na, "top"), Plane(nb, "top"));

        var r = await Asm.ReplaceAsync(new AsmReplaceRequest { Doc = asm, Component = nb, Path = c.Path });
        var rep = Assert.Single(r.Replaced);
        Assert.Equal(nb, rep.From);
        Assert.Equal($"{c.Name}-1", rep.To);
        Assert.Equal(b.Path, r.OldPath, ignoreCase: true);
        Assert.Equal(c.Path, r.NewPath, ignoreCase: true);
        Assert.Empty(r.MatesInError); // plane mates survive a replacement (VERIFIED)
        var list = await Asm.ComponentListAsync(asm, false, 100, 0);
        Assert.Equal(2, list.Total);
        Assert.DoesNotContain(list.Components, x => string.Equals(x.Path, b.Path, StringComparison.OrdinalIgnoreCase));
        var nc = list.Components.Single(x => x.Name == rep.To);
        Assert.Equal(c.Path, nc.Path, ignoreCase: true);
        Assert.Equal(2, nc.Mates);
        AssertBox(nc.Box, [nc.OriginMm![0] - 15, -15, -15], [nc.OriginMm[0] + 15, 15, 15]); // the 30 mm cube, on A's front and top planes
        Assert.Equal(2, (await Asm.MateListAsync(asm, 100, 0)).Total);

        Assert.Equal(ErrorCodes.IoError, (await Assert.ThrowsAsync<SwException>(() =>
            Asm.ReplaceAsync(new AsmReplaceRequest { Doc = asm, Component = rep.To, Path = Path.Combine(_dir, "nope.sldprt") }))).Code);
        Assert.Equal(ErrorCodes.InvalidArgument, (await Assert.ThrowsAsync<SwException>(() =>
            Asm.ReplaceAsync(new AsmReplaceRequest { Doc = asm, Component = rep.To, Path = Path.Combine(_dir, "x.step") }))).Code);
        Assert.Equal(ErrorCodes.InvalidArgument, (await Assert.ThrowsAsync<SwException>(() =>
            Asm.ReplaceAsync(new AsmReplaceRequest { Doc = asm, Component = rep.To, Path = c.Path }))).Code);
        Assert.Equal(ErrorCodes.EntityNotFound, (await Assert.ThrowsAsync<SwException>(() =>
            Asm.ReplaceAsync(new AsmReplaceRequest { Doc = asm, Component = nb, Path = b.Path }))).Code); // nb is gone
    }

    // ------------------------------------------------------------------ asm_pattern

    private Task<AsmPatternResult> Pattern(AsmPatternRequest q) => Asm.PatternAsync(q);

    private static double[] Center(BoxMm? b)
    {
        Assert.NotNull(b);
        return [(b.Min[0] + b.Max[0]) / 2, (b.Min[1] + b.Max[1]) / 2, (b.Min[2] + b.Max[2]) / 2];
    }

    [Fact]
    public async Task Linear_pattern_places_instances_at_the_spacing_along_one_and_two_directions()
    {
        var cube = await CubeAsync("lin");
        string asm = await NewAssemblyAsync("asm_pattern_lin");
        string seed = (await InsertAsync(asm, cube.Path, [0, 0, 0])).Component.Name;

        // 3 along +x, 30 mm apart: the 20 mm cube (x -10..10) repeats at x 20..40 and 50..70.
        var r = await Pattern(new() { Doc = asm, Kind = "linear", Components = [seed], Count = 3, SpacingMm = 30, Direction = "+x" });
        Assert.Equal("linear", r.Kind);
        Assert.Equal(2, r.InstanceCount);
        Assert.Equal(3, r.ComponentCount);
        var boxes = r.Instances.Select(i => i.Box).OrderBy(b => b!.Min[0]).ToList();
        AssertBox(boxes[0], [20, -10, -10], [40, 10, 10]);
        AssertBox(boxes[1], [50, -10, -10], [70, 10, 10]);
        Assert.All(r.Instances, i => AssertRows(Identity, i.Rotation));
        Assert.All(r.Instances, i => Assert.Equal(cube.Path, i.Path, ignoreCase: true));
        Assert.Contains(r.Verified, c => c.StartsWith("every instance at the seed moved by", StringComparison.Ordinal));
        Assert.Equal(3, (await Asm.ComponentListAsync(asm, false, 100, 0)).Total);

        // A second pattern of the same seed: 2 along -x (sign given) by 2 along -z, 25 mm apart.
        var r2 = await Pattern(new()
        {
            Doc = asm, Kind = "linear", Components = [seed], Count = 2, SpacingMm = 30, Direction = "-x", Direction2 = "-z", Count2 = 2, Spacing2Mm = 25,
        });
        Assert.Equal(3, r2.InstanceCount);
        var centers = r2.Instances.Select(i => Center(i.Box)).OrderBy(c => c[0]).ThenBy(c => c[2]).ToList();
        AssertPoint([-30, 0, -25], centers[0], 0.01);
        AssertPoint([-30, 0, 0], centers[1], 0.01);
        AssertPoint([0, 0, -25], centers[2], 0.01);
        Assert.Equal(6, r2.ComponentCount);
    }

    [Fact]
    public async Task Linear_pattern_along_an_edge_follows_the_given_sign()
    {
        var box = await BoxAsync("edge"); // x 0..40, y 0..10, z 0..20
        var cube = await CubeAsync("edgecube");
        string asm = await NewAssemblyAsync("asm_pattern_edge");
        string nb = (await InsertAsync(asm, box.Path, [0, 0, 0])).Component.Name;
        string nc = (await InsertAsync(asm, cube.Path, [0, 50, 0])).Component.Name;
        // The box's edge along z at x = y = 0 (part mm); "-z" gives the sign along it.
        var r = await Pattern(new()
        {
            Doc = asm, Kind = "linear", Components = [nc], Count = 3, SpacingMm = 40, Direction = "-z",
            DirectionRef = new EntityRef { Component = nb, Type = "edge", At = [0, 0, 10] },
        });
        Assert.Equal(2, r.InstanceCount);
        var centers = r.Instances.Select(i => Center(i.Box)).OrderBy(c => c[2]).ToList();
        AssertPoint([0, 50, -80], centers[0], 0.01);
        AssertPoint([0, 50, -40], centers[1], 0.01);
        Assert.NotNull(r.Resolved);
    }

    [Fact]
    public async Task Circular_pattern_turns_the_seed_about_a_bore_and_mirror_reflects_an_asymmetric_part()
    {
        var plate = await PlateAsync("hub");   // bore r 5 along y through the origin
        var box = await BoxAsync("arm");       // x 0..40, y 0..10, z 0..20: not symmetric about its own planes
        string asm = await NewAssemblyAsync("asm_pattern_circ");
        string np = (await InsertAsync(asm, plate.Path, [0, 0, 0])).Component.Name;
        string na = (await InsertAsync(asm, box.Path, [30, 20, 0])).Component.Name; // box x 30..70, centre (50, 25, 10)

        // 4 instances about the bore (a cylindrical face, part mm): 90° apart.
        var r = await Pattern(new() { Doc = asm, Kind = "circular", Components = [na], Count = 4, Axis = new EntityRef { Component = np, At = [5, 5, 0] } });
        Assert.Equal(3, r.InstanceCount);
        // Right-hand 90° about +y takes (x, z) to (z, -x): the centre (50, 25, 10) goes to (10, 25, -50); the rotation
        // of each instance is fixed by where it sits (the same whichever way SOLIDWORKS runs the axis).
        var byCenter = r.Instances.ToDictionary(i => Center(i.Box), i => i.Rotation);
        double[][] ry90 = [[0, 0, 1], [0, 1, 0], [-1, 0, 0]];
        double[][] ry180 = [[-1, 0, 0], [0, 1, 0], [0, 0, -1]];
        double[][] ry270 = [[0, 0, -1], [0, 1, 0], [1, 0, 0]];
        foreach (var (want, rot) in new[] { (new double[] { 10, 25, -50 }, ry90), ([-50, 25, -10], ry180), ([-10, 25, 50], ry270) })
        {
            var hit = byCenter.Single(kv => want.Zip(kv.Key).All(x => Math.Abs(x.First - x.Second) <= 0.01));
            AssertRows(rot, hit.Value, 1e-6);
        }

        // Mirror the arm about the assembly's right plane (x = 0): its box x 30..70 lands at x -70..-30.
        var m = await Pattern(new() { Doc = asm, Kind = "mirror", Components = [na], Plane = new EntityRef { Name = "right" } });
        Assert.Equal("mirror", m.Kind);
        var copy = Assert.Single(m.Instances);
        AssertBox(copy.Box, [-70, 20, 0], [-30, 30, 20]);
        Assert.Equal(box.Path, copy.Path, ignoreCase: true);
        Assert.Contains(m.Verified, c => c.Contains("reflection", StringComparison.Ordinal));
        Assert.Equal(6, m.ComponentCount);
    }

    [Fact]
    public async Task Opposite_hand_mirrors_write_new_mirrored_parts_at_the_mirror_image()
    {
        // VERIFIED recipe (IMirrorComponentFeatureData, PreserveZAxis false). The L at x = 40 (box x 40..70, z 0..20) mirrored
        // about the right plane: a new part file next to the seed, its component on x -70..-40, every vertex reflected.
        var l = await LAsync("hand");
        string asm = await NewAssemblyAsync("asm_hand");
        string nl = (await InsertAsync(asm, l.Path, [40, 0, 0])).Component.Name;
        var right = new EntityRef { Name = "right" };
        var m = await Pattern(new() { Doc = asm, Kind = "mirror", Components = [nl], Plane = right, OppositeHand = true });
        Assert.Equal("MirrorCompFeat", m.FeatureType);
        var copy = Assert.Single(m.Instances);
        AssertBox(copy.Box, [-70, 0, 0], [-40, 10, 20]);
        string file = Assert.Single(m.Files!);
        Assert.Equal(Path.Combine(_dir, $"{l.Name}_mirror.sldprt"), file, ignoreCase: true);
        Assert.True(File.Exists(file));
        Assert.Equal(file, copy.Path, ignoreCase: true);
        Assert.Contains(m.Verified, c => c.Contains("all 12 vertices reflected, the same volume", StringComparison.Ordinal));

        // Again: the file exists, so nothing is made; with another suffix about the front plane (z = 0) it works.
        var again = await Assert.ThrowsAsync<SwException>(() => Pattern(new() { Doc = asm, Kind = "mirror", Components = [nl], Plane = right, OppositeHand = true }));
        Assert.Equal(ErrorCodes.IoError, again.Code);
        Assert.Equal(2, (await Asm.ComponentListAsync(asm, false, 100, 0)).Total);
        var lh = await Pattern(new() { Doc = asm, Kind = "mirror", Components = [nl], Plane = new EntityRef { Name = "front" }, OppositeHand = true, Suffix = "-LH" });
        AssertBox(Assert.Single(lh.Instances).Box, [40, 0, -20], [70, 10, 0]);
        Assert.EndsWith($"{l.Name}-LH.sldprt", Assert.Single(lh.Files!), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, lh.ComponentCount);

        // Refused before anything changes: two instances of one part, a bad suffix, a folder outside the write roots,
        // and the opposite-hand options on other kinds.
        string n2 = (await InsertAsync(asm, l.Path, [40, 40, 0])).Component.Name;
        async Task<string> Refused(AsmPatternRequest q) => (await Assert.ThrowsAsync<SwException>(() => Pattern(q))).Code;
        Assert.Equal(ErrorCodes.InvalidArgument, await Refused(new() { Doc = asm, Kind = "mirror", Components = [nl, n2], Plane = right, OppositeHand = true, Suffix = "_x" }));
        Assert.Equal(ErrorCodes.InvalidArgument, await Refused(new() { Doc = asm, Kind = "mirror", Components = [nl], Plane = right, OppositeHand = true, Suffix = "a/b" }));
        Assert.Equal(ErrorCodes.InvalidArgument, await Refused(new() { Doc = asm, Kind = "mirror", Components = [nl], Plane = right, OppositeHand = true, Suffix = "_y", Folder = @"C:\Windows\swmcp" }));
        Assert.Equal(ErrorCodes.InvalidArgument, await Refused(new() { Doc = asm, Kind = "mirror", Components = [nl], Plane = right, Suffix = "_y" }));
        Assert.Equal(ErrorCodes.InvalidArgument, await Refused(new() { Doc = asm, Kind = "linear", Components = [nl], Count = 2, SpacingMm = 50, Direction = "+x", OppositeHand = true }));
        Assert.Equal(4, (await Asm.ComponentListAsync(asm, false, 100, 0)).Total);
        Assert.False(File.Exists(Path.Combine(_dir, $"{l.Name}_x.sldprt")));
    }

    // ------------------------------------------------------------------ asm_bom (M8 WP3)

    [Fact]
    public async Task Bom_counts_top_level_parts_and_indented_lines_and_writes_a_csv()
    {
        // top = 2 x cube + sub; sub = 1 x cube + 1 x box. The cube carries PartNo and Description.
        var cube = await CubeAsync("bomcube");
        var props = new PropertiesService(S);
        await props.PropSetAsync(cube.Doc, "PartNo", "A-100");
        await props.PropSetAsync(cube.Doc, "Description", "Cube, 20 mm");
        var box = await BoxAsync("bombox");
        string sub = await NewAssemblyAsync("bom_sub");
        await InsertAsync(sub, cube.Path, [0, 0, 0]);
        await InsertAsync(sub, box.Path, [50, 0, 0], fixedState: false);
        var subSaved = await Docs.SaveAsync(sub, "as", Path.Combine(_dir, $"bom_sub_{_tag}.sldasm"), false);
        string top = await NewAssemblyAsync("bom_top");
        await InsertAsync(top, cube.Path, [0, 0, 0]);
        await InsertAsync(top, cube.Path, [0, 40, 0], fixedState: false);
        await InsertAsync(top, subSaved.SavedTo!, [0, 0, 100], fixedState: false);

        var t = await Asm.BomAsync(new AsmBomRequest { Doc = top });
        Assert.Equal(3, t.Instances);
        Assert.Equal(2, t.RowCount);
        var cubeRow = t.Rows.Single(r => r.Kind == "part");
        Assert.Equal(("A-100", "Cube, 20 mm", 2), (cubeRow.PartNumber, cubeRow.Description, cubeRow.Quantity));
        Assert.Equal(1, t.Rows.Single(r => r.Kind == "assembly").Quantity);

        var parts = await Asm.BomAsync(new AsmBomRequest { Doc = top, Levels = "parts" });
        Assert.Equal(4, parts.Instances);
        Assert.Equal(3, parts.Rows.Single(r => r.PartNumber == "A-100").Quantity);
        Assert.Equal(1, parts.Rows.Single(r => r.PartNumber == box.Name).Quantity); // no PartNo: the file name

        var ind = await Asm.BomAsync(new AsmBomRequest { Doc = top, Levels = "indented", Path = Path.Combine(_dir, "bom.csv") });
        Assert.Equal(["1", "2", "2.1", "2.2"], ind.Rows.Select(r => r.Item));
        Assert.Equal([2, 1, 1, 1], ind.Rows.Select(r => r.Quantity));
        Assert.True(File.Exists(ind.File));
        Assert.Contains(ind.Checks, c => c.StartsWith("CSV written and read back: 4 row(s)", StringComparison.Ordinal));
        Assert.Contains("\"Cube, 20 mm\"", await File.ReadAllTextAsync(ind.File!));
        var again = await Assert.ThrowsAsync<SwException>(() => Asm.BomAsync(new AsmBomRequest { Doc = top, Path = ind.File }));
        Assert.Equal(ErrorCodes.IoError, again.Code); // never overwritten without overwrite:true
    }

    [Fact]
    public async Task Release_package_writes_a_step_per_model_the_drawing_pdf_the_flat_dxf_and_the_bom()
    {
        // A sheet-metal bracket (80 x 40 x 2, one bend) and a block with a drawing saved next to it, in a saved assembly.
        string bracket = await NewPartAsync("rel_bracket");
        await Sk.StartAsync(bracket, new EntityRef { Name = "top" }, false);
        await Sk.AddAsync(bracket, [new SketchEntity { Kind = "rectangle", Center = [0, 0], WidthMm = 80, HeightMm = 40 }]);
        await Sk.FinishAsync(bracket);
        var sm = new SheetMetalService(S);
        await sm.BaseFlangeAsync(new BaseFlangeRequest { Doc = bracket, ThicknessMm = 2, BendRadiusMm = 2 });
        await sm.BendAsync(new SketchedBendRequest { Doc = bracket, FromMm = [20, 0, -20], ToMm = [20, 0, 20] });
        var br = await SaveAsync(bracket, "rel_bracket");
        var block = await BoxAsync("rel_block");
        var dr = await Docs.NewAsync("drawing", "mmgs", null, $"rel_drw_{_tag}");
        _asms.Add(dr.Document.Handle);
        await new DrawingService(S).ViewsStandardAsync(block.Path, ["front"], null, dr.Document.Handle);
        await Docs.SaveAsync(dr.Document.Handle, "as", Path.ChangeExtension(block.Path, ".slddrw"), false);
        string asm = await NewAssemblyAsync("rel_asm");
        await InsertAsync(asm, br.Path, [0, 0, 0]);
        await InsertAsync(asm, block.Path, [100, 0, 0], fixedState: false);
        await Docs.SaveAsync(asm, "as", Path.Combine(_dir, $"rel_asm_{_tag}.sldasm"), false);

        string folder = Path.Combine(_dir, "release");
        var r = await new ReleaseService(S).ExportAllAsync(new ExportAllRequest { Doc = asm, Folder = folder });
        Assert.Equal(Path.GetFullPath(folder), r.Folder, ignoreCase: true);
        Assert.Equal(3, r.Files.Count(f => f.Format == "step")); // the assembly, the bracket, the block
        var pdf = Assert.Single(r.Files, f => f.Format == "pdf");
        Assert.Equal(Path.GetFileName(Path.ChangeExtension(block.Path, ".slddrw")), pdf.Source, ignoreCase: true);
        var dxf = Assert.Single(r.Files, f => f.Format == "dxf");
        Assert.Contains("rel_bracket", dxf.Source, StringComparison.OrdinalIgnoreCase);
        Assert.Single(r.Files, f => f.Format == "bom");
        Assert.All(r.Files, f => Assert.True(File.Exists(f.Path) && new FileInfo(f.Path).Length > 0, f.Path));
        Assert.Contains(r.Skipped!, s => s.Format == "pdf" && s.Source.Contains("rel_bracket", StringComparison.OrdinalIgnoreCase)); // no drawing
        // Again without overwrite: every file exists, so nothing is written and each is reported.
        var again = await Assert.ThrowsAsync<SwException>(() => new ReleaseService(S).ExportAllAsync(new ExportAllRequest { Doc = asm, Folder = folder, Formats = ["step"] }));
        Assert.Equal(ErrorCodes.FeatureFailed, again.Code);
    }

    [Fact]
    public async Task Pack_and_go_copies_the_assembly_its_parts_and_drawing_and_the_copies_reference_each_other()
    {
        // Pack and Go leaves out files under the Windows temp folder, so this assembly lives in Documents\solidworks-mcp.
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "solidworks-mcp", $"swmcp_pack_{_tag}");
        Directory.CreateDirectory(dir);
        var open = new List<string>();
        try
        {
            async Task<string> BlockAt(string name, double w)
            {
                string p = await NewPartAsync(name);
                open.Add(p);
                await Sk.StartAsync(p, new EntityRef { Name = "top" }, false);
                await Sk.AddAsync(p, [new SketchEntity { Kind = "rectangle", Center = [0, 0], WidthMm = w, HeightMm = 20 }]);
                await Fs.ExtrudeAsync(new ExtrudeRequest { Doc = p, DepthMm = 10 });
                return (await Docs.SaveAsync(p, "as", Path.Combine(dir, name + ".sldprt"), false)).SavedTo!;
            }
            string a = await BlockAt("pk_a", 40), b = await BlockAt("pk_b", 20);
            string asm = await NewAssemblyAsync("pk_asm");
            open.Insert(0, asm);
            await InsertAsync(asm, a, [0, 0, 0]);
            await InsertAsync(asm, b, [0, 10, 0], fixedState: false);
            string asmPath = (await Docs.SaveAsync(asm, "as", Path.Combine(dir, "pk_asm.sldasm"), false)).SavedTo!;
            var dr = await Docs.NewAsync("drawing", "mmgs", null, $"pk_drw_{_tag}");
            open.Insert(0, dr.Document.Handle);
            await new DrawingService(S).ViewsStandardAsync(asmPath, ["front"], null, dr.Document.Handle);
            await Docs.SaveAsync(dr.Document.Handle, "as", Path.ChangeExtension(asmPath, ".slddrw"), false);

            var pack = new PackService(S);
            string to = Path.Combine(dir, "packed");
            var r = await pack.PackAsync(new DocPackRequest { Doc = asm, Folder = to, Prefix = "P_" });
            Assert.Equal(["P_PK_A.SLDPRT", "P_PK_ASM.SLDASM", "P_PK_ASM.SLDDRW", "P_PK_B.SLDPRT"],
                r.Files.Select(f => Path.GetFileName(f.To).ToUpperInvariant()).Order(StringComparer.Ordinal).ToArray());
            Assert.All(r.Files, f => Assert.True(File.Exists(f.To), f.To));
            Assert.Contains(r.Checks, c => c.StartsWith("the 2 copied assembl(ies)/drawing(s) reference the copies only", StringComparison.Ordinal));
            Assert.Contains(r.Checks, c => c == "the open document still has its own file");
            // Again: the copies exist, so nothing is written without overwrite.
            Assert.Equal(ErrorCodes.IoError, (await Assert.ThrowsAsync<SwException>(() => pack.PackAsync(new DocPackRequest { Doc = asm, Folder = to, Prefix = "P_" }))).Code);
            // One part, renamed with a suffix, no drawings.
            var one = await pack.PackAsync(new DocPackRequest { Doc = a, Folder = Path.Combine(dir, "one"), Suffix = "_rev2", IncludeDrawings = false });
            Assert.Equal("pk_a_rev2.SLDPRT", Path.GetFileName(Assert.Single(one.Files).To), ignoreCase: true);
        }
        finally
        {
            foreach (var h in open)
            {
                try { await Docs.CloseAsync(h, save: false, discard: true); }
                catch (SwException) { /* closed already */ }
            }
            try { Directory.Delete(dir, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* best effort */ }
        }
    }

    [Fact]
    public async Task Review_of_an_assembly_counts_the_floating_component_and_lists_a_suppressed_one()
    {
        var cube = await CubeAsync("rv");
        string asm = await NewAssemblyAsync("rv_asm");
        await InsertAsync(asm, cube.Path, [0, 0, 0]); // fixed: the first component
        string free = (await InsertAsync(asm, cube.Path, [0, 50, 0], fixedState: false)).Component.Name;
        var review = new ReviewService(S);
        var r = await review.ReviewAsync(asm);
        Assert.Equal("assembly", r.Type);
        Assert.Equal(0, r.Errors);
        Assert.StartsWith("1 component(s) not fully positioned", Assert.Single(r.Issues, i => i.Kind == "component_floating").Message, StringComparison.Ordinal);
        Assert.Contains(r.Checked, c => c.StartsWith("2 component(s)", StringComparison.Ordinal));
        await Set(new AsmComponentSetRequest { Doc = asm, Component = free, Suppressed = true });
        var r2 = await review.ReviewAsync(asm);
        Assert.Contains(r2.Issues, i => i.Kind == "component_suppressed" && i.Where == free);
        Assert.DoesNotContain(r2.Issues, i => i.Kind == "component_floating");
    }

    [Fact]
    public async Task Pack_and_go_is_refused_for_files_in_the_temp_folder_that_it_would_leave_out()
    {
        // This class saves its files under %TEMP%, which Pack and Go skips: the copies would still point at the originals.
        var cube = await CubeAsync("pk_tmp");
        string asm = await NewAssemblyAsync("pk_tmp_asm");
        await InsertAsync(asm, cube.Path, [0, 0, 0]);
        await Docs.SaveAsync(asm, "as", Path.Combine(_dir, $"pk_tmp_asm_{_tag}.sldasm"), false);
        var refused = await Assert.ThrowsAsync<SwException>(() => new PackService(S).PackAsync(new DocPackRequest { Doc = asm, Folder = Path.Combine(_dir, "packed") }));
        Assert.Equal(ErrorCodes.InvalidArgument, refused.Code);
        Assert.Contains("would leave out", refused.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(_dir, "packed")) && Directory.EnumerateFiles(Path.Combine(_dir, "packed")).Any(), "nothing written");
    }

    [Fact]
    public async Task Pattern_requests_that_cant_work_are_refused_before_anything_changes()
    {
        var cube = await CubeAsync("bad");
        var shaft = await ShaftAsync("badshaft");
        string asm = await NewAssemblyAsync("asm_pattern_bad");
        string nc = (await InsertAsync(asm, cube.Path, [0, 0, 0])).Component.Name;
        string ns = (await InsertAsync(asm, shaft.Path, [50, 0, 0])).Component.Name;

        async Task<string> Refused(AsmPatternRequest q) => (await Assert.ThrowsAsync<SwException>(() => Pattern(q))).Code;
        Assert.Equal(ErrorCodes.InvalidArgument, await Refused(new() { Doc = asm, Kind = "spiral", Components = [nc] }));
        Assert.Equal(ErrorCodes.InvalidArgument, await Refused(new() { Doc = asm, Kind = "linear", Components = [nc], Count = 3, Direction = "+x" })); // no spacing
        Assert.Equal(ErrorCodes.InvalidArgument, await Refused(new() { Doc = asm, Kind = "linear", Components = [nc], Count = 1, SpacingMm = 10, Direction = "+x" }));
        Assert.Equal(ErrorCodes.InvalidArgument, await Refused(new() { Doc = asm, Kind = "linear", Components = [nc, nc], Count = 2, SpacingMm = 10, Direction = "+x" }));
        Assert.Equal(ErrorCodes.InvalidArgument, await Refused(new() { Doc = asm, Kind = "linear", Components = [nc], Count = 2, SpacingMm = 10, Direction = "+x", Plane = new EntityRef { Name = "right" } }));
        Assert.Equal(ErrorCodes.InvalidArgument, await Refused(new() { Doc = asm, Kind = "circular", Components = [nc], Count = 3 })); // no axis
        Assert.Equal(ErrorCodes.InvalidArgument, await Refused(new() { Doc = asm, Kind = "circular", Components = [nc], Count = 5, TotalAngleDeg = 100, EqualSpacing = false, Axis = new EntityRef { Component = ns, At = [5, 10, 0] } }));
        // The shaft's side is round: not a mirror plane.
        Assert.Equal(ErrorCodes.InvalidArgument, await Refused(new() { Doc = asm, Kind = "mirror", Components = [nc], Plane = new EntityRef { Component = ns, At = [5, 10, 0] } }));
        Assert.Equal(ErrorCodes.EntityNotFound, await Refused(new() { Doc = asm, Kind = "mirror", Components = ["nope-1"], Plane = new EntityRef { Name = "right" } }));
        Assert.Equal(2, (await Asm.ComponentListAsync(asm, false, 100, 0)).Total);
    }

    // ------------------------------------------------------------------ more mate types

    private Task<AsmMateAddResult> MateQ(AsmMateRequest q) => Asm.MateAddAsync(q);

    private static EntityRef Face(string component, double x, double y, double z) => new() { Component = component, At = [x, y, z] };

    [Fact]
    public async Task Limit_distance_and_symmetric_mates_hold_their_geometry_and_read_back()
    {
        var a = await CubeAsync("limA");
        var b = await CubeAsync("limB");
        string asm = await NewAssemblyAsync("asm_limit");
        string na = (await InsertAsync(asm, a.Path, [-30, 0, 0])).Component.Name;
        string nb = (await InsertAsync(asm, b.Path, [60, 5, 0])).Component.Name;
        // A's +x face (x = -20) and B's -x face: a limit distance between 10 and 40 mm, starting at 20.
        var lim = await MateQ(new() { Doc = asm, Type = "distance", A = Face(na, 10, 0, 0), B = Face(nb, -10, 0, 0), DistanceMm = 20, MinMm = 10, MaxMm = 40 });
        Assert.Equal("distance", lim.Mate.Type);
        Assert.Contains(lim.Verified, c => c.StartsWith("distance 20 mm, within the limits 10..40 mm", StringComparison.Ordinal));
        Assert.Contains(lim.Verified, c => c.StartsWith("limits 10..40 mm read back", StringComparison.Ordinal));

        // Symmetric about the assembly's right plane (x = 0): B's -x face mirrors A's +x face, so it sits at x = +20.
        var c = await CubeAsync("symC");
        string nc = (await InsertAsync(asm, c.Path, [80, 50, 0])).Component.Name;
        var sym = await MateQ(new() { Doc = asm, Type = "symmetric", A = Face(na, 10, 0, 0), B = Face(nc, -10, 0, 0), Plane = new EntityRef { Name = "right" } });
        Assert.Equal("symmetric", sym.Mate.Type);
        Assert.Contains(sym.Verified, x => x.Contains("mirror images in the symmetry plane", StringComparison.Ordinal));
        Assert.Equal(30, sym.Components.Single(x => x.Name == nc).OriginMm![0], P);
    }

    [Fact]
    public async Task Width_centres_a_cube_between_the_box_ends_and_hinge_seats_a_shaft_in_the_bore()
    {
        var box = await BoxAsync("wbox");   // x 0..40
        var cube = await CubeAsync("wcube"); // x -10..10 about its origin
        string asm = await NewAssemblyAsync("asm_width");
        string nbx = (await InsertAsync(asm, box.Path, [0, 0, 0])).Component.Name;
        string ncu = (await InsertAsync(asm, cube.Path, [100, 30, 50])).Component.Name;
        var w = await MateQ(new()
        {
            Doc = asm, Type = "width", A = Face(nbx, 0, 5, 10), B = Face(nbx, 40, 5, 10), C = Face(ncu, -10, 0, 0), D = Face(ncu, 10, 0, 0),
        });
        Assert.Equal("width", w.Mate.Type);
        Assert.Contains("the tab is centred between the width faces", w.Verified);
        Assert.Equal(20, w.Components.Single(x => x.Name == ncu).OriginMm![0], P);

        var plate = await PlateAsync("hplate"); // bore r 5 along y, top face y = 10
        var shaft = await ShaftAsync("hshaft"); // r 5 along +y from its origin
        string asm2 = await NewAssemblyAsync("asm_hinge");
        string np = (await InsertAsync(asm2, plate.Path, [0, 0, 0])).Component.Name;
        string ns = (await InsertAsync(asm2, shaft.Path, [50, 40, 20])).Component.Name;
        var h = await MateQ(new()
        {
            Doc = asm2, Type = "hinge", A = Face(np, 5, 5, 0), B = Face(ns, 5, 15, 0), C = Face(np, 15, 10, 15), D = Face(ns, 2, 0, 0),
        });
        Assert.Equal("hinge", h.Mate.Type);
        Assert.Contains("axes coincide", h.Verified);
        Assert.Contains("planes coincide (0 mm apart)", h.Verified);
        var s = h.Components.Single(x => x.Name == ns);
        Assert.Equal(0, s.OriginMm![0], P);
        Assert.Equal(0, s.OriginMm[2], P);
    }

    [Fact]
    public async Task Gear_and_rack_mates_read_their_numbers_back_and_screws_are_refused()
    {
        var plate = await PlateAsync("gplate");
        var shaft = await ShaftAsync("gshaft");
        var box = await BoxAsync("grack");
        string asm = await NewAssemblyAsync("asm_mech");
        string np = (await InsertAsync(asm, plate.Path, [0, 0, 0])).Component.Name;
        string s1 = (await InsertAsync(asm, shaft.Path, [60, 0, 0])).Component.Name;
        string s2 = (await InsertAsync(asm, shaft.Path, [100, 0, 0])).Component.Name;
        string rk = (await InsertAsync(asm, box.Path, [0, 0, 80])).Component.Name;

        var gear = await MateQ(new() { Doc = asm, Type = "gear", A = Face(s1, 5, 15, 0), B = Face(s2, 5, 15, 0), Ratio = [20, 40] });
        Assert.Equal("gear", gear.Mate.Type);
        Assert.Contains(gear.Verified, c => c.StartsWith("gear ratio", StringComparison.Ordinal));

        // Rack and pinion: the box's long edge (along x at y = z = 0) is the rack, the second shaft the pinion.
        var rack = await MateQ(new()
        {
            Doc = asm, Type = "rack_pinion", A = new EntityRef { Component = rk, Type = "edge", At = [20, 0, 0] }, B = Face(s2, 5, 15, 0), PitchMm = 31.4,
        });
        Assert.Equal("rack_pinion", rack.Mate.Type);
        Assert.Contains(rack.Verified, c => c.StartsWith("rack travel per revolution 31.4 mm read back", StringComparison.Ordinal));

        // Screw mates are refused: SOLIDWORKS ignores their pitch through the API.
        Assert.Equal(ErrorCodes.NotSupported, (await Assert.ThrowsAsync<SwException>(() =>
            MateQ(new() { Doc = asm, Type = "screw", A = Face(np, 5, 5, 0), B = Face(s1, 5, 15, 0), PitchMm = 1.5 }))).Code);
        Assert.Equal(2, (await Asm.MateListAsync(asm, 100, 0)).Total);
    }

    // ------------------------------------------------------------------ asm_explode

    [Fact]
    public async Task Exploded_view_moves_each_component_by_the_sum_of_its_steps()
    {
        var plate = await PlateAsync("xplate");
        var shaft = await ShaftAsync("xshaft");
        var cube = await CubeAsync("xcube");
        string asm = await NewAssemblyAsync("asm_explode");
        await InsertAsync(asm, plate.Path, [0, 0, 0]);
        string ns = (await InsertAsync(asm, shaft.Path, [0, 10, 0])).Component.Name;
        string nc = (await InsertAsync(asm, cube.Path, [60, 10, 0])).Component.Name;
        var r = await Asm.ExplodeAsync(new AsmExplodeRequest
        {
            Doc = asm,
            Steps =
            [
                new AsmExplodeStep { Components = [ns], Direction = "+y", DistanceMm = 60 },
                new AsmExplodeStep { Components = [ns, nc], Direction = "-x", DistanceMm = 40 },
            ],
        });
        Assert.Equal(2, r.StepNames.Count);
        Assert.True(r.Exploded);
        AssertPoint([-40, 60, 0], r.Components.Single(x => x.Name == ns).OffsetMm);
        AssertPoint([-40, 0, 0], r.Components.Single(x => x.Name == nc).OffsetMm);
        AssertPoint([0, 10, 0], r.Components.Single(x => x.Name == ns).CollapsedOriginMm);
        Assert.Contains(r.Verified, c => c.Contains("every component moved by the sum of its steps", StringComparison.Ordinal));

        // A second view, shown collapsed at the end: the components are back at their places.
        var r2 = await Asm.ExplodeAsync(new AsmExplodeRequest
        {
            Doc = asm, Steps = [new AsmExplodeStep { Components = [nc], Direction = "+z", DistanceMm = 25 }], Collapse = true,
        });
        Assert.NotEqual(r.View, r2.View);
        Assert.False(r2.Exploded);
        AssertPoint([0, 0, 25], r2.Components.Single().OffsetMm);
        AssertPoint([60, 10, 0], (await ComponentAsync(asm, nc)).OriginMm);
    }

    [Fact]
    public async Task Explode_steps_that_cant_work_are_refused()
    {
        var cube = await CubeAsync("xbad");
        string asm = await NewAssemblyAsync("asm_explode_bad");
        string nc = (await InsertAsync(asm, cube.Path, [0, 0, 0])).Component.Name;
        async Task<string> Refused(AsmExplodeStep[] steps) =>
            (await Assert.ThrowsAsync<SwException>(() => Asm.ExplodeAsync(new AsmExplodeRequest { Doc = asm, Steps = steps }))).Code;
        Assert.Equal(ErrorCodes.InvalidArgument, await Refused([]));
        Assert.Equal(ErrorCodes.InvalidArgument, await Refused([new AsmExplodeStep { Components = [nc], Direction = "up", DistanceMm = 10 }]));
        Assert.Equal(ErrorCodes.InvalidArgument, await Refused([new AsmExplodeStep { Components = [nc], Direction = "1,1,0", DistanceMm = 10 }]));
        Assert.Equal(ErrorCodes.InvalidArgument, await Refused([new AsmExplodeStep { Components = [nc], Direction = "+x", DistanceMm = 0 }]));
        Assert.Equal(ErrorCodes.EntityNotFound, await Refused([new AsmExplodeStep { Components = ["nope-1"], Direction = "+x", DistanceMm = 10 }]));
    }

    [Fact]
    public async Task New_mate_types_without_their_entities_or_numbers_are_refused()
    {
        var a = await CubeAsync("refA");
        var b = await CubeAsync("refB");
        string asm = await NewAssemblyAsync("asm_mref");
        string na = (await InsertAsync(asm, a.Path, [0, 0, 0])).Component.Name;
        string nb = (await InsertAsync(asm, b.Path, [50, 0, 0])).Component.Name;
        var fa = Face(na, 10, 0, 0);
        var fb = Face(nb, -10, 0, 0);
        async Task<string> Refused(AsmMateRequest q) => (await Assert.ThrowsAsync<SwException>(() => MateQ(q))).Code;
        Assert.Equal(ErrorCodes.InvalidArgument, await Refused(new() { Doc = asm, Type = "width", A = fa, B = fb }));
        Assert.Equal(ErrorCodes.InvalidArgument, await Refused(new() { Doc = asm, Type = "symmetric", A = fa, B = fb }));
        Assert.Equal(ErrorCodes.InvalidArgument, await Refused(new() { Doc = asm, Type = "gear", A = fa, B = fb }));
        Assert.Equal(ErrorCodes.InvalidArgument, await Refused(new() { Doc = asm, Type = "gear", A = fa, B = fb, Ratio = [1, 0] }));
        Assert.Equal(ErrorCodes.InvalidArgument, await Refused(new() { Doc = asm, Type = "rack_pinion", A = fa, B = fb }));
        Assert.Equal(ErrorCodes.InvalidArgument, await Refused(new() { Doc = asm, Type = "distance", A = fa, B = fb, DistanceMm = 20, MinMm = 30, MaxMm = 10 }));
        Assert.Equal(ErrorCodes.InvalidArgument, await Refused(new() { Doc = asm, Type = "distance", A = fa, B = fb, DistanceMm = 50, MinMm = 10, MaxMm = 40 }));
        Assert.Equal(ErrorCodes.InvalidArgument, await Refused(new() { Doc = asm, Type = "coincident", A = fa, B = fb, MinMm = 1, MaxMm = 2 }));
        Assert.Equal(ErrorCodes.InvalidArgument, await Refused(new() { Doc = asm, Type = "coincident", A = fa, B = fb, PitchMm = 2 }));
        Assert.Equal(ErrorCodes.NotSupported, await Refused(new() { Doc = asm, Type = "slot", A = fa, B = fb }));
        Assert.Equal(0, (await Asm.MateListAsync(asm, 100, 0)).Total);
    }
}
