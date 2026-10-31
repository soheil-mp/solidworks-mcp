using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace SolidWorksMcp.Core.Services;

// Interop-free half of material_add (linked into the offline unit tests): the custom material library this server writes.

/// <summary>A custom material in SI: kg/m³, Pa, W/(m·K), J/(kg·K), 1/K.</summary>
public sealed record MaterialDef(string Name, string Classification, double DensityKgM3, double ElasticModulusPa, double PoissonRatio,
    double ShearModulusPa, double? YieldPa = null, double? TensilePa = null, double? ThermalConductivity = null, double? SpecificHeat = null,
    double? ThermalExpansion = null);

/// <summary>
/// The .sldmat format (SOLIDWORKS' own files: UTF-16 XML, a mstns:materials root holding classification elements with
/// material elements and their physicalproperties: EX, NUXY, GXY, ALPX, DENS, KX, C, SIGXT, SIGYLD, values in SI). The
/// libraries this server writes carry a marker comment, and only those are ever changed.
/// </summary>
internal static class MaterialXml
{
    public const string Ns = "http://www.solidworks.com/sldmaterials";
    public const string Marker = "written by solidworks-mcp";

    private static readonly (string Tag, string Display)[] Props =
    [
        ("EX", "Elastic Modulus"), ("NUXY", "Poisson's Ratio"), ("GXY", "Shear Modulus"), ("ALPX", "Thermal Expansion Coefficient"),
        ("DENS", "Density"), ("KX", "Thermal Conductivity"), ("C", "Specific Heat"), ("SIGXT", "Tensile Strength"), ("SIGYLD", "Yield Strength"),
    ];

    public static XDocument NewLibrary() => new(new XDeclaration("1.0", "utf-16", null),
        new XComment($" {Marker}: materials added with material_add "),
        new XElement(XName.Get("materials", Ns), new XAttribute(XNamespace.Xmlns + "mstns", Ns), new XAttribute("version", "2008.03")));

    public static bool IsOurs(XDocument doc) => doc.Nodes().OfType<XComment>().Any(c => c.Value.Contains(Marker, StringComparison.Ordinal));

