using SolidWorks.Interop.sldworks;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Documents;

namespace SolidWorksMcp.Core.Geometry;

/// <summary>
/// Short handles ("F3", "E12", "V5", "B2") for topological entities, backed by SolidWorks persistent references
/// (<c>IModelDocExtension.GetPersistReference3</c>). Ids are unique for the server session and never reused; each is
/// bound to its document. On resolution the persistent-reference status is mapped (deleted/invalid → ENTITY_STALE,
/// suppressed → its own hint) and a stored signature (kind, surface/curve type, pick point) is re-checked, because a
/// persistent id can silently resolve to a surviving fragment of a split face. STA thread only.
/// </summary>
internal sealed class EntityRegistry
{
    private readonly Dictionary<string, Record> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<IModelDoc2, Dictionary<string, string>> _byDoc = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<char, int> _counters = [];

    /// <param name="LocalPick">A point on the entity in the entity's own coordinates (part space), for re-validation.</param>
    internal sealed record Record(string Id, IModelDoc2 Doc, string DocTitle, byte[] Persist, string Kind, string? SubKind, Vec3? LocalPick);

    /// <summary>Returns the id for <paramref name="entity"/>, allocating one if new; refreshes its signature.</summary>
    public string IdFor(IModelDoc2 doc, object entity, char prefix, string kind, string? subKind = null, Vec3? localPick = null)
    {
        byte[] persist = ComArgs.Bytes((object?)doc.Extension.GetPersistReference3(entity))
            ?? throw new SwException(ErrorCodes.Internal, $"No persistent reference for this {kind}.");
        string key = Convert.ToBase64String(persist);
        if (!_byDoc.TryGetValue(doc, out var keys)) _byDoc[doc] = keys = new Dictionary<string, string>(StringComparer.Ordinal);
        if (keys.TryGetValue(key, out var existing))
        {
            if (localPick is not null || subKind is not null)
            {
                var old = _byId[existing];
                _byId[existing] = old with { SubKind = subKind ?? old.SubKind, LocalPick = localPick ?? old.LocalPick };
            }
            return existing;
        }
        int n = _counters.TryGetValue(prefix, out int c) ? c + 1 : 1;
        _counters[prefix] = n;
        string id = $"{prefix}{n}";
        _byId[id] = new Record(id, doc, DocumentRegistry.SafeTitle(doc), persist, kind, subKind, localPick);
        keys[key] = id;
        return id;
    }

    public bool TryGet(string id, out Record record) => _byId.TryGetValue(id.Trim(), out record!);

    /// <summary>Resolves an id to its live entity in <paramref name="doc"/>; throws ENTITY_NOT_FOUND / ENTITY_STALE.</summary>
    public object Resolve(IModelDoc2 doc, string id)
    {
        if (!TryGet(id, out var rec))
            throw new SwException(ErrorCodes.EntityNotFound, $"Unknown entity id '{id}'.",
                "Ids come from face_list / edge_list / vertex_list / body_list or feature results in this server session; list again to get current ids.");
        if (!ReferenceEquals(rec.Doc, doc) && !DocLocator.SameDoc(rec.Doc, doc))
            throw new SwException(ErrorCodes.EntityNotFound,
                $"Entity '{id}' belongs to document '{rec.DocTitle}', not '{DocumentRegistry.SafeTitle(doc)}'.",
                "Work in that document (doc_activate or the 'doc' parameter), or list entities of this one.");
        object? obj = (object?)doc.Extension.GetObjectByPersistReference3(rec.Persist, out int status);
        string relist = rec.Kind switch { "face" => "face_list", "edge" => "edge_list", "vertex" => "vertex_list", "body" => "body_list", _ => "the listing tool" };
        switch (status)
        {
            case 2: // swPersistReferencedObject_Suppressed
                throw new SwException(ErrorCodes.EntityStale, $"Entity '{id}' ({rec.Kind}) belongs to a feature that is suppressed in the active configuration.",
                    "Unsuppress the feature or switch configuration, or pick another entity.");
            case 1 or 4: // _Invalid / _Deleted
                throw new SwException(ErrorCodes.EntityStale, $"Entity '{id}' ({rec.Kind}) no longer exists: the model changed (deleted, merged or split).",
                    $"Re-run {relist} for current ids, or reference it by point.");
        }
        if (obj is null)
            throw new SwException(ErrorCodes.EntityStale, $"Entity '{id}' ({rec.Kind}) no longer resolves.", $"Re-run {relist} for current ids.");
        Validate(rec, obj, relist);
        return obj;
    }

    /// <summary>The signature check: same kind and sub-kind, and the stored point still lies on the entity.</summary>
    private static void Validate(Record rec, object obj, string relist)
    {
        string? sub = obj switch
        {
            IFace2 f => Topology.SurfaceKind(f.Surface()),
            IEdge e => Topology.CurveKind(e.Curve()),
            _ => rec.SubKind,
        };
        bool kindOk = rec.Kind switch
        {
            "face" => obj is IFace2,
            "edge" => obj is IEdge,
            "vertex" => obj is IVertex,
            "body" => obj is IBody2,
            _ => true,
        };
        if (!kindOk || (rec.SubKind is not null && sub is not null && sub != rec.SubKind))
            throw new SwException(ErrorCodes.EntityStale, $"Entity '{rec.Id}' changed type (now {sub}); the topology changed.", $"Re-run {relist}.");
        if (rec.LocalPick is Vec3 p)
        {
            double d = obj switch
            {
                IFace2 f => Topology.DistanceToFace(f, p),
                IEdge e => Topology.DistanceToEdge(e, p),
                IVertex v => Vec3.From(v.Point()).DistanceTo(p),
                _ => 0,
            };
            if (d > 1e-6)
                throw new SwException(ErrorCodes.EntityStale,
                    $"Entity '{rec.Id}' ({rec.Kind}) moved or was split ({Units.Mm(d)} mm from where it was listed).",
                    $"Re-run {relist} for current ids, or reference it by point.");
        }
    }

    /// <summary>Forgets everything (the SOLIDWORKS instance went away). Counters keep running so ids are never reused.</summary>
    public void Clear()
    {
        _byId.Clear();
        _byDoc.Clear();
    }

    /// <summary>Forgets ids of a closed document.</summary>
    public void Forget(IModelDoc2 doc)
    {
        foreach (var r in _byId.Values.Where(r => ReferenceEquals(r.Doc, doc)).ToList())
            _byId.Remove(r.Id);
        _byDoc.Remove(doc);
    }
}
