using System.Globalization;

namespace SolidWorksMcp.Core.Services;

// Interop-free half of the standard-hardware area (linked into the offline unit tests): the ISO dimension tables, the
// size and length checks, the names and properties of the generated parts, the volumes they must have and the screw
// length a bolted joint needs.

/// <summary>What a hardware part is.</summary>
public enum HardwareKind { SocketHeadScrew, HexHeadScrew, HexNut, PlainWasher, DowelPin, BallBearing }

/// <summary>
/// One size of a standard, in mm. D: the thread or pin diameter, a washer's bolt size, a bearing's bore. H: the head height
/// k, a nut's height m, a washer's thickness h, a bearing's width B. Fields that don't apply are 0.
/// </summary>
public sealed record HardwareSize(string Size, double D, double Pitch = 0, double HeadD = 0, double H = 0, double Af = 0,
    double SocketT = 0, double ThreadB = 0, double Inner = 0, double Outer = 0, double MinL = 0, double MaxL = 0);

public sealed record HardwareStandard(string Name, HardwareKind Kind, string Title, IReadOnlyList<HardwareSize> Sizes)
{
    public bool HasLength => Kind is HardwareKind.SocketHeadScrew or HardwareKind.HexHeadScrew or HardwareKind.DowelPin;
    public bool IsScrew => Kind is HardwareKind.SocketHeadScrew or HardwareKind.HexHeadScrew;
}

/// <summary>A validated part: standard, size, length and what the generated part must be.</summary>
public sealed record HardwareSpec(HardwareStandard Standard, HardwareSize Size, double? LengthMm)
{
    public HardwareKind Kind => Standard.Kind;
    public string LengthText => LengthMm is double l ? l.ToString("0.###", CultureInfo.InvariantCulture) : "";
    /// <summary>"ISO 4762 - M5 x 20", the part number.</summary>
    public string Designation => Standard.HasLength ? $"{Standard.Name} - {Size.Size} x {LengthText}" : $"{Standard.Name} - {Size.Size}";
    public string FileName => Standard.HasLength ? $"{Standard.Name} {Size.Size}x{LengthText}.sldprt" : $"{Standard.Name} {Size.Size}.sldprt";
    public string Description => Kind == HardwareKind.BallBearing
        ? $"{Standard.Title} {Size.Size} ({F(Size.D)} x {F(Size.Outer)} x {F(Size.H)}), simplified envelope"
        : $"{Standard.Title} {Designation}";
    public bool IsThreaded => Kind is HardwareKind.SocketHeadScrew or HardwareKind.HexHeadScrew or HardwareKind.HexNut;
    /// <summary>The thread length: ISO 4762 screws longer than b are threaded over b, ISO 4017 ones fully, nuts through.</summary>
    public double ThreadLengthMm => Kind switch
    {
        HardwareKind.SocketHeadScrew => Math.Min(LengthMm!.Value, Size.ThreadB),
        HardwareKind.HexHeadScrew => LengthMm!.Value,
        HardwareKind.HexNut => Size.H,
        _ => 0,
    };
    public string ThreadText => F(ThreadLengthMm);

    /// <summary>The volume of the generated part, mm³ (the simplified geometry: no chamfers, no thread form).</summary>
    public double VolumeMm3
    {
        get
        {
            var s = Size;
            double hex = Math.Sqrt(3) / 2 * s.Af * s.Af; // a hexagon's area from its width across flats
            return Kind switch
            {
                HardwareKind.SocketHeadScrew => Circle(s.HeadD) * s.H + Circle(s.D) * LengthMm!.Value - hex * s.SocketT,
                HardwareKind.HexHeadScrew => hex * s.H + Circle(s.D) * LengthMm!.Value,
                HardwareKind.HexNut => (hex - Circle(s.D)) * s.H,
                HardwareKind.PlainWasher => (Circle(s.Outer) - Circle(s.Inner)) * s.H,
                HardwareKind.DowelPin => Circle(s.D) * LengthMm!.Value,
                _ => (Circle(s.Outer) - Circle(s.D)) * s.H,
            };
        }
    }

