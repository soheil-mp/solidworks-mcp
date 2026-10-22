namespace SolidWorksMcp.Server.Hosting;

/// <summary>
/// The server instructions (sent at initialize; Claude Code shows them to the model up front and cuts them at 4,096
/// characters). They carry the conventions every tool relies on, so tool descriptions can stay short.
/// </summary>
public static class Instructions
{
    public const string Text = """
        solidworks-mcp drives the user's running SOLIDWORKS: parts, sketches, features, inspection, screenshots (and,
        as they ship: assemblies, drawings, export, simulation).

        Conventions
        1. Units: mm, degrees and kg in every input and output (never meters).
        2. SOLIDWORKS is Y-up: Top Plane = XZ (normal +Y), Front = XY (+Z), Right = YZ (+X). "Top" means +Y; put flat
           parts on the top plane. Planes are named {"name":"top"}, "front" or "right" (any UI language).
        3. Points: [x,y,z] = model mm. Sketch tools also take [x,y] sketch-local mm; on the top plane sketch (x,y) is
           model (x,0,-y), so model points [x,y,z] are often easier (they are projected onto the sketch plane).
        4. Geometry references: {"id":"F3"} from face_list / edge_list or a feature result (newFaces);
           {"at":[x,y,z]} = the nearest face/edge/vertex; {"name":"Sketch1"|"Fillet1"|"top"}; list parameters also
           take queries, e.g. edges:[{"where":{"curve":"line","parallelTo":"y"},"expect":4}].
           No selection state carries over between calls.
        5. Documents: tools act on the session's target document (set by doc_new or doc_activate) or on 'doc'.
           You may change only documents you created. To change a document the user already had open, ask the
           user, then call doc_activate {doc, allowEdit:true} (a backup copy is made). Never close, discard or
           overwrite the user's files unless they asked.

        Typical loop
        doc_new → sketch_start {on:{name:"top"}} → sketch_add {entities:[…]} → sketch_finish →
        feature_extrude {depthMm} → feature_fillet … → view_screenshot to check → doc_save {mode:"as", path}.
        Read each result: it reports the verified volume change, the created feature and its new faces (with ids),
        so extra listing calls are rarely needed.

        Errors read "CODE: message Hint: …".
        - A failed feature is rolled back automatically: change the input instead of repeating the call.
        - ENTITY_AMBIGUOUS / ENTITY_NOT_FOUND list candidates with pick points.
        - TIMEOUT: the operation may still complete. Check sw_status (recentOps) before retrying anything.
        - DIALOG_BLOCKING: a SOLIDWORKS dialog needs an answer. Tell the user what it says (dialog_list) and ask;
          use dialog_respond only for a dialog your own call caused.
        - USER_BUSY: the user is in a SOLIDWORKS command; ask them to finish it. NOT_OWNED: see rule 5.
        vba_run (only when the user enabled it) is a last resort; inside VBA the API uses meters and radians
        (helpers MM() and DEG()).
        """;
}
