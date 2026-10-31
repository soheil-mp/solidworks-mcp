using System.Runtime.InteropServices;
using SolidWorks.Interop.sldcostingapi;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Model;

namespace SolidWorksMcp.Core.Services;

/// <summary>A cost item of a Costing analysis (a feature or operation).</summary>
public sealed class CostItem
{
    public required string Name { get; init; }
    public double Cost { get; init; }
    /// <summary>Time, in the template's unit (minutes for the shipped templates); null when Costing gives none.</summary>
    public double? Time { get; init; }
    public bool Setup { get; init; }
}

public sealed class CostResult : ToolResult
{
    /// <summary>machining | sheet_metal | structural.</summary>
    public required string Method { get; init; }
    public required string Template { get; init; }
    public string? Currency { get; init; }
    public double MaterialCost { get; init; }
    public double ManufacturingCost { get; init; }
    public double SetupCost { get; init; }
    public double TotalToManufacture { get; init; }
    public double TotalToCharge { get; init; }
    public int LotSize { get; init; }
    public int Quantity { get; init; }
    /// <summary>The material Costing prices (its template's class for the part's material).</summary>
    public string? Material { get; init; }
    public string? PartMaterial { get; init; }
    public double? MassKg { get; init; }
    public required IReadOnlyList<CostItem> Items { get; init; }
    public required IReadOnlyList<string> Checks { get; init; }
}

