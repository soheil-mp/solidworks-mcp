using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace SolidWorksMcp.Core.Com;

/// <summary>Typed wrappers used by export, open and import (VARIANT/IDispatch results cast to object first).</summary>
internal static partial class SwExt
{
    /// <summary><c>ISldWorks.GetOpenDocumentByName</c>: the open document with that path or name, else null.</summary>
    public static IModelDoc2? OpenDocNamed(this ISldWorks app, string pathOrName)
    {
        try { return (object?)app.GetOpenDocumentByName(pathOrName) as IModelDoc2; }
        catch (System.Runtime.InteropServices.COMException) { return null; }
    }

    /// <summary>
    /// <c>ISldWorks.GetImportFileData</c>: the import options object LoadFile4 takes (STEP, IGES, …), or null for
    /// formats that have none.
    /// </summary>
    public static object? ImportDataFor(this ISldWorks app, string path)
    {
        try { return (object?)app.GetImportFileData(path); }
        catch (System.Runtime.InteropServices.COMException) { return null; }
    }

    /// <summary>A PDF export-options object (<c>GetExportFileData(swExportPdfData)</c>), or null.</summary>
    public static IExportPdfData? PdfExportData(this ISldWorks app) =>
        (object?)app.GetExportFileData((int)swExportDataFileType_e.swExportPdfData) as IExportPdfData;

    /// <summary>The assembly's bounding box [xmin, ymin, zmin, xmax, ymax, zmax] in meters (empty when unknown).</summary>
    public static double[] AsmBox(this IAssemblyDoc asm) => ComArgs.Doubles((object?)asm.GetBox(0));

    /// <summary>The document's configuration names (empty for drawings).</summary>
    public static string[] ConfigNames(this IModelDoc2 doc) => ComArgs.Strings((object?)doc.GetConfigurationNames());

    /// <summary>
    /// <c>ISldWorks.GetConfigurationNames(path)</c>: the configurations stored in a part or assembly file, read without
    /// opening it (empty when unknown).
    /// </summary>
    public static string[] ConfigNamesOf(this ISldWorks app, string path)
    {
        try { return ComArgs.Strings((object?)app.GetConfigurationNames(path)); }
        catch (System.Runtime.InteropServices.COMException) { return []; }
    }

    /// <summary>
    /// <c>IModelDoc2.GetDependencies2(traverse, search, false)</c>: the documents this one references, as
    /// [name, path, name, path, …] (empty when none).
    /// </summary>
    public static string[] DependencyPairs(this IModelDoc2 doc, bool traverse) =>
        ComArgs.Strings((object?)doc.GetDependencies2(traverse, true, false));
}
