namespace SolidWorksMcp.Core.Geometry;

/// <summary>What one entity reference resolved to (echoed in results so the agent can check the pick).</summary>
public sealed class ResolvedInfo
{
    public required string Input { get; init; }
    public string? Id { get; init; }
    public required string Type { get; init; }
    public required string Label { get; init; }
    public double? DistanceMm { get; init; }
    public double[]? PickMm { get; init; }
}
