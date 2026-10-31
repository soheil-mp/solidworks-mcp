using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Services;
using SolidWorksMcp.Server.Hosting;

namespace SolidWorksMcp.Server.Tools;

[McpServerToolType, Toolset("inspect")]
public sealed class MeasureTools(SwSession session)
{
    private readonly MeasureService _svc = new(session);

    private const string DocDescription = "Document (handle, title or path); default: the target document.";

    [McpServerTool(Name = "body_list", Title = "List bodies", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Bodies of the part: id (B…, usable as {id}), name, solid or surface, volume, area, bounding box (mm), face and edge " +
                 "counts, hidden. Works while a sketch is open.")]
    public async Task<string> BodyList(
        [Description(DocDescription)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ResultJson.Serialize(await _svc.BodyListAsync(doc, cancellationToken));

    [McpServerTool(Name = "model_measure", Title = "Measure", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Measure one entity or between two (SOLIDWORKS Measure): distance, normal and center distance, angle, dx/dy/dz, length, " +
                 "area, perimeter, radius, diameter, point coordinates; only the values that apply (mm, mm², deg). Faces, edges, vertices, " +
                 "planes, axes, points, sketch entities, e.g. a:{type:'face', at:[0,20,0]}, b:{name:'top'} ('at' needs a type here).")]
    public async Task<string> ModelMeasure(
        [Description("The entity to measure, or the first of two.")] EntityRef a,
        [Description("The second entity (distance, angle).")] EntityRef? b = null,
        [Description(DocDescription)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ResultJson.Serialize(await _svc.MeasureAsync(doc, a, b, cancellationToken));

    [McpServerTool(Name = "model_mass", Title = "Mass properties", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Mass properties of the part or assembly, or of one body: mass (kg), volume (mm³), area (mm²), density (kg/m³), center of " +
                 "mass (mm), principal moments (kg·mm²) and axes, inertia tensor at the center of mass. Cross-checked against the bodies' " +
                 "own volumes. SKETCH_STATE while a sketch is open (SOLIDWORKS reads zero then).")]
    public Task<string> ModelMass(
        IProgress<ProgressNotificationValue> progress,
        [Description("One solid body: {id:'B1'} (body_list), {name:'Boss-Extrude1'} or {at:[x,y,z]} on one of its faces.")] EntityRef? body = null,
        [Description(DocDescription)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_svc.MassAsync(doc, body, cancellationToken), progress, "model_mass");

    [McpServerTool(Name = "model_check", Title = "Check geometry", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Geometry check (IBody2.Check3) of every body of the part, or one: the faults with their error names and the faulty " +
                 "entity (id, point). valid=true with empty faults means the body is sound.")]
    public Task<string> ModelCheck(
        IProgress<ProgressNotificationValue> progress,
        [Description("One body: {id:'B1'} (body_list), {name:'Boss-Extrude1'} or {at:[x,y,z]} on one of its faces.")] EntityRef? body = null,
        [Description(DocDescription)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_svc.CheckAsync(doc, body, cancellationToken), progress, "model_check");

    [McpServerTool(Name = "model_review", Title = "Design review", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Health report of a part or assembly in one call, errors first: features with rebuild errors or warnings, over- and " +
                 "under-defined sketches, suppressed features, a pending rebuild; parts: no material, faulty geometry, several bodies; " +
                 "assemblies: missing, unloaded or suppressed components, floating ones and mate errors. Read-only.")]
    public Task<string> ModelReview(
        IProgress<ProgressNotificationValue> progress,
        [Description(DocDescription)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(new ReviewService(session).ReviewAsync(doc, cancellationToken), progress, "model_review");

    [McpServerTool(Name = "model_compare", Title = "Compare revisions", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Compares two revisions of a part or assembly: volume, area, mass and size (this − other), the features only one of " +
                 "them has (name and type) and every dimension whose value differs. 'other' is an open document (handle, title, path) " +
                 "or a file, which is opened read-only and closed again. same=true when nothing differs.")]
    public Task<string> ModelCompare(
        IProgress<ProgressNotificationValue> progress,
        [Description("The document to compare with: handle, title or path of an open one, or a file path.")] string other,
        [Description(DocDescription)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(new CompareService(session).CompareAsync(doc, other, cancellationToken), progress, "model_compare");

    [McpServerTool(Name = "model_section", Title = "Section properties", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Section properties of flat faces in one plane (a member's end face is its cross-section): area, centroid, the principal " +
                 "second moments of area with their axes, section moduli (I / farthest fibre), radii of gyration and the polar moment, all " +
                 "about the centroid. Verified: the area is the faces' own, the polar moment the sum of the principal ones.")]
    public Task<string> ModelSection(
        IProgress<ProgressNotificationValue> progress,
        [Description("Flat faces in one plane, e.g. [{at:[x,y,z]}] on a member's end.")] EntityRef[] faces,
        [Description(DocDescription)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(new SectionService(session).SectionAsync(doc, faces, cancellationToken), progress, "model_section");

    [McpServerTool(Name = "model_cost", Title = "Cost estimate", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("SOLIDWORKS Costing estimate of a single-body part (machined, sheet metal or structural, by how Costing sees the body, " +
                 "with its first template): material, manufacturing and setup cost, the totals to manufacture and to charge, lot size, " +
                 "quantity and the cost items. Needs a material. Verified: the costs are positive and add up.")]
    public Task<string> ModelCost(
        IProgress<ProgressNotificationValue> progress,
        [Description("Lot size (parts per setup).")] int? lotSize = null,
        [Description("Total quantity.")] int? quantity = null,
        [Description(DocDescription)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(new CostService(session).CostAsync(doc, lotSize, quantity, cancellationToken), progress, "model_cost");

    [McpServerTool(Name = "model_cut_list", Title = "Cut list", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Cut list of a weldment or sheet-metal part, for fabrication: each item (identical bodies) with its quantity, type and " +
                 "properties as SOLIDWORKS resolves them (LENGTH, ANGLE1/2 of members; bounding box, thickness and bends of blanks; " +
                 "description, material). update:true refreshes the cut list first. Verified: the items hold every solid body once.")]
    public Task<string> ModelCutList(
        IProgress<ProgressNotificationValue> progress,
        [Description("Refresh the cut list first (changes the part).")] bool update = false,
        [Description(DocDescription)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(new CutListService(session).CutListAsync(doc, update, cancellationToken), progress, "model_cut_list");
}
