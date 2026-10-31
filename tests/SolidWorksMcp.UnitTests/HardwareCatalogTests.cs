using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Services;
using Xunit;

namespace SolidWorksMcp.UnitTests;

/// <summary>hardware_part's tables (HardwareCatalog): ISO sizes and lengths, names, volumes and the bolt length through a nut.</summary>
public class HardwareCatalogTests
{
    private static SwException Refused(Action a) => Assert.Throws<SwException>(a);

    [Fact]
    public void A_socket_head_cap_screw_is_named_by_its_designation_and_has_the_volume_of_head_shank_less_socket()
    {
        var s = HardwareCatalog.Resolve("ISO 4762", "M5", 20);
        Assert.Equal("ISO 4762 - M5 x 20", s.Designation);
        Assert.Equal("ISO 4762 M5x20.sldprt", s.FileName);
        Assert.Equal("Hexagon socket head cap screw ISO 4762 - M5 x 20", s.Description);
        Assert.Equal((8.5, 5.0, 4.0, 2.5, 0.8), (s.Size.HeadD, s.Size.H, s.Size.Af, s.Size.SocketT, s.Size.Pitch));
        Assert.Equal(Math.PI / 4 * 8.5 * 8.5 * 5 + Math.PI / 4 * 25 * 20 - Math.Sqrt(3) / 2 * 16 * 2.5, s.VolumeMm3, 1e-9);
        Assert.Equal(20, s.ThreadLengthMm); // shorter than b = 22: threaded to the head
        Assert.Equal(22, HardwareCatalog.Resolve("ISO 4762", "M5", 40).ThreadLengthMm);
        Assert.Contains(("PartNo", "ISO 4762 - M5 x 20"), s.Properties);
        Assert.Contains(("Length", "20"), s.Properties);
        Assert.Contains((HardwareCatalog.MarkerProperty, HardwareCatalog.MarkerVersion), s.Properties);
    }

    [Fact]
    public void Standards_and_sizes_are_found_however_they_are_written()
    {
        Assert.Equal("ISO 4762", HardwareCatalog.Resolve("iso4762", "m5", 20).Standard.Name);
        Assert.Equal("ISO 4762", HardwareCatalog.Resolve("4762", "5", 20).Standard.Name);
        Assert.Equal("M2.5", HardwareCatalog.Resolve("ISO 4032", "M2.5", null).Size.Size);
        Assert.Equal(8.4, HardwareCatalog.Resolve("ISO 7089", "8", null).Size.Inner);
        Assert.Equal("6", HardwareCatalog.Resolve("ISO 8734", "d6", 20).Size.Size);
        Assert.Equal("ISO 15", HardwareCatalog.Resolve("iso 15", "6204", null).Standard.Name);
    }

    [Fact]
    public void Lengths_outside_the_series_or_the_sizes_range_are_refused_with_the_lengths_that_exist()
    {
        var odd = Refused(() => HardwareCatalog.Resolve("ISO 4762", "M5", 21));
        Assert.Equal(ErrorCodes.InvalidArgument, odd.Code);
        Assert.Equal("Lengths: 8, 10, 12, 16, 20, 25, 30, 35, 40, 45, 50.", odd.Hint);
        Refused(() => HardwareCatalog.Resolve("ISO 4762", "M5", 60));
        Refused(() => HardwareCatalog.Resolve("ISO 4762", "M5", null));
        Refused(() => HardwareCatalog.Resolve("ISO 4032", "M5", 20)); // nuts have no length
        Refused(() => HardwareCatalog.Resolve("ISO 4762", "M7", 20));
        Assert.Contains("ISO 4762 (hexagon socket head cap screw)", Refused(() => HardwareCatalog.Resolve("DIN 912", "M5", 20)).Hint);
        Assert.Equal("Lengths: 14, 16, 18, 20, 22, 24, 26, 28, 30, 32, 35, 40, 45, 50, 55, 60.",
            Refused(() => HardwareCatalog.Resolve("ISO 8734", "6", 13)).Hint);
    }