    /// <summary>The custom properties of the part (the BOM reads PartNo and Description).</summary>
    public IReadOnlyList<(string Name, string Value)> Properties
    {
        get
        {
            var p = new List<(string, string)> { ("PartNo", Designation), ("Description", Description), ("Standard", Standard.Name), ("Size", Size.Size) };
            if (LengthMm is not null) p.Add(("Length", LengthText));
            p.Add((HardwareCatalog.MarkerProperty, HardwareCatalog.MarkerVersion));
            return p;
        }
    }

    private static double Circle(double d) => Math.PI / 4 * d * d;
    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}

public static class HardwareCatalog
{
    /// <summary>The custom property that marks a part this server generated (and its geometry version).</summary>
    public const string MarkerProperty = "swmcp.hardware";
    public const string MarkerVersion = "1";
    public const string Material = "Alloy Steel";

    /// <summary>Preferred lengths of screws (ISO 4762, ISO 4017) and of dowel pins (ISO 8734).</summary>
    public static readonly double[] ScrewLengths = [2.5, 3, 4, 5, 6, 8, 10, 12, 16, 20, 25, 30, 35, 40, 45, 50, 55, 60, 65, 70, 80, 90, 100, 110, 120, 130, 140, 150, 160, 180, 200];
    public static readonly double[] PinLengths = [3, 4, 5, 6, 8, 10, 12, 14, 16, 18, 20, 22, 24, 26, 28, 30, 32, 35, 40, 45, 50, 55, 60, 65, 70, 75, 80, 85, 90, 95, 100];

    // ISO 4762: d, P, dk, k, s, t, b and the length range.
    private static HardwareSize Shcs(string m, double d, double p, double dk, double k, double s, double t, double b, double l0, double l1) =>
        new(m, d, p, HeadD: dk, H: k, Af: s, SocketT: t, ThreadB: b, MinL: l0, MaxL: l1);

    private static HardwareSize Hex(string m, double d, double p, double s, double k, double l0, double l1) =>
        new(m, d, p, H: k, Af: s, MinL: l0, MaxL: l1);

    private static HardwareSize Nut(string m, double d, double p, double s, double mh) => new(m, d, p, H: mh, Af: s);

    private static HardwareSize Washer(string m, double d, double d1, double d2, double h) => new(m, d, Inner: d1, Outer: d2, H: h);

    private static HardwareSize Pin(double d, double l0, double l1) => new(d.ToString("0.#", CultureInfo.InvariantCulture), d, MinL: l0, MaxL: l1);

    private static HardwareSize Bearing(string n, double d, double od, double b) => new(n, d, Outer: od, H: b);

