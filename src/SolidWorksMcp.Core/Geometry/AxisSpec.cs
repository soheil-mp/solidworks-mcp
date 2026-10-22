using System.Globalization;
using System.Text.Json;

namespace SolidWorksMcp.Core.Geometry;

/// <summary>Axis and direction specs used by filters and tools: "+x", "-y", "z", "x+", "x,y,z" (pure; unit-tested).</summary>
public static class AxisSpec
{
    /// <summary>Parses "+x", "-y", "z", "x+" or [x,y,z] (as JsonElement, double[] or string) into a unit vector.</summary>
    public static Vec3 Parse(object spec, string argName)
    {
        switch (spec)
        {
            case string s: return ParseAxisString(s, argName);
            case double[] a when a.Length == 3: return new Vec3(a[0], a[1], a[2]).Normalized();
            case JsonElement je when je.ValueKind == JsonValueKind.String: return ParseAxisString(je.GetString()!, argName);
            case JsonElement je when je.ValueKind == JsonValueKind.Array && je.GetArrayLength() == 3:
                var v = je.EnumerateArray().Select(x => x.GetDouble()).ToArray();
                if (v.All(c => c == 0)) break;
                return new Vec3(v[0], v[1], v[2]).Normalized();
        }
        throw SwException.InvalidArgument($"'{argName}' must be \"+x\", \"-y\", \"z\"… or a vector [x, y, z].");
    }

    internal static Vec3 ParseAxisString(string s, string argName)
    {
        string t = s.Trim().ToLowerInvariant().Replace(" ", "");
        if (t.Contains(','))
        {
            var parts = t.Trim('[', ']').Split(',');
            if (parts.Length == 3 && parts.All(x => double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
            {
                var v = new Vec3(double.Parse(parts[0], CultureInfo.InvariantCulture), double.Parse(parts[1], CultureInfo.InvariantCulture),
                    double.Parse(parts[2], CultureInfo.InvariantCulture));
                if (v.Length > 1e-12) return v.Normalized();
            }
            throw SwException.InvalidArgument($"'{argName}': '{s}' is not a vector \"x,y,z\".");
        }
        double sign = 1;
        if (t.StartsWith('-')) { sign = -1; t = t[1..]; }
        else if (t.StartsWith('+')) t = t[1..];
        else if (t.EndsWith('-')) { sign = -1; t = t[..^1]; }
        else if (t.EndsWith('+')) t = t[..^1];
        return t switch
        {
            "x" => Vec3.UnitX * sign,
            "y" => Vec3.UnitY * sign,
            "z" => Vec3.UnitZ * sign,
            _ => throw SwException.InvalidArgument($"'{argName}': '{s}' is not an axis.", "Use +x, -x, +y, -y, +z, -z or [x, y, z]."),
        };
    }

    public static (Vec3 Axis, double Sign) Extreme(string spec)
    {
        var a = ParseAxisString(spec, "where.extreme");
        double sign = a.X + a.Y + a.Z; // ±1
        return (new Vec3(Math.Abs(a.X), Math.Abs(a.Y), Math.Abs(a.Z)), sign);
    }
}
