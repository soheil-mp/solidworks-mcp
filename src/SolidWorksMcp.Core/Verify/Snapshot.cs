using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Geometry;
using SolidWorksMcp.Core.Model;

namespace SolidWorksMcp.Core.Verify;

/// <summary>
/// Model state before/after a tool: every feature (sub-features included) by <c>IFeature.GetID</c>, and per-body
/// geometry from <c>IBody2.GetMassProperties</c> (which, unlike document mass properties, does not read zero while a
/// sketch is open). Taken inside the same STA operation as the mutation, from the bound document.
/// </summary>
internal sealed class Snapshot
{
    public required Dictionary<int, IFeature> Features { get; init; }
    public required int Bodies { get; init; }
    public required int SolidBodies { get; init; }
    public required double Volume { get; init; } // m³
    public required double Area { get; init; }   // m²
    public required int Faces { get; init; }
    public required int Edges { get; init; }
    public double[]? Box { get; init; }          // m, over all bodies

    public static Snapshot Take(IModelDoc2 doc)
    {
        var feats = new Dictionary<int, IFeature>();
        foreach (var f in ComArgs.Objects<IFeature>((object?)doc.FeatureManager.GetFeatures(false)))
        {
            try { feats[f.GetID()] = f; } catch { /* system nodes without ids */ }
        }
        int bodies = 0, solids = 0, faces = 0, edges = 0;
        double vol = 0, area = 0;
        double[]? box = null;
        if (doc.GetType() == (int)swDocumentTypes_e.swDocPART)
        {
            foreach (var b in ((IPartDoc)doc).AllBodies(visibleOnly: false))
            {
                bodies++;
                bool solid = b.GetType() == (int)swBodyType_e.swSolidBody;
                if (solid) solids++;
                var mp = ComArgs.Doubles((object?)b.GetMassProperties(1.0)); // [cx, cy, cz, volume, area, mass, ...]
                if (mp.Length >= 5)
                {
                    if (solid) vol += mp[3];
                    area += mp[4];
                }
                faces += b.GetFaceCount();
                edges += b.GetEdgeCount();
                var bb = b.Box();
                if (bb.Length == 6)
                    box = box is null ? bb : [Math.Min(box[0], bb[0]), Math.Min(box[1], bb[1]), Math.Min(box[2], bb[2]),
                        Math.Max(box[3], bb[3]), Math.Max(box[4], bb[4]), Math.Max(box[5], bb[5])];
            }
        }
        return new Snapshot { Features = feats, Bodies = bodies, SolidBodies = solids, Volume = vol, Area = area, Faces = faces, Edges = edges, Box = box };
    }

    public ModelState ToState() => new() { VolumeMm3 = Units.Mm3(Volume), Bodies = Bodies, Box = BoxMm.FromMeters(Box) };
}