    public static readonly IReadOnlyList<HardwareStandard> Standards =
    [
        new("ISO 4762", HardwareKind.SocketHeadScrew, "Hexagon socket head cap screw",
        [
            Shcs("M2", 2, 0.4, 3.8, 2, 1.5, 1, 16, 3, 20), Shcs("M2.5", 2.5, 0.45, 4.5, 2.5, 2, 1.1, 17, 4, 25),
            Shcs("M3", 3, 0.5, 5.5, 3, 2.5, 1.3, 18, 5, 30), Shcs("M4", 4, 0.7, 7, 4, 3, 2, 20, 6, 40),
            Shcs("M5", 5, 0.8, 8.5, 5, 4, 2.5, 22, 8, 50), Shcs("M6", 6, 1, 10, 6, 5, 3, 24, 10, 60),
            Shcs("M8", 8, 1.25, 13, 8, 6, 4, 28, 12, 80), Shcs("M10", 10, 1.5, 16, 10, 8, 5, 32, 16, 100),
            Shcs("M12", 12, 1.75, 18, 12, 10, 6, 36, 20, 120), Shcs("M16", 16, 2, 24, 16, 14, 8, 44, 25, 160),
            Shcs("M20", 20, 2.5, 30, 20, 17, 10, 52, 30, 200), Shcs("M24", 24, 3, 36, 24, 19, 12, 60, 40, 200),
        ]),
        new("ISO 4017", HardwareKind.HexHeadScrew, "Hexagon head screw",
        [
            Hex("M3", 3, 0.5, 5.5, 2, 6, 30), Hex("M4", 4, 0.7, 7, 2.8, 8, 40), Hex("M5", 5, 0.8, 8, 3.5, 10, 50),
            Hex("M6", 6, 1, 10, 4, 12, 60), Hex("M8", 8, 1.25, 13, 5.3, 16, 80), Hex("M10", 10, 1.5, 16, 6.4, 20, 100),
            Hex("M12", 12, 1.75, 18, 7.5, 25, 120), Hex("M16", 16, 2, 24, 10, 30, 200), Hex("M20", 20, 2.5, 30, 12.5, 40, 200),
            Hex("M24", 24, 3, 36, 15, 50, 200),
        ]),
        new("ISO 4032", HardwareKind.HexNut, "Hexagon nut",
        [
            Nut("M2", 2, 0.4, 4, 1.6), Nut("M2.5", 2.5, 0.45, 5, 2), Nut("M3", 3, 0.5, 5.5, 2.4), Nut("M4", 4, 0.7, 7, 3.2),
            Nut("M5", 5, 0.8, 8, 4.7), Nut("M6", 6, 1, 10, 5.2), Nut("M8", 8, 1.25, 13, 6.8), Nut("M10", 10, 1.5, 16, 8.4),
            Nut("M12", 12, 1.75, 18, 10.8), Nut("M16", 16, 2, 24, 14.8), Nut("M20", 20, 2.5, 30, 18), Nut("M24", 24, 3, 36, 21.5),
        ]),
        new("ISO 7089", HardwareKind.PlainWasher, "Plain washer",
        [
            Washer("M2", 2, 2.2, 5, 0.3), Washer("M2.5", 2.5, 2.7, 6, 0.5), Washer("M3", 3, 3.2, 7, 0.5), Washer("M4", 4, 4.3, 9, 0.8),
            Washer("M5", 5, 5.3, 10, 1), Washer("M6", 6, 6.4, 12, 1.6), Washer("M8", 8, 8.4, 16, 1.6), Washer("M10", 10, 10.5, 20, 2),
            Washer("M12", 12, 13, 24, 2.5), Washer("M16", 16, 17, 30, 3), Washer("M20", 20, 21, 37, 3), Washer("M24", 24, 25, 44, 4),
        ]),
        new("ISO 8734", HardwareKind.DowelPin, "Dowel pin",
        [
            Pin(1, 3, 10), Pin(1.5, 4, 16), Pin(2, 5, 20), Pin(2.5, 6, 24), Pin(3, 8, 30), Pin(4, 10, 40), Pin(5, 12, 50),
            Pin(6, 14, 60), Pin(8, 18, 80), Pin(10, 22, 100), Pin(12, 26, 100), Pin(16, 40, 100), Pin(20, 50, 100),
        ]),
        new("ISO 15", HardwareKind.BallBearing, "Deep groove ball bearing",
        [
            Bearing("6000", 10, 26, 8), Bearing("6001", 12, 28, 8), Bearing("6002", 15, 32, 9), Bearing("6003", 17, 35, 10),
            Bearing("6004", 20, 42, 12), Bearing("6005", 25, 47, 12), Bearing("6006", 30, 55, 13), Bearing("6007", 35, 62, 14),
            Bearing("6008", 40, 68, 15), Bearing("6009", 45, 75, 16), Bearing("6010", 50, 80, 16),
            Bearing("6200", 10, 30, 9), Bearing("6201", 12, 32, 10), Bearing("6202", 15, 35, 11), Bearing("6203", 17, 40, 12),
            Bearing("6204", 20, 47, 14), Bearing("6205", 25, 52, 15), Bearing("6206", 30, 62, 16), Bearing("6207", 35, 72, 17),
            Bearing("6208", 40, 80, 18), Bearing("6209", 45, 85, 19), Bearing("6210", 50, 90, 20),
            Bearing("6300", 10, 35, 11), Bearing("6301", 12, 37, 12), Bearing("6302", 15, 42, 13), Bearing("6303", 17, 47, 14),
            Bearing("6304", 20, 52, 15), Bearing("6305", 25, 62, 17), Bearing("6306", 30, 72, 19), Bearing("6307", 35, 80, 21),
            Bearing("6308", 40, 90, 23), Bearing("6309", 45, 100, 25), Bearing("6310", 50, 110, 27),
        ]),
    ];

