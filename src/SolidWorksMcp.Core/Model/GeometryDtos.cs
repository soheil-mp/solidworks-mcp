namespace SolidWorksMcp.Core.Model;

/// <summary>A face as tools report it (mm, model/assembly space).</summary>
public sealed class FaceInfo
{
    public required string Id { get; init; }
    public required string Surface { get; init; }
    public double AreaMm2 { get; init; }
    /// <summary>A point strictly inside the face: passing it back as {at:...} resolves to this face.</summary>
    public required double[] PickMm { get; init; }
    public double[]? Normal { get; init; }
    public double[]? AxisOriginMm { get; init; }
    public double[]? Axis { get; init; }
    public double? RadiusMm { get; init; }
    public double? HalfAngleDeg { get; init; }
    public string? Feature { get; init; }
    public string? Body { get; init; }
    public string? Component { get; init; }
}

/// <summary>An edge as tools report it (mm, model/assembly space).</summary>
public sealed class EdgeInfo
{
    public required string Id { get; init; }
    public required string Curve { get; init; }
    public double LengthMm { get; init; }
    public required double[] StartMm { get; init; }
    public required double[] EndMm { get; init; }
    /// <summary>Mid-parameter point: passing it back as {at:...} resolves to this edge.</summary>
    public required double[] MidMm { get; init; }
    public double? RadiusMm { get; init; }
    public double[]? CenterMm { get; init; }
    public double[]? Axis { get; init; }
    public double[]? Direction { get; init; }
    public bool? Closed { get; init; }
    public IReadOnlyList<string>? Faces { get; init; }
    public string? Component { get; init; }
}

public sealed class VertexInfo
{
    public required string Id { get; init; }
    public required double[] PointMm { get; init; }
    public string? Component { get; init; }
}

/// <summary>An axis-aligned box in mm.</summary>
public sealed class BoxMm
{
    public required double[] Min { get; init; }
    public required double[] Max { get; init; }
    public required double[] Size { get; init; }

    public static BoxMm? FromMeters(double[]? b)
    {
        if (b is null || b.Length < 6 || b.Any(v => !double.IsFinite(v))) return null;
        return new BoxMm
        {
            Min = [Geometry.Units.Mm(b[0]), Geometry.Units.Mm(b[1]), Geometry.Units.Mm(b[2])],
            Max = [Geometry.Units.Mm(b[3]), Geometry.Units.Mm(b[4]), Geometry.Units.Mm(b[5])],
            Size = [Geometry.Units.Mm(b[3] - b[0]), Geometry.Units.Mm(b[4] - b[1]), Geometry.Units.Mm(b[5] - b[2])],
        };
    }
}

/// <summary>A feature created or reported by a tool.</summary>
public sealed class FeatureRef
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public int? Error { get; init; }
    public bool? Warning { get; init; }
}

/// <summary>Model state after a change (so the agent needs no extra inspection call).</summary>
public sealed class ModelState
{
    public double VolumeMm3 { get; init; }
    public int Bodies { get; init; }
    public BoxMm? Box { get; init; }
}

/// <summary>Evidence that a feature tool did what it claims.</summary>
public sealed class Verified
{
    public double VolumeDeltaMm3 { get; init; }
    public double AreaDeltaMm2 { get; init; }
    public int BodyCountDelta { get; init; }
    public int FaceCountDelta { get; init; }
    /// <summary>added | removed | none.</summary>
    public required string Material { get; init; }
    public IReadOnlyList<string>? Checks { get; init; }
}

/// <summary>A dimension of a feature or sketch.</summary>
public sealed class DimensionInfo
{
    public required string Name { get; init; }
    public double Value { get; init; }
    /// <summary>mm | deg | count.</summary>
    public required string Unit { get; init; }
    public bool? Driven { get; init; }
    /// <summary>The tolerance, when it has one (type other than none).</summary>
    public ToleranceInfo? Tolerance { get; init; }
}

/// <summary>A dimension's tolerance as SOLIDWORKS reads it back (deviations in the dimension's unit: mm or deg).</summary>
public sealed class ToleranceInfo
{
    /// <summary>none | basic | bilateral | limit | symmetric | min | max | fit | fit_with_tolerance | fit_tolerance_only.</summary>
    public required string Type { get; init; }
    /// <summary>Upper deviation (symmetric: the ± value).</summary>
    public double? Upper { get; init; }
    /// <summary>Lower deviation, usually negative.</summary>
    public double? Lower { get; init; }
    /// <summary>ISO hole class of a fit, e.g. H7.</summary>
    public string? Hole { get; init; }
    /// <summary>ISO shaft class of a fit, e.g. g6.</summary>
    public string? Shaft { get; init; }
}

/// <summary>Result of every feature-creating tool.</summary>
public sealed class FeatureResult : ToolResult
{
    public required FeatureRef Feature { get; init; }
    public IReadOnlyList<FeatureRef>? AlsoCreated { get; init; }
    public string? ConsumedSketch { get; init; }
    public double[]? DirectionModel { get; init; }
    public required Verified Verified { get; init; }
    public required ModelState Model { get; init; }
    public IReadOnlyList<FaceInfo>? NewFaces { get; init; }
    public int? NewFaceCount { get; init; }
    public IReadOnlyList<DimensionInfo>? Dimensions { get; init; }
    public IReadOnlyList<Geometry.ResolvedInfo>? Resolved { get; init; }
}
