using System.ComponentModel;

namespace SolidWorksMcp.Core.Model;

/// <summary>
/// One entity for sketch_add. <c>kind</c> selects the shape; the fields used by each kind are listed in the tool
/// description. Points are [x, y] (sketch mm) or [x, y, z] (model mm, projected onto the sketch plane).
/// </summary>
public sealed class SketchEntity
{
    [Description("line | centerline | polyline | rectangle | circle | arc | slot | polygon | ellipse | spline | point")]
    public string Kind { get; set; } = "";

    [Description("line/centerline/slot: start point.")]
    public double[]? From { get; set; }

    [Description("line/centerline/slot: end point.")]
    public double[]? To { get; set; }

    [Description("polyline/spline: points in order.")]
    public double[][]? Points { get; set; }

    [Description("polyline: close the loop (last point back to the first).")]
    public bool? Closed { get; set; }

    [Description("rectangle: two opposite corners [[x1,y1],[x2,y2]].")]
    public double[][]? Corners { get; set; }

    [Description("rectangle/circle/arc/polygon/ellipse: center point.")]
    public double[]? Center { get; set; }

    [Description("rectangle (with center): width along sketch x, in mm.")]
    public double? WidthMm { get; set; }

    [Description("rectangle (with center): height along sketch y, in mm.")]
    public double? HeightMm { get; set; }

    [Description("circle/arc: radius in mm.")]
    public double? RadiusMm { get; set; }

    [Description("circle: diameter in mm (instead of radiusMm).")]
    public double? DiameterMm { get; set; }

    [Description("arc (3-point form): start point.")]
    public double[]? Start { get; set; }

    [Description("arc (3-point form): a point on the arc between start and end.")]
    public double[]? Mid { get; set; }

    [Description("arc (3-point form): end point.")]
    public double[]? End { get; set; }

    [Description("arc (center form): start angle in degrees, counter-clockwise from sketch +x.")]
    public double? StartDeg { get; set; }

    [Description("arc (center form): end angle in degrees (counter-clockwise from startDeg).")]
    public double? EndDeg { get; set; }

    [Description("polygon: number of sides (3-40).")]
    public int? Sides { get; set; }

    [Description("polygon: radius of the circumscribed circle (to the vertices), mm.")]
    public double? CircumradiusMm { get; set; }

    [Description("polygon: radius of the inscribed circle (to the flats), mm.")]
    public double? InradiusMm { get; set; }

    [Description("polygon/ellipse: rotation in degrees (polygon: first vertex angle; ellipse: major axis angle).")]
    public double? RotationDeg { get; set; }

    [Description("ellipse: semi-major radius in mm.")]
    public double? MajorRadiusMm { get; set; }

    [Description("ellipse: semi-minor radius in mm.")]
    public double? MinorRadiusMm { get; set; }

    [Description("point: location.")]
    public double[]? At { get; set; }

    [Description("Make the entity construction geometry (not part of profiles).")]
    public bool? Construction { get; set; }

    [Description("Your label for this entity; echoed in the result.")]
    public string? Label { get; set; }
}