    public static string StandardList => string.Join(", ", Standards.Select(s => $"{s.Name} ({s.Title.ToLowerInvariant()})"));

    /// <summary>The standard by name: "ISO 4762", "iso4762" or "4762".</summary>
    public static HardwareStandard Standard(string? name)
    {
        string key = Key(name);
        if (key.StartsWith("iso", StringComparison.Ordinal)) key = key[3..];
        return Standards.FirstOrDefault(s => Key(s.Name)[3..] == key)
               ?? throw SwException.InvalidArgument($"standard '{name}' is not supported.", $"Supported: {StandardList}.");
    }

    /// <summary>A size of a standard: "M5" or "5" (screws, nuts, washers), "6" or "d6" (pins), "6204" (bearings).</summary>
    public static HardwareSize SizeOf(HardwareStandard std, string? size)
    {
        if (string.IsNullOrWhiteSpace(size)) throw SwException.InvalidArgument("'size' is required, e.g. M5 (screws, nuts, washers), 6 (pins) or 6204 (bearings).");
        string want = Key(size);
        if (std.Kind == HardwareKind.DowelPin && want.StartsWith('d')) want = want[1..];
        if (std.Kind != HardwareKind.DowelPin && std.Kind != HardwareKind.BallBearing && !want.StartsWith('m')) want = "m" + want;
        return std.Sizes.FirstOrDefault(s => Key(s.Size) == want)
               ?? throw SwException.InvalidArgument($"{std.Name} has no size '{size}'.", $"Sizes: {string.Join(", ", std.Sizes.Select(s => s.Size))}.");
    }

    /// <summary>Validates standard, size and length (a preferred length in the size's range, for screws and pins).</summary>
    public static HardwareSpec Resolve(string? standard, string? size, double? lengthMm)
    {
        var std = Standard(standard);
        var sz = SizeOf(std, size);
        if (!std.HasLength)
        {
            if (lengthMm is not null) throw SwException.InvalidArgument($"{std.Name} parts have no length; leave lengthMm out.");
            return new HardwareSpec(std, sz, null);
        }
        if (lengthMm is not double l) throw SwException.InvalidArgument($"{std.Name} needs lengthMm (one of {Lengths(std, sz)}).");
        var series = std.Kind == HardwareKind.DowelPin ? PinLengths : ScrewLengths;
        if (!series.Any(x => Math.Abs(x - l) < 1e-9) || l < sz.MinL - 1e-9 || l > sz.MaxL + 1e-9)
            throw SwException.InvalidArgument($"{std.Name} {sz.Size} isn't made {l.ToString("0.###", CultureInfo.InvariantCulture)} mm long.",
                $"Lengths: {Lengths(std, sz)}.");
        return new HardwareSpec(std, sz, l);
    }

    /// <summary>The preferred lengths of a size, as text.</summary>
    public static string Lengths(HardwareStandard std, HardwareSize sz) =>
        string.Join(", ", (std.Kind == HardwareKind.DowelPin ? PinLengths : ScrewLengths)
            .Where(x => x >= sz.MinL - 1e-9 && x <= sz.MaxL + 1e-9).Select(x => x.ToString("0.###", CultureInfo.InvariantCulture)));

    /// <summary>
    /// The shortest preferred length of a screw that reaches through a nut: grip + washers + nut height + two pitches
    /// (a bolt should stand at least two threads past its nut); null when the size isn't made that long.
    /// </summary>
    public static double? ShortestThroughNut(HardwareSpec screw, double gripMm, double washersMm, double nutMm) =>
        ScrewLengths.Where(x => x >= screw.Size.MinL - 1e-9 && x <= screw.Size.MaxL + 1e-9)
            .Cast<double?>().FirstOrDefault(x => x >= gripMm + washersMm + nutMm + 2 * screw.Size.Pitch - 1e-9);

    /// <summary>A screw's nut and washer of the same size.</summary>
    public static HardwareSpec NutFor(HardwareSpec screw) => Resolve("ISO 4032", screw.Size.Size, null);
    public static HardwareSpec WasherFor(HardwareSpec screw) => Resolve("ISO 7089", screw.Size.Size, null);

    private static string Key(string? s) => new((s ?? "").Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