/// <summary>
/// model_cost: a SOLIDWORKS Costing estimate of a single-body part (IModelDocExtension.GetCostingManager → ICostManager →
/// CostingModel as ICostPart; the common analysis on the part, then the method's own on its body, with the first template of
/// that method; metric ones first). Tier B: the costs are positive and add up (total = material + manufacturing).
/// </summary>
public sealed class CostService(SwSession session)
{
    public Task<CostResult> CostAsync(string? doc, int? lotSize, int? quantity, CancellationToken ct = default)
    {
        if (lotSize is < 1 or > 1_000_000) throw SwException.InvalidArgument($"lotSize must be 1..1000000 (got {lotSize}).");
        if (quantity is < 1 or > 10_000_000) throw SwException.InvalidArgument($"quantity must be 1..10000000 (got {quantity}).");
        return session.RunAsync("model_cost", ctx => Cost(ctx, lotSize, quantity), RunOptions.Write(doc) with
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(300, session.Options.DefaultTimeout.TotalSeconds)),
        }, ct);
    }

    private static CostResult Cost(SwContext ctx, int? lotSize, int? quantity)
    {
        var part = ctx.Part();
        var d = (IModelDoc2)part;
        if (part.Bodies(swBodyType_e.swSolidBody, visibleOnly: false).Count != 1)
            throw SwException.InvalidArgument($"'{ctx.Title(d)}' needs exactly one solid body for a Costing estimate.");
        if ((part.GetMaterialPropertyName2("", out _) ?? "").Length == 0)
            throw SwException.InvalidArgument($"'{ctx.Title(d)}' has no material; Costing prices the material by it.", "material_set first.");
        var cm = (object?)d.Extension.GetCostingManager() as CostManager
                 ?? throw new SwException(ErrorCodes.NotSupported, "SOLIDWORKS Costing is not available here (it comes with SOLIDWORKS Professional and Premium).");
        cm.WaitForUIUpdate();
        var cp = (object?)cm.CostingModel as CostPart ?? throw new SwException(ErrorCodes.NotSupported, "Costing offers no model for this part.");
        var body = ComArgs.Objects<CostBody>((object?)cp.GetBodies()).FirstOrDefault()
                   ?? throw new SwException(ErrorCodes.FeatureFailed, "Costing sees no body in the part.");
        var (kind, method) = body.GetBodyType() switch
        {
            (int)swcBodyType_e.swcBodyType_Machined => ((int)swcCostingType_e.swcCostingType_Machining, "machining"),
            (int)swcBodyType_e.swcBodyType_SheetMetal => ((int)swcCostingType_e.swcCostingType_SheetMetal, "sheet_metal"),
            (int)swcBodyType_e.swcBodyType_Structural => ((int)swcCostingType_e.swcCostingType_Structural, "structural"),
            int t => throw new SwException(ErrorCodes.NotSupported, $"Costing doesn't know how to make this body (body type {t})."),
        };
        string Template(int type) => ComArgs.Strings((object?)cm.GetTemplatePathnames(type)).OrderBy(t => t.Contains("metric", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .FirstOrDefault() ?? throw new SwException(ErrorCodes.NotSupported, "No Costing template is installed (Tools > Options > File Locations > Costing templates).");
        string common = Template((int)swcCostingType_e.swcCostingType_Common), specific = Template(kind);
        var analysis = (object?)cp.GetCostAnalysis() as CostAnalysis ?? (object?)cp.CreateCostAnalysis(common) as CostAnalysis
                       ?? throw new SwException(ErrorCodes.FeatureFailed, "Costing made no analysis for the part.");
        var bodyAnalysis = body.BodyStatus == (int)swcBodyStatus_e.swcBodyStatus_Analysed
            ? (object?)body.GetCostAnalysis() as CostAnalysis
            : (object?)body.CreateCostAnalysis(specific) as CostAnalysis;
        if (bodyAnalysis is null) throw new SwException(ErrorCodes.FeatureFailed, $"Costing made no {method} analysis of the body.");
        if (lotSize is int lot) analysis.LotSize = lot;
        if (quantity is int qty) analysis.TotalQuantity = qty;
        cm.WaitForUIUpdate();

        double material = analysis.GetMaterialCost(), manufacturing = analysis.GetManufacturingCost(), setup = analysis.GetSetupCost();
        double total = analysis.GetTotalCostToManufacture(), charge = analysis.GetTotalCostToCharge();
        var checks = new List<string>();
        if (!(material > 0) || !(total > 0))
            throw new SwException(ErrorCodes.VerifyFailed, $"The estimate is not usable: material {material}, total {total}.");
        if (Math.Abs(total - (material + manufacturing)) > 1e-6 * total + 1e-9)
            ctx.Warn($"The total to manufacture ({total}) is not material + manufacturing ({material} + {manufacturing}).");
        else checks.Add("total to manufacture = material + manufacturing");
        var items = new List<CostItem>();
        // Negative costs and times mean "none" (VERIFIED: the "No Cost Assigned" item reads -1 for both).
        for (var f = (object?)bodyAnalysis.GetFirstCostFeature() as CostFeature; f is not null && items.Count < 40; f = (object?)f.GetNextFeature() as CostFeature)
            if (f.CombinedCost >= 0)
                items.Add(new CostItem
                {
                    Name = f.Name ?? "", Cost = Math.Round(f.CombinedCost, 4), Time = f.CombinedTime >= 0 ? Math.Round(f.CombinedTime, 4) : null, Setup = f.IsSetup,
                });
        double? mass = (object?)d.Extension.CreateMassProperty2() is IMassProperty2 mp ? Math.Round(mp.Mass, 6) : null;
        string? mat = (object?)bodyAnalysis.GetSpecificAnalysis() is CostAnalysisMachining mach ? mach.CurrentMaterial : null;
        string partMaterial = part.GetMaterialPropertyName2("", out _) ?? "";
        if (mat is { Length: > 0 } && mat != partMaterial) checks.Add($"Costing prices '{partMaterial}' as its '{mat}'");
        checks.Add($"{method} estimate with '{Path.GetFileName(specific)}': {items.Count} cost item(s)");
        return new CostResult
        {
            Doc = ctx.Title(d), Method = method, Template = specific, Currency = analysis.CurrencyCode,
            MaterialCost = Math.Round(material, 4), ManufacturingCost = Math.Round(manufacturing, 4), SetupCost = Math.Round(setup, 4),
            TotalToManufacture = Math.Round(total, 4), TotalToCharge = Math.Round(charge, 4), LotSize = analysis.LotSize, Quantity = analysis.TotalQuantity,
            Material = mat, PartMaterial = partMaterial, MassKg = mass, Items = items, Checks = checks,
        };
    }
}
