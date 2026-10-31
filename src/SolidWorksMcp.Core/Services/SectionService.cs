using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Geometry;
using SolidWorksMcp.Core.Model;

namespace SolidWorksMcp.Core.Services;

/// <summary>A principal axis of a section: its direction, second moment, section modulus and radius of gyration.</summary>
public sealed class SectionAxis
{
    /// <summary>Unit direction of the axis through the centroid (model space).</summary>
    public required double[] Direction { get; init; }
    /// <summary>Second moment of area about it, mm⁴.</summary>
    public double InertiaMm4 { get; init; }
    /// <summary>The farthest point of the section from it, mm.</summary>
    public double FarthestMm { get; init; }
    /// <summary>Section modulus I / farthest, mm³.</summary>
    public double ModulusMm3 { get; init; }
    /// <summary>√(I / A), mm.</summary>
    public double RadiusOfGyrationMm { get; init; }
}

public sealed class SectionResult : ToolResult
{
    public int Faces { get; init; }
    public double AreaMm2 { get; init; }
    public required double[] CentroidMm { get; init; }
    /// <summary>The section plane's unit normal.</summary>
    public required double[] Normal { get; init; }
    /// <summary>The principal axes through the centroid, the larger moment first.</summary>
    public required IReadOnlyList<SectionAxis> Principal { get; init; }
    /// <summary>Polar moment about the centroid (the two principal moments together), mm⁴.</summary>
    public double PolarMm4 { get; init; }
    public required IReadOnlyList<string> Checks { get; init; }
    public IReadOnlyList<ResolvedInfo>? Resolved { get; init; }
}

