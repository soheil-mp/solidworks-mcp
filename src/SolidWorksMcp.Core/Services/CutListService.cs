using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Geometry;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Verify;

namespace SolidWorksMcp.Core.Services;

/// <summary>One cut-list item: identical bodies (a weldment member length, a sheet-metal blank) and their properties.</summary>
public sealed class CutListItemInfo
{
    public required string Name { get; init; }
    /// <summary>How many bodies the item holds.</summary>
    public int Quantity { get; init; }
    /// <summary>weldment | sheet_metal | solid.</summary>
    public required string Type { get; init; }
    /// <summary>The item's cut-list properties as SOLIDWORKS resolves them (LENGTH, ANGLE1, Description, Bounding Box Length, …).</summary>
    public required IReadOnlyDictionary<string, string> Properties { get; init; }
    public required IReadOnlyList<string> Bodies { get; init; }
}

public sealed class CutListResult : ToolResult
{
    public required IReadOnlyList<CutListItemInfo> Items { get; init; }
    /// <summary>Bodies the items hold, together.</summary>
    public int Bodies { get; init; }
    /// <summary>Whether SOLIDWORKS keeps the cut list up to date by itself.</summary>
    public bool? AutomaticUpdate { get; init; }
    public bool? Updated { get; init; }
    public required IReadOnlyList<string> Checks { get; init; }
}

/// <summary>
/// model_cut_list: the cut list of a weldment or sheet-metal part (its CutListFolder features: IBodyFolder for the bodies,
/// IFeature.CustomPropertyManager for the properties). Verified: the items hold every solid body of the part once.
/// </summary>
public sealed class CutListService(SwSession session)
{
    public Task<CutListResult> CutListAsync(string? doc, bool update, CancellationToken ct = default) =>
        session.RunAsync("model_cut_list", ctx =>
        {
            var part = ctx.Part();
            var d = (IModelDoc2)part;
            var root = Tree.Walk(d).FirstOrDefault(f => f.GetTypeName2() == "SolidBodyFolder")?.GetSpecificFeature2() as IBodyFolder;
            bool? automatic = root is null ? null : Try(() => root.GetAutomaticUpdate());
            bool updated = false;
            if (update)
            {
                if (root is null) throw SwException.InvalidArgument($"'{ctx.Title(d)}' has no cut list.");
                updated = root.UpdateCutList();
                if (!updated) ctx.Warn("UpdateCutList returned false.");
                d.EditRebuild3();
            }
            var items = new List<CutListItemInfo>();
            foreach (var f in Tree.Walk(d).Where(f => f.GetTypeName2() == "CutListFolder"))
            {
                if (f.GetSpecificFeature2() is not IBodyFolder bf) continue;
                int count = Try(() => bf.GetBodyCount()) ?? 0;
                if (count == 0) continue;
                var bodies = ComArgs.Objects<IBody2>((object?)bf.GetBodies()).Select(b => b.Name ?? "").ToList();
                items.Add(new CutListItemInfo
                {
                    Name = f.Name,
                    Quantity = count,
                    Type = (Try(() => bf.GetCutListType()) ?? 0) switch
                    {
                        (int)swCutListType_e.swWeldmentCutlist => "weldment",
                        (int)swCutListType_e.swSheetmetalCutlist => "sheet_metal",
                        _ => "solid",
                    },
                    Properties = Props(f),
                    Bodies = bodies.Take(20).ToList(),
                });
            }
            if (items.Count == 0)
                throw SwException.InvalidArgument($"'{ctx.Title(d)}' has no cut-list items.",
                    "Cut lists come with weldments (weldment_member) and sheet metal; for others, try update:true.");
            int listed = items.Sum(i => i.Quantity);
            int solids = Snapshot.Take(d).SolidBodies;
            var checks = new List<string> { $"{items.Count} item(s) holding {listed} bod(ies)" };
            if (listed == solids) checks.Add($"every solid body of the part ({solids}) is in the cut list once");
            else ctx.Warn($"The cut list holds {listed} bodies but the part has {solids} solid bodies: it may be out of date (pass update:true).");
            if (automatic == false && !update) ctx.Warn("The cut list does not update automatically; pass update:true to refresh it first.");
            return new CutListResult
            {
                Doc = ctx.Title(d),
                Items = items,
                Bodies = listed,
                AutomaticUpdate = automatic,
                Updated = update ? updated : null,
                Checks = checks,
            };
        }, update ? RunOptions.Write(doc) : RunOptions.Read(doc), ct);

    /// <summary>The folder's custom properties with their evaluated values (GetAll3).</summary>
    private static Dictionary<string, string> Props(IFeature f)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (f.CustomPropertyManager is not ICustomPropertyManager mgr) return map;
            object names = null!, types = null!, values = null!, resolved = null!, links = null!;
            mgr.GetAll3(ref names, ref types, ref values, ref resolved, ref links);
            // PropValues are the evaluated values; 'Resolved' holds swCustomInfoGetResult_e statuses (VERIFIED: all 2).
            var n = ComArgs.Strings((object?)names);
            var v = ComArgs.Strings((object?)values);
            for (int i = 0; i < n.Length; i++) map[n[i]] = i < v.Length ? v[i] ?? "" : "";
        }
        catch (COMException e) when (!ComArgs.IsDisconnect(e))
        {
        }
        return map;
    }

    private static T? Try<T>(Func<T> read) where T : struct
    {
        try { return read(); }
        catch (COMException e) when (!ComArgs.IsDisconnect(e)) { return null; }
    }
}
