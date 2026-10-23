using System.Globalization;
using SolidWorksMcp.Core.Prefs;
using Xunit;

namespace SolidWorksMcp.UnitTests;

/// <summary>
/// Restoring coupled preferences against a simulated SOLIDWORKS whose STL quality is derived from deviation and angle
/// (the behaviour verified live: coarse/fine overwrite both with their preset, non-preset values switch to custom,
/// custom doesn't stick while both hold a preset).
/// </summary>
public class PrefGroupTests
{
    private const int Q = SystemPrefIds.StlQuality, D = SystemPrefIds.StlDeviation, A = SystemPrefIds.StlAngleTolerance;
    private static readonly (double D, double A) Coarse = (0.0005952380952380953, Math.PI / 6), Fine = (0.000119, Math.PI / 18);
    private static readonly (double D, double A) UserCustom = (3.7147102526002975E-05, Math.PI / 18);

    private sealed class FakeStl
    {
        public int Quality = 3;
        public double Dev = UserCustom.D, Ang = UserCustom.A;
        public int Writes;

        public string Read(PrefKind kind, int pref) => (kind, pref) switch
        {
            (PrefKind.Integer, Q) => Quality.ToString(CultureInfo.InvariantCulture),
            (PrefKind.Double, D) => Dev.ToString("R", CultureInfo.InvariantCulture),
            (PrefKind.Double, A) => Ang.ToString("R", CultureInfo.InvariantCulture),
            _ => throw new ArgumentException($"{kind} {pref}"),
        };

        public void Write(PrefKind kind, int pref, string value)
        {
            Writes++;
            if (kind == PrefKind.Integer && pref == Q)
            {
                int q = int.Parse(value, CultureInfo.InvariantCulture);
                if (q == 1) (Dev, Ang, Quality) = (Coarse.D, Coarse.A, 1);
                else if (q == 2) (Dev, Ang, Quality) = (Fine.D, Fine.A, 2);
                else Derive(); // custom only "sticks" when the values aren't a preset
                return;
            }
            double v = double.Parse(value, CultureInfo.InvariantCulture);
            if (pref == D) Dev = v; else Ang = v;
            Derive();
        }

        private void Derive() =>
            Quality = (Dev, Ang) == Coarse ? 1 : (Dev, Ang) == Fine ? 2 : 3;

        /// <summary>What a PreferenceScope records for the group after the server wrote quality <paramref name="q"/>.</summary>
        public List<PrefValue> Hold(int q, (double D, double A)? custom = null)
        {
            var old = new[] { Read(PrefKind.Double, D), Read(PrefKind.Double, A), Read(PrefKind.Integer, Q) };
            if (custom is { } c)
            {
                Write(PrefKind.Double, D, c.D.ToString("R", CultureInfo.InvariantCulture));
                Write(PrefKind.Double, A, c.A.ToString("R", CultureInfo.InvariantCulture));
            }
            Write(PrefKind.Integer, Q, q.ToString(CultureInfo.InvariantCulture));
            return
            [
                new(PrefKind.Double, D, old[0], Read(PrefKind.Double, D)),
                new(PrefKind.Double, A, old[1], Read(PrefKind.Double, A)),
                new(PrefKind.Integer, Q, old[2], Read(PrefKind.Integer, Q)),
            ];
        }
    }

    [Fact]
    public void The_stl_group_restores_deviation_and_angle_before_the_quality()
    {
        var group = SystemPrefIds.GroupOf(PrefKind.Integer, Q);
        Assert.NotNull(group);
        Assert.Equal([(PrefKind.Double, D), (PrefKind.Double, A), (PrefKind.Integer, Q)], group);
        Assert.Same(group, SystemPrefIds.GroupOf(PrefKind.Double, A));
        Assert.Null(SystemPrefIds.GroupOf(PrefKind.Integer, SystemPrefIds.StepAp));
    }

    [Fact]
    public void A_coarse_export_gives_back_the_users_custom_values()
    {
        // The case that left a user's options at coarse: setting coarse overwrote deviation and angle with the preset.
        var sw = new FakeStl();
        var held = sw.Hold(1);
        Assert.Equal((1, Coarse.D, Coarse.A), (sw.Quality, sw.Dev, sw.Ang));
        Assert.Equal(PrefGroupRestore.Outcome.Restored, PrefGroupRestore.Restore(held, sw.Read, sw.Write));
        Assert.Equal((3, UserCustom.D, UserCustom.A), (sw.Quality, sw.Dev, sw.Ang));
    }

    [Fact]
    public void A_custom_export_gives_back_a_fine_preset()
    {
        var sw = new FakeStl { Quality = 2, Dev = Fine.D, Ang = Fine.A };
        var held = sw.Hold(3, custom: (5e-6, Math.PI / 90));
        Assert.Equal(3, sw.Quality);
        Assert.Equal(PrefGroupRestore.Outcome.Restored, PrefGroupRestore.Restore(held, sw.Read, sw.Write));
        Assert.Equal((2, Fine.D, Fine.A), (sw.Quality, sw.Dev, sw.Ang));
    }

    [Fact]
    public void A_group_someone_else_changed_is_left_alone()
    {
        var sw = new FakeStl();
        var held = sw.Hold(1);
        sw.Write(PrefKind.Integer, Q, "2"); // the user picked fine meanwhile
        int writes = sw.Writes;
        Assert.Equal(PrefGroupRestore.Outcome.ChangedExternally, PrefGroupRestore.Restore(held, sw.Read, sw.Write));
        Assert.Equal(writes, sw.Writes);
        Assert.Equal((2, Fine.D, Fine.A), (sw.Quality, sw.Dev, sw.Ang));
    }

    [Fact]
    public void An_untouched_group_needs_no_writes()
    {
        var sw = new FakeStl();
        var held = sw.Hold(1);
        PrefGroupRestore.Restore(held, sw.Read, sw.Write);
        int writes = sw.Writes;
        Assert.Equal(PrefGroupRestore.Outcome.AlreadyOriginal, PrefGroupRestore.Restore(held, sw.Read, sw.Write));
        Assert.Equal(writes, sw.Writes);
    }

    [Fact]
    public void A_single_preference_still_restores_with_compare_and_swap()
    {
        string value = "203";
        PrefValue[] unit = [new(PrefKind.Integer, SystemPrefIds.StepAp, "203", "214")];
        value = "214";
        Assert.Equal(PrefGroupRestore.Outcome.Restored, PrefGroupRestore.Restore(unit, (_, _) => value, (_, _, v) => value = v));
        Assert.Equal("203", value);
        value = "242"; // changed by someone else after the server's write
        Assert.Equal(PrefGroupRestore.Outcome.ChangedExternally, PrefGroupRestore.Restore(unit, (_, _) => value, (_, _, v) => value = v));
        Assert.Equal("242", value);
    }

    [Fact]
    public void A_value_that_does_not_stick_is_reported_as_failed()
    {
        PrefValue[] unit = [new(PrefKind.Toggle, SystemPrefIds.StlBinary, "True", "False")];
        Assert.Equal(PrefGroupRestore.Outcome.Failed, PrefGroupRestore.Restore(unit, (_, _) => "False", (_, _, _) => { }));
    }
}