    public static XDocument Parse(string xml) =>
        XDocument.Load(XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }));

    /// <summary>Adds the material under its classification (created when missing); one of that name is replaced only with <paramref name="overwrite"/>.</summary>
    public static bool Upsert(XDocument doc, MaterialDef m, bool overwrite)
    {
        var root = doc.Root ?? throw new SwException(ErrorCodes.Internal, "The material library has no root element.");
        var existing = root.Descendants("material").FirstOrDefault(e => string.Equals((string?)e.Attribute("name"), m.Name, StringComparison.Ordinal));
        if (existing is not null)
        {
            if (!overwrite)
                throw SwException.InvalidArgument($"The library already has a material named '{m.Name}'.", "Pass overwrite:true to replace it, or pick another name.");
            var parent = existing.Parent;
            existing.Remove();
            if (parent is not null && parent != root && !parent.Elements("material").Any()) parent.Remove();
        }
        var cls = root.Elements("classification").FirstOrDefault(e => string.Equals((string?)e.Attribute("name"), m.Classification, StringComparison.Ordinal));
        if (cls is null)
        {
            cls = new XElement("classification", new XAttribute("name", m.Classification));
            root.Add(cls);
        }
        cls.Add(Element(m));
        return existing is not null;
    }

    private static XElement Element(MaterialDef m)
    {
        double?[] values = [m.ElasticModulusPa, m.PoissonRatio, m.ShearModulusPa, m.ThermalExpansion, m.DensityKgM3, m.ThermalConductivity,
            m.SpecificHeat, m.TensilePa, m.YieldPa];
        var phys = new XElement("physicalproperties");
        for (int i = 0; i < Props.Length; i++)
            if (values[i] is double v)
                phys.Add(new XElement(Props[i].Tag, new XAttribute("displayname", Props[i].Display), new XAttribute("value", v.ToString("R", CultureInfo.InvariantCulture))));
        return new XElement("material", new XAttribute("name", m.Name), phys);
    }

    /// <summary>The physical properties of the material <paramref name="name"/> as written (tag → SI value), or null.</summary>
    public static IReadOnlyDictionary<string, double>? Read(XDocument doc, string name)
    {
        var mat = doc.Root?.Descendants("material").FirstOrDefault(e => string.Equals((string?)e.Attribute("name"), name, StringComparison.Ordinal));
        if (mat is null) return null;
        var map = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var p in mat.Element("physicalproperties")?.Elements() ?? [])
            if (double.TryParse((string?)p.Attribute("value"), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) map[p.Name.LocalName] = v;
        return map;
    }

    /// <summary>The tags and SI values a material should read back with.</summary>
    public static IReadOnlyDictionary<string, double> Expected(MaterialDef m)
    {
        double?[] values = [m.ElasticModulusPa, m.PoissonRatio, m.ShearModulusPa, m.ThermalExpansion, m.DensityKgM3, m.ThermalConductivity,
            m.SpecificHeat, m.TensilePa, m.YieldPa];
        var map = new Dictionary<string, double>(StringComparer.Ordinal);
        for (int i = 0; i < Props.Length; i++)
            if (values[i] is double v) map[Props[i].Tag] = v;
        return map;
    }

    /// <summary>The library as SOLIDWORKS writes its own: UTF-16 with a byte-order mark.</summary>
    public static byte[] Bytes(XDocument doc)
    {
        using var ms = new MemoryStream();
        using (var w = XmlWriter.Create(ms, new XmlWriterSettings { Encoding = new UnicodeEncoding(false, true), Indent = true }))
            doc.Save(w);
        return ms.ToArray();
    }

    /// <summary>
    /// Checks a material before it is written: a name and classification SOLIDWORKS can show, and properties in physical
    /// ranges (ν between -1 and 0.5, positive moduli and density). The shear modulus defaults to E / (2(1+ν)).
    /// </summary>
    public static MaterialDef Check(string? name, string? classification, double? densityKgM3, double? elasticModulusGpa, double? poissonRatio,
        double? shearModulusGpa, double? yieldMpa, double? tensileMpa, double? conductivity, double? specificHeat, double? expansion)
    {
        static double Need(double? v, string arg, double min, double max)
        {
            if (v is not double x) throw SwException.InvalidArgument($"'{arg}' is required.");
            if (!double.IsFinite(x) || x <= min || x > max)
                throw SwException.InvalidArgument($"{arg} must be greater than {min.ToString(CultureInfo.InvariantCulture)} and at most {max.ToString(CultureInfo.InvariantCulture)} (got {x.ToString(CultureInfo.InvariantCulture)}).");
            return x;
        }
        static double? Opt(double? v, string arg, double min, double max) => v is null ? null : Need(v, arg, min, max);
        static string Text(string? s, string arg, string? fallback)
        {
            string t = (s ?? fallback ?? "").Trim();
            if (t.Length == 0) throw SwException.InvalidArgument($"'{arg}' is required.");
            if (t.Length > 80 || t.Any(char.IsControl) || t.IndexOfAny(['<', '>', '&', '"']) >= 0)
                throw SwException.InvalidArgument($"'{arg}' must be at most 80 plain characters (got '{t}').");
            return t;
        }
        double nu = poissonRatio is double p && double.IsFinite(p) && p > -1 && p < 0.5
            ? p
            : throw SwException.InvalidArgument($"poissonRatio must be between -1 and 0.5 (got {poissonRatio?.ToString(CultureInfo.InvariantCulture) ?? "nothing"}).");
        double e = Need(elasticModulusGpa, "elasticModulusGpa", 0, 2000) * 1e9;
        double g = Opt(shearModulusGpa, "shearModulusGpa", 0, 2000) is double gg ? gg * 1e9 : e / (2 * (1 + nu));
        double? ys = Opt(yieldMpa, "yieldMpa", 0, 100_000) * 1e6, ts = Opt(tensileMpa, "tensileMpa", 0, 100_000) * 1e6;
        if (ys is double y0 && ts is double t0 && y0 > t0) throw SwException.InvalidArgument($"yieldMpa ({yieldMpa}) is above tensileMpa ({tensileMpa}).");
        return new MaterialDef(Text(name, "name", null), Text(classification, "classification", "solidworks-mcp"),
            Need(densityKgM3, "densityKgM3", 0, 30_000), e, nu, g, ys, ts,
            Opt(conductivity, "thermalConductivityWmK", 0, 10_000), Opt(specificHeat, "specificHeatJkgK", 0, 100_000), Opt(expansion, "thermalExpansionPerK", -1e-3, 1e-3));
    }
}
