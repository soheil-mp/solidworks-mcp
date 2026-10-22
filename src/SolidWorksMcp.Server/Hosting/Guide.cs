using System.Reflection;
using System.Text;
using ModelContextProtocol.Server;
using SolidWorksMcp.Core;

namespace SolidWorksMcp.Server.Hosting;

/// <summary>sw_help output: one guide topic (or the index) as markdown.</summary>
public sealed class HelpResult
{
    public required string Topic { get; init; }
    public required string Text { get; init; }
    /// <summary>The index only: the topics and what each explains.</summary>
    public IReadOnlyList<HelpTopic>? Topics { get; init; }
}

public sealed record HelpTopic(string Name, string About);

/// <summary>A tool as the guides see it: its MCP name, toolset, title and parameter names (from reflection).</summary>
public sealed record ToolDoc(string Name, string Toolset, string Title, bool ReadOnly, IReadOnlyList<(string Name, bool Optional)> Parameters);

/// <summary>
/// The agent guides served by sw_help. They use the server's real tool and parameter names (a contract test parses
/// every <c>tool {param: …}</c> example against the catalog). The "tools" topic is generated from the catalog itself.
/// </summary>
public static class Guide
{
    public static readonly IReadOnlyList<HelpTopic> Topics =
    [
        new("conventions", "units, axes and planes, points, documents and access, files, results"),
        new("entityref", "how to reference faces, edges, vertices, planes, sketches and features"),
        new("workflow_plate", "a plate with holes and rounded corners, step by step"),
        new("workflow_enclosure", "an open box with 2 mm walls, rounded corners and a cable hole"),
        new("editing", "changing an existing part, a user's document or a STEP file"),
        new("recovery", "every error code and what to do next"),
        new("tools", "every tool of this server with its parameters"),
    ];

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["convention"] = "conventions", ["units"] = "conventions",
        ["entity"] = "entityref", ["entity_ref"] = "entityref", ["entityrefs"] = "entityref", ["references"] = "entityref", ["refs"] = "entityref",
        ["plate"] = "workflow_plate", ["enclosure"] = "workflow_enclosure", ["box"] = "workflow_enclosure",
        ["edit"] = "editing", ["errors"] = "recovery", ["error"] = "recovery", ["tool"] = "tools", ["index"] = "",
    };

    /// <summary>The topic text, or the index for null/empty. Unknown topics are INVALID_ARGUMENT.</summary>
    public static HelpResult Get(string? topic, string? revision = null)
    {
        string t = (topic ?? "").Trim().Replace('-', '_');
        if (Aliases.TryGetValue(t, out var alias)) t = alias;
        t = t.ToLowerInvariant();
        if (t.Length == 0) return new HelpResult { Topic = "index", Text = Index(revision), Topics = Topics };
        string text = t switch
        {
            "conventions" => Conventions,
            "entityref" => EntityRef,
            "workflow_plate" => WorkflowPlate,
            "workflow_enclosure" => WorkflowEnclosure,
            "editing" => Editing,
            "recovery" => Recovery,
            "tools" => ToolsText(),
            _ => throw SwException.InvalidArgument($"Unknown help topic '{topic}'.",
                $"Topics: {string.Join(", ", Topics.Select(x => x.Name))} (or omit 'topic' for the index)."),
        };
        return new HelpResult { Topic = t, Text = text };
    }

    private static string Index(string? revision)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# solidworks-mcp help").AppendLine();
        sb.AppendLine("Topics: sw_help {topic:\"<name>\"}");
        foreach (var t in Topics) sb.AppendLine($"- {t.Name}: {t.About}");
        sb.AppendLine();
        sb.AppendLine("Typical loop: sw_status, then doc_new (or doc_open), sketch_start, sketch_add, feature_extrude, feature_fillet, " +
                      "view_screenshot, doc_save / model_export. Read every result: it reports what was verified.");
        sb.AppendLine(revision is null
            ? "SOLIDWORKS: not attached yet (the first tool call that needs it attaches)."
            : $"SOLIDWORKS: attached, revision {revision}.");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ topics

    public const string Conventions = """
        # Conventions

        **Units.** Every input and output is mm, degrees and kg (areas mm2, volumes mm3). Only code in vba_run uses the API's
        meters and radians (helpers MM() and DEG()).

        **Axes and planes.** SOLIDWORKS is Y-up.
        - Top Plane = XZ, normal +Y. Front Plane = XY, normal +Z. Right Plane = YZ, normal +X.
        - Name them {name:"top"}, {name:"front"}, {name:"right"} (any UI language).
        - "Top" means +Y. Put flat parts on the top plane: their thickness then grows along +Y.

        **Points.**
        - [x,y,z] is model mm.
        - In sketch tools, [x,y] is sketch-local mm. On the top plane, sketch (x, y) is model (x, 0, -y); on the right
          plane, sketch (x, y) is model (0, y, -x).
        - Sketch tools also take model points [x,y,z] and project them onto the sketch plane. When in doubt, use those.
        - sketch_start returns the sketch frame (origin, x axis, y axis, normal) and, on a face, faceBoundsMm.

        **Documents.**
        - Tools act on the session's target document (set by doc_new, doc_open or doc_activate) or on 'doc' (a handle such
          as D2, a title or a path). With no target, read-only tools use the active document; changes need a document you
          may edit (see access).
        - access: owned = created, opened or imported by this server (full access); adopted = a user document the user let
          you edit (doc_activate allowEdit=true, after a backup); foreign = the user's (read-only for this server).
        - Never close, discard or overwrite the user's documents or files unless they asked.

        **Files.** Relative paths go under Documents\solidworks-mcp. Writes are allowed under the user profile, %TEMP% and
        that folder. An existing file needs overwrite=true; a file this server didn't write is copied to a backup first
        (backupPath in the result). Exports report the path actually written.

        **Results.** Compact JSON: doc, the tool's data, warnings[] (read them) and dialogsHandled[].
        - Feature tools report the feature, verified {volumeDeltaMm3, material, checks}, newFaces (with ids), dimensions
          and the model's box. A failed feature is rolled back.
        - Lists take limit and offset, and return total and truncated.

        **Long calls** send progress. A TIMEOUT means the call may still finish: check sw_status (recentOps) first.
        """;

    public const string EntityRef = """
        # Referencing geometry

        Tools take geometry as reference objects. No selection state carries over between calls.

        | Form | Example | Notes |
        |---|---|---|
        | id | {id:"F3"} | From face_list, edge_list, vertex_list or a result's newFaces (body ids from model_info). Re-checked against the stored geometry: ENTITY_STALE when it changed. |
        | point | {at:[50,20,0]} | The face, edge or vertex nearest that model point (mm); the parameter says which type. Pick points from listings (pickMm, edge mid points) always resolve back. |
        | typed point | {type:"edge", at:[0,20,30], tolMm:0.05} | tolMm defaults to 0.01. Beyond it the nearest entity is used with a warning; strict:true refuses instead. |
        | name | {name:"top"}, {name:"Sketch1"}, {name:"Boss-Extrude1"} | Planes (front/top/right or a plane's name), sketches, features, bodies, components. |
        | query | {where:{curve:"line", parallelTo:"y"}, expect:4} | Every match of a filter. expect guards the count (ENTITY_AMBIGUOUS otherwise). For list parameters such as edges and faces. |

        Filter fields (the 'where' of face_list, edge_list, vertex_list and of references):
        - type: face | edge | vertex
        - surface (faces): plane | cylinder | cone | sphere | torus | bspline
        - curve (edges): line | circle | ellipse | bspline
        - normal (planar faces): "+y", "-z" or "x,y,z"; parallelTo (lines, axes): "x", "y", "z" or "x,y,z"; angleTolDeg (default 1)
        - radiusMm (with radiusTolMm), lengthMm
        - near: [x,y,z] with withinMm (default 1)
        - extreme: "+y" = the highest ones, "-x" = the lowest ones
        - feature: made by that feature (an edge matches when either of its faces does), e.g. "Cut-Extrude1"
        - body, component, ofFace: "F2" (with loop: outer | inner)

        Examples
        - Upward planar faces: face_list {where:{surface:"plane", normal:"+y"}}; only the highest: add extreme:"+y".
        - The 4 vertical edges of a block: edges:[{where:{curve:"line", parallelTo:"y"}, expect:4}].
        - The edges of all Ø6 holes: edge_list {where:{curve:"circle", radiusMm:3}}.
        - The outer boundary of face F2: edge_list {where:{ofFace:"F2", loop:"outer"}}.
        - A point on a corner touches three faces and is ambiguous: use a face's pickMm instead.
        - Assemblies: {type:"face", component:"bracket-1", at:[x,y,z]} with 'at' in that part's own coordinates.
        """;

    public const string WorkflowPlate = """
        # Workflow: a plate with holes and rounded corners

        A 120 x 80 x 8 mm plate on the top plane with four Ø6.6 holes and R5 corners (7 calls).

        1. doc_new {name:"plate"}
           A new MMGS part owned by this server; it becomes the target.
        2. sketch_start {on:{name:"top"}}
        3. sketch_add {entities:[{kind:"rectangle", center:[0,0], widthMm:120, heightMm:80},
             {kind:"circle", center:[-50,-30], diameterMm:6.6}, {kind:"circle", center:[50,-30], diameterMm:6.6},
             {kind:"circle", center:[-50,30], diameterMm:6.6}, {kind:"circle", center:[50,30], diameterMm:6.6}]}
           The contour status must report closed loops. Circles inside the rectangle become through holes.
        4. feature_extrude {depthMm:8}
           It uses the open sketch and closes it. Top-plane extrudes go toward +Y, so the plate spans y = 0..8.
           verified.volumeDeltaMm3 = 120*80*8 - 4*pi*3.3^2*8 = 75705.2.
        5. feature_fillet {radiusMm:5, edges:[{where:{curve:"line", parallelTo:"y"}, expect:4}]}
           The four vertical corner edges (hole edges are circles, so the query finds exactly four).
        6. view_screenshot {view:"iso"}
        7. doc_save {mode:"as", path:"plate.sldprt"}, and if wanted model_export {path:"plate.step"}

        Variations
        - Holes added later: sketch_start {on:{at:[0,8,0]}} (the top face), sketch_add circles with model points [x,8,z],
          then feature_extrude {cut:true, end:"through_all"} (a cut goes into the material by default).
        - Plate centred on the plane: feature_extrude {end:"mid_plane", depthMm:8} gives y = -4..4.
        - STL for printing: model_export {path:"plate.stl"} (binary, fine); the result reports the triangle count.
        """;

    public const string WorkflowEnclosure = """
        # Workflow: an open enclosure

        120 x 80 x 40 mm outside, 2 mm walls and floor, R6 outer corners, an Ø8 cable hole in the +X wall (15 calls).
        It uses a pocket cut, so every step is a verified extrude or fillet.

        1. doc_new {name:"enclosure"}
        2. sketch_start {on:{name:"top"}}
        3. sketch_add {entities:[{kind:"rectangle", center:[0,0], widthMm:120, heightMm:80}]}
        4. feature_extrude {depthMm:40}
           A block from y = 0 to 40 (volume 384000).
        5. feature_fillet {radiusMm:6, edges:[{where:{curve:"line", parallelTo:"y"}, expect:4}]}
        6. sketch_start {on:{at:[0,40,0]}}
           On the top face.
        7. sketch_add {entities:[{kind:"rectangle", center:[0,40,0], widthMm:116, heightMm:76}]}
           Model points are projected onto the face; 116 x 76 leaves 2 mm walls.
        8. feature_extrude {cut:true, depthMm:38}
           The pocket: the cut goes into the material and leaves a 2 mm floor (volumeDeltaMm3 = -335008).
        9. feature_fillet {radiusMm:4, edges:[{where:{curve:"line", parallelTo:"y", feature:"Cut-Extrude1"}, expect:4}]}
           The pocket's corners: R4 = R6 - wall, so the wall stays 2 mm all round. Use the cut's name from step 8.
        10. sketch_start {on:{at:[60,20,0]}}
            On the flat part of the +X wall.
        11. sketch_add {entities:[{kind:"circle", center:[60,20,0], diameterMm:8}]}
        12. feature_extrude {cut:true, depthMm:3}
            Through that wall only (through_all would also cut the opposite wall).
        13. view_screenshot {view:"iso"}
        14. doc_save {mode:"as", path:"enclosure.sldprt"}
        15. model_export {path:"enclosure.step"}

        Check: model_info reports about 48177 mm3 (384000 - 1236.1 outer rounds - 335008 pocket + 521.9 inner rounds - 100.5 hole).
        """;

    public const string Editing = """
        # Editing an existing part

        1. Get the document.
           - A file: doc_open {path:"C:\\work\\bracket.sldprt"}. This server owns what it opens, and doc_save mode
             "inPlace" overwrites that file: prefer mode "copy" or "as" unless the user wants the original changed.
             If the file is already open in SOLIDWORKS, doc_open only makes it the target (alreadyOpen); it stays the
             user's document, as in the next point.
           - A part the user already has open: doc_list, then ask the user, then doc_activate {doc:"D2", allowEdit:true}
             (a backup copy is saved first). Without that the document stays read-only for you (NOT_OWNED).
           - STEP, IGES, Parasolid, SAT or STL: doc_open {path:"bracket.step"} imports it as a new part with MMGS units;
             the result lists the bodies and their volume. An STL usually arrives as a mesh body: no faces to build on.
        2. Understand it: model_info (bodies, volume, box, material, units), feature_list (features and rebuild errors),
           view_screenshot {view:"iso"}.
        3. Find geometry: face_list {where:{surface:"cylinder"}} (holes and bosses), face_list {where:{surface:"plane",
           normal:"+y"}}, edge_list {where:{ofFace:"F7"}}. Use the returned ids, or pickMm points as {at:[x,y,z]}.
        4. Change it.
           - Cuts or bosses on a face: sketch_start {on:{id:"F7"}}, sketch_add with model points (they are projected onto
             the face), then feature_extrude {cut:true, end:"through_all"} or feature_extrude {depthMm:5}.
           - Rounds: feature_fillet {radiusMm:2, edges:[{id:"E4"}]}.
           Each result verifies the volume change; a failed feature is rolled back automatically.
        5. Check: view_screenshot and model_info.
        6. Keep it the way the user wants: doc_save {mode:"inPlace"} (overwrites), doc_save {mode:"copy",
           path:"bracket_v2.sldprt"} (a copy; the document is unchanged) or doc_save {mode:"as", path:"bracket_v2.sldprt"}
           (documents you own). model_export {path:"bracket_v2.step"} for neutral formats.

        A change no tool covers (sw_help {topic:"tools"} lists them): tell the user. vba_run is the last resort, and only
        when the user enabled it.
        """;

    public const string Recovery = """
        # Errors and recovery

        Errors read "CODE: message Hint: ...". Follow the hint; never repeat an identical failing call.

        | Code | Meaning | What to do |
        |---|---|---|
        | INVALID_ARGUMENT | A parameter is wrong, missing or unknown. | Fix it: the message names it and the valid values. |
        | ENTITY_NOT_FOUND, ENTITY_AMBIGUOUS | No entity, or several, matched a reference. | Use a listed candidate (id or pick point), or narrow the query (expect, near, extreme, feature). |
        | ENTITY_STALE | An id no longer matches its geometry. | List again (face_list, edge_list) and use the new ids. |
        | SKETCH_STATE | A sketch is open when it must be closed, or the reverse. | sketch_finish, or sketch_start first. |
        | FEATURE_FAILED, REBUILD_ERROR, VERIFY_FAILED | Nothing was created, it has errors, or it didn't do what was asked. It was rolled back. | Change the input (depth, direction, references). keepOnFailure:true keeps the feature for diagnosis. |
        | WRONG_DOC_TYPE | The target is the wrong kind of document. | doc_list, then doc_activate the right one, or pass doc. |
        | NO_ACTIVE_DOC, DOC_CLOSED | No document, or the target was closed. | doc_new, doc_open or doc_activate. |
        | NOT_OWNED | The document belongs to the user (reading it is fine). | Ask the user; only then doc_activate {doc:"D2", allowEdit:true} (a backup is saved first). Otherwise build your own part with doc_new. |
        | DIRTY | Closing a document with unsaved changes. | doc_close {save:true}, or doc_close {discard:true} for documents you created. |
        | USER_BUSY | The user is in a SOLIDWORKS command. | Ask them to finish or cancel it, then retry. |
        | DIALOG_BLOCKING | A modal dialog blocks SOLIDWORKS. | dialog_list, tell the user what it says and ask. dialog_respond only for a dialog your own call caused. |
        | TIMEOUT | The call ran out of time; it may still complete. | sw_status: recentOps shows whether it finished. Don't retry blindly. |
        | BUSY | Another call or server holds SOLIDWORKS. | Wait a few seconds and retry. |
        | NOT_CONNECTED | SOLIDWORKS isn't running or can't be reached. | Ask the user to start it, or sw_connect {launch:true} if they want you to. |
        | IO_ERROR | A file problem, including a refused overwrite. | Choose another path, or pass overwrite:true when replacing is intended. |
        | NOT_SUPPORTED, ADDIN_UNAVAILABLE, DISABLED | Not available here (version, add-in, SWMCP_TOOLSETS, VBA off). | Tell the user what is missing; don't work around it silently. |
        | VBA_ERROR | The vba_run code failed. | Read the error text and fix the code. |
        | INTERNAL | Something unexpected. | sw_status, then check the model (model_info, feature_list) before continuing. |

        Health checks
        - sw_status: connection, documents and their access, the target, the open sketch, dialogs, the running operation
          and recentOps. It works even while SOLIDWORKS is blocked.
        - After a surprise, model_info and view_screenshot show the real state of the model.
        - Warnings in a successful result matter: a re-activated document, a preference changed by someone else, a
          nearest-entity pick instead of an exact one.
        """;

    // ------------------------------------------------------------------ the tools topic (generated)

    private static readonly HashSet<Type> HiddenParameterTypes =
        [typeof(CancellationToken), typeof(IProgress<ModelContextProtocol.ProgressNotificationValue>), typeof(McpServer), typeof(IServiceProvider)];

    /// <summary>Every tool registered in this server, from the tool classes' attributes (what the catalog is built from).</summary>
    public static IReadOnlyList<ToolDoc> Tools()
    {
        var list = new List<ToolDoc>();
        foreach (var type in typeof(Guide).Assembly.GetTypes().Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null))
        {
            string set = type.GetCustomAttribute<ToolsetAttribute>()?.Name ?? "misc";
            foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            {
                if (m.GetCustomAttribute<McpServerToolAttribute>() is not { Name: { } name } a) continue;
                var ps = m.GetParameters()
                    .Where(p => !HiddenParameterTypes.Contains(p.ParameterType) &&
                                !(p.ParameterType.IsGenericType && p.ParameterType.GetGenericTypeDefinition() == typeof(RequestContext<>)))
                    .Select(p => (p.Name ?? "", p.HasDefaultValue))
                    .ToList();
                list.Add(new ToolDoc(name, set, a.Title ?? name, a.ReadOnly, ps));
            }
        }
        return list;
    }

    private static string ToolsText()
    {
        var sets = Toolsets.Selected();
        bool readOnly = Toolsets.ReadOnlyMode;
        var tools = Tools();
        var enabled = tools.Where(t => Toolsets.IsEnabled(t.Name, sets, readOnly)).ToList();
        var sb = new StringBuilder();
        sb.AppendLine("# Tools").AppendLine();
        sb.AppendLine("Generated from this server's catalog. Parameters marked ? are optional; 'doc' defaults to the target document. " +
                      "Each tool's own description says what it verifies.");
        foreach (var g in enabled.GroupBy(t => t.Toolset).OrderBy(g => Array.IndexOf(Toolsets.All, g.Key) is var i && i < 0 ? 99 : i))
        {
            sb.AppendLine().AppendLine($"## {g.Key}");
            foreach (var t in g.OrderBy(t => t.Name, StringComparer.Ordinal))
            {
                string ps = string.Join(", ", t.Parameters.Select(p => p.Optional ? p.Name + "?" : p.Name));
                sb.AppendLine($"- {t.Name} {{{ps}}}: {t.Title}{(t.ReadOnly ? " (read-only)" : "")}");
            }
        }
        int hidden = tools.Count - enabled.Count;
        if (hidden > 0) sb.AppendLine().AppendLine($"{hidden} more tool(s) are switched off here (SWMCP_TOOLSETS / SWMCP_READ_ONLY).");
        return sb.ToString();
    }
}