    [Fact]
    public void Nuts_washers_pins_and_bearings_have_the_volumes_of_their_simple_shapes()
    {
        var nut = HardwareCatalog.Resolve("ISO 4032", "M8", null);
        Assert.Equal((Math.Sqrt(3) / 2 * 13 * 13 - Math.PI / 4 * 64) * 6.8, nut.VolumeMm3, 1e-9);
        Assert.Equal("ISO 4032 M8.sldprt", nut.FileName);
        Assert.Equal("Hexagon nut ISO 4032 - M8", nut.Description);
        var washer = HardwareCatalog.Resolve("ISO 7089", "M8", null);
        Assert.Equal(Math.PI / 4 * (16 * 16 - 8.4 * 8.4) * 1.6, washer.VolumeMm3, 1e-9);
        var pin = HardwareCatalog.Resolve("ISO 8734", "6", 20);
        Assert.Equal(Math.PI / 4 * 36 * 20, pin.VolumeMm3, 1e-9);
        Assert.Equal("ISO 8734 - 6 x 20", pin.Designation);
        var bearing = HardwareCatalog.Resolve("ISO 15", "6204", null);
        Assert.Equal(Math.PI / 4 * (47 * 47 - 20 * 20) * 14, bearing.VolumeMm3, 1e-9);
        Assert.Equal("Deep groove ball bearing 6204 (20 x 47 x 14), simplified envelope", bearing.Description);
        var hex = HardwareCatalog.Resolve("ISO 4017", "M10", 40);
        Assert.Equal(Math.Sqrt(3) / 2 * 16 * 16 * 6.4 + Math.PI / 4 * 100 * 40, hex.VolumeMm3, 1e-9);
        Assert.Equal(40, hex.ThreadLengthMm); // ISO 4017: threaded to the head
    }

    [Fact]
    public void The_tables_are_consistent()
    {
        foreach (var std in HardwareCatalog.Standards)
            foreach (var s in std.Sizes)
            {
                string what = $"{std.Name} {s.Size}";
                Assert.True(s.D > 0, what);
                switch (std.Kind)
                {
                    case HardwareKind.SocketHeadScrew:
                        Assert.True(s.HeadD > s.D && s.H == s.D && s.Af < s.D && s.SocketT < s.H && s.ThreadB > 2 * s.D, what);
                        break;
                    case HardwareKind.HexHeadScrew:
                        Assert.True(s.Af > s.D && s.H < s.D, what);
                        break;
                    case HardwareKind.HexNut:
                        Assert.True(s.Af > s.D && s.H < s.D && s.Pitch > 0, what);
                        break;
                    case HardwareKind.PlainWasher:
                        Assert.True(s.Inner > s.D && s.Outer > s.Inner && s.H > 0, what);
                        break;
                    case HardwareKind.BallBearing:
                        Assert.True(s.Outer > s.D && s.H > 0, what);
                        break;
                }
                if (std.HasLength) Assert.True(s.MaxL > s.MinL && HardwareCatalog.Lengths(std, s).Length > 0, what);
                // Each screw size has its nut and washer.
                if (std.IsScrew)
                {
                    var screw = HardwareCatalog.Resolve(std.Name, s.Size, s.MaxL);
                    Assert.Equal(s.D, HardwareCatalog.NutFor(screw).Size.D);
                    Assert.Equal(s.D, HardwareCatalog.WasherFor(screw).Size.D);
                }
            }
    }

    [Fact]
    public void A_bolt_through_a_nut_is_the_shortest_length_that_stands_two_threads_past_it()
    {
        var m8 = HardwareCatalog.Resolve("ISO 4017", "M8", 30);
        // 20 mm grip + two 1.6 mm washers + the 6.8 mm nut + 2 × 1.25 = 32.5: the next length is 35.
        Assert.Equal(35, HardwareCatalog.ShortestThroughNut(m8, 20, 3.2, 6.8));
        Assert.Equal(30, HardwareCatalog.ShortestThroughNut(m8, 20, 0, 6.8)); // 29.3
        Assert.Null(HardwareCatalog.ShortestThroughNut(m8, 75, 3.2, 6.8)); // M8 ISO 4017 stops at 80
    }
}
