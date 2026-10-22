using System.ComponentModel;

namespace SolidWorksMcp.Core.Model;

/// <summary>
/// How a tool argument points at geometry. One identification is used, in this priority: <c>id</c> (from a listing or a
/// feature result), <c>where</c> (a query), <c>at</c> (nearest entity to a point, mm), <c>name</c> (planes, axes,
/// sketches, features, bodies, components, sketch segments). Property descriptions are kept short on purpose: the
/// schema is repeated in many tools; the server instructions explain the forms.
/// </summary>
[Description("{id:'F3'} | {at:[x,y,z]} | {name:'top'|'Sketch1'} | {where:{...},expect:n}")]
public sealed class EntityRef
{
    [Description("Id from a listing or result, e.g. F3, E12.")]
    public string? Id { get; set; }

    [Description("face|edge|vertex|plane|axis|point|sketch|sketch_segment|sketch_point|feature|body|component|origin (usually inferred).")]
    public string? Type { get; set; }

    [Description("Point on/near it, mm: [x,y,z] model (with component: that part's coords); sketch_point: [x,y].")]
    public double[]? At { get; set; }

    [Description("Name: front/top/right, a plane/axis/sketch/feature/body/component/segment name.")]
    public string? Name { get; set; }

    [Description("Query object with the same fields as face_list/edge_list 'where', e.g. {curve:'line',parallelTo:'y'}.")]
    public System.Text.Json.JsonElement? Where { get; set; }

    /// <summary>The parsed <see cref="Where"/> (strict: unknown fields are an error).</summary>
    public EntityFilter? WhereFilter(string argName)
    {
        if (Where is not System.Text.Json.JsonElement je || je.ValueKind is System.Text.Json.JsonValueKind.Null or System.Text.Json.JsonValueKind.Undefined)
            return null;
        if (je.ValueKind != System.Text.Json.JsonValueKind.Object)
            throw SwException.InvalidArgument($"'{argName}.where' must be an object, e.g. {{\"curve\":\"line\",\"parallelTo\":\"y\"}}.");
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<EntityFilter>(je, StrictJson);
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw SwException.InvalidArgument($"'{argName}.where' is invalid: {ex.Message}",
                "Fields: type, surface, curve, normal, parallelTo, radiusMm, radiusTolMm, lengthMm, near, withinMm, extreme, feature, body, ofFace, loop, component, angleTolDeg.");
        }
    }

    private static readonly System.Text.Json.JsonSerializerOptions StrictJson = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    [Description("Exact number of matches required with 'where'.")]
    public int? Expect { get; set; }

    [Description("Assembly component, e.g. bracket-1.")]
    public string? Component { get; set; }

    [Description("Owning sketch (sketch_segment/sketch_point); default: the open one.")]
    public string? Sketch { get; set; }

    [Description("Point tolerance in mm (default 0.01).")]
    public double? TolMm { get; set; }

    [Description("Reject the nearest-entity fallback beyond tolMm.")]
    public bool? Strict { get; set; }

    public override string ToString()
    {
        if (Id is not null) return $"{{id:{Id}}}";
        var parts = new List<string>();
        if (Type is not null) parts.Add("type:" + Type);
        if (At is not null) parts.Add("at:[" + string.Join(", ", At.Select(v => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture))) + "]");
        if (Name is not null) parts.Add("name:" + Name);
        if (Where is not null) parts.Add("where:{…}");
        if (Component is not null) parts.Add("component:" + Component);
        if (Sketch is not null) parts.Add("sketch:" + Sketch);
        return "{" + string.Join(", ", parts) + "}";
    }
}

/// <summary>Filter for face_list / edge_list / vertex_list and for <see cref="EntityRef.Where"/> queries. All set fields must match.</summary>
[Description("All set fields must match.")]
public sealed class EntityFilter
{
    [Description("face|edge|vertex (default: what the tool needs).")]
    public string? Type { get; set; }

    [Description("Faces: plane|cylinder|cone|sphere|torus|bspline.")]
    public string? Surface { get; set; }

    [Description("Edges: line|circle|ellipse|bspline.")]
    public string? Curve { get; set; }

    [Description("Planar face normal: +y, -z or 'x,y,z'.")]
    public string? Normal { get; set; }

    [Description("Line direction or axis parallel to: x, y, z or 'x,y,z'.")]
    public string? ParallelTo { get; set; }

    [Description("Radius in mm (faces and circular edges).")]
    public double? RadiusMm { get; set; }

    [Description("Radius tolerance in mm (default 0.001).")]
    public double? RadiusTolMm { get; set; }

    [Description("Edge length in mm.")]
    public double? LengthMm { get; set; }

    [Description("Only near this point [x,y,z] mm.")]
    public double[]? Near { get; set; }

    [Description("Distance for 'near' in mm (default 1).")]
    public double? WithinMm { get; set; }

    [Description("Only the extreme ones along an axis: +y = highest, -x = lowest.")]
    public string? Extreme { get; set; }

    [Description("Made by this feature, e.g. Boss-Extrude1.")]
    public string? Feature { get; set; }

    [Description("Body name or id.")]
    public string? Body { get; set; }

    [Description("Edges/vertices of this face id, e.g. F2.")]
    public string? OfFace { get; set; }

    [Description("With ofFace: outer|inner.")]
    public string? Loop { get; set; }

    [Description("Assembly component, e.g. bracket-1.")]
    public string? Component { get; set; }

    [Description("Angle tolerance in degrees for normal/parallelTo (default 1).")]
    public double? AngleTolDeg { get; set; }
}