/// <summary>
/// model_section: the section properties of flat faces (a member's end face is its cross-section) through
/// IModelDocExtension.GetSectionProperties2 (metric; [status, area, centroid, …, polar, angle, principal moments, axes]).
/// Section moduli are I over the farthest boundary point from each principal axis (the face's tessellation corners).
/// Tier B: the area equals the faces' own, J = I1 + I2, and the principal axes are orthonormal and lie in the faces' plane.
/// </summary>
public sealed class SectionService(SwSession session)
{
    public Task<SectionResult> SectionAsync(string? doc, IReadOnlyList<EntityRef>? faces, CancellationToken ct = default)
    {
        if (faces is not { Count: > 0 })
            throw SwException.InvalidArgument("'faces' is required: flat faces in one plane, e.g. a member's end face.",
                "e.g. faces:[{at:[x,y,z]}] on the end of a beam, or the faces of a cut (face_list shows ids).");
        return session.RunAsync("model_section", ctx =>
        {
            var d = ctx.DocOfType(swDocumentTypes_e.swDocPART, swDocumentTypes_e.swDocASSEMBLY);
            var resolved = EntityResolver.ResolveAll(ctx, d, faces, "faces", EntityKind.Face);
            var list = resolved.Select(r => (IFace2)r.Obj).ToList();
            Vec3? normal = null, origin = null;
            foreach (var (f, r) in list.Zip(resolved))
            {
                var g = Topology.Describe(f);
                if (g.Surface != "plane" || g.Normal is not Vec3 fn)
                    throw SwException.InvalidArgument($"{r.Label} is a {g.Surface}; section properties are for flat faces.");
                var p = Topology.InteriorPoint(f);
                // One plane: parallel faces at another level gave moments that don't add up (VERIFIED), so they are refused.
                if (normal is Vec3 n0 && (n0.Cross(fn).Length > 1e-6 || Math.Abs((p - origin!.Value).Dot(n0)) > 1e-6))
                    throw SwException.InvalidArgument($"{r.Label} is not in the plane of the other faces; a section lies in one plane.",
                        "Pass the faces of one cut, e.g. both flanges' ends at the same level.");
                normal ??= fn;
                origin ??= p;
            }
            double[] raw = [];
            var attempts = new List<string>();
            foreach (var (how, arr) in new (string, Func<object>)[]
                     {
                         ("object array", () => list.Cast<object>().ToArray()),
                         ("IDispatch array", () => ComArgs.DispatchArray(list)),
                     })
            {
                d.ClearSelection2(true);
                try
                {
                    raw = ComArgs.Doubles((object?)d.Extension.GetSectionProperties2(arr()));
                }
                catch (COMException e) when (!ComArgs.IsDisconnect(e))
                {
                    attempts.Add($"{how}: {e.Message}");
                    continue;
                }
                if (raw.Length >= 24 && raw[0] == 0) break;
                attempts.Add($"{how}: status {(raw.Length > 0 ? raw[0].ToString(System.Globalization.CultureInfo.InvariantCulture) : "none")}");
            }
            d.ClearSelection2(true);
            if (raw.Length < 24 || raw[0] != 0)
                throw new SwException(ErrorCodes.FeatureFailed, $"SOLIDWORKS computed no section properties ({string.Join("; ", attempts)}).",
                    "Pass flat faces of solid bodies, all in one plane (status 2 means they aren't).");

            double area = raw[1];
            var c = new Vec3(raw[2], raw[3], raw[4]);
            double polar = raw[11];
            double[] moments = [raw[13], raw[14]];
            Vec3[] axes = [new(raw[15], raw[16], raw[17]), new(raw[18], raw[19], raw[20])];
            var checks = new List<string>();
            double own = list.Sum(f => f.GetArea());
            if (Math.Abs(area - own) > 1e-6 * own)
                throw new SwException(ErrorCodes.VerifyFailed, $"The section's area {Units.Mm2(area)} mm² is not the faces' {Units.Mm2(own)} mm².");
            checks.Add($"area {Units.Mm2(area)} mm², the faces' own");
            if (moments.Any(m => !(m > 0)) || Math.Abs(polar - moments.Sum()) > 1e-6 * polar)
                throw new SwException(ErrorCodes.VerifyFailed,
                    $"The moments don't add up: principal {Units.R(moments[0] * 1e12)} and {Units.R(moments[1] * 1e12)} mm⁴, polar {Units.R(polar * 1e12)} mm⁴.");
            checks.Add("polar moment = the sum of the principal ones (about the centroid)");
            var n = normal!.Value;
            if (Math.Abs(axes[0].Length - 1) > 1e-6 || Math.Abs(axes[1].Length - 1) > 1e-6 || Math.Abs(axes[0].Dot(axes[1])) > 1e-6 ||
                Math.Abs(axes[0].Dot(n)) > 1e-6 || Math.Abs(axes[1].Dot(n)) > 1e-6)
                throw new SwException(ErrorCodes.VerifyFailed, "The principal axes are not two perpendicular unit vectors in the section's plane.");
            checks.Add("principal axes perpendicular, in the plane");

            // The section's corners (tessellation vertices lie on the boundary), for the farthest fibre from each axis.
            var points = list.SelectMany(f => Corners(f)).ToList();
            var principal = new List<SectionAxis>();
            for (int i = 0; i < 2; i++)
            {
                var across = axes[1 - i]; // the distance from axis i is measured along the other axis
                double far = points.Count == 0 ? 0 : points.Max(p => Math.Abs((p - c).Dot(across)));
                principal.Add(new SectionAxis
                {
                    Direction = Units.Unit(axes[i]),
                    InertiaMm4 = Math.Round(moments[i] * 1e12, 3),
                    FarthestMm = Units.Mm(far),
                    ModulusMm3 = far > 0 ? Math.Round(moments[i] / far * 1e9, 3) : 0,
                    RadiusOfGyrationMm = Units.Mm(Math.Sqrt(moments[i] / area)),
                });
            }
            principal = principal.OrderByDescending(a => a.InertiaMm4).ToList();
            return new SectionResult
            {
                Doc = ctx.Title(d),
                Faces = list.Count,
                AreaMm2 = Units.Mm2(area),
                CentroidMm = Units.Mm(c),
                Normal = Units.Unit(n),
                Principal = principal,
                PolarMm4 = Math.Round(polar * 1e12, 3),
                Checks = checks,
                Resolved = resolved.Select(r => r.Info("faces")).ToList(),
            };
        }, RunOptions.Read(doc), ct);
    }

    /// <summary>The vertices of a face's tessellation (they include its boundary's corners and points along its curves).</summary>
    private static IEnumerable<Vec3> Corners(IFace2 face)
    {
        var tess = ComArgs.Doubles((object?)face.GetTessTriangles(true));
        for (int i = 0; i + 2 < tess.Length; i += 3) yield return Vec3.From(tess, i);
    }
}
