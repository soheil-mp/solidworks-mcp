using System.Runtime.InteropServices;

namespace SolidWorksMcp.Core.Com;

/// <summary>
/// Marshalling helpers for the SolidWorks API's VARIANT-typed arguments and results. Recipes were proven in late-bound
/// VBA, so the VARIANT "nothing" flavours must be chosen deliberately (measured on .NET 10):
/// <list type="table">
/// <item><term><see cref="Empty"/></term><description>C# null → VT_EMPTY (VBA <c>Empty</c> / an unset Variant)</description></item>
/// <item><term><see cref="Nothing"/></term><description>VT_DISPATCH null (VBA <c>Nothing</c>)</description></item>
/// <item><term><see cref="Null"/></term><description>VT_NULL (VBA <c>Null</c>)</description></item>
/// <item><term><see cref="Missing"/></term><description>VT_ERROR DISP_E_PARAMNOTFOUND (an omitted optional)</description></item>
/// </list>
/// Arrays: <see cref="VariantArray"/> gives VT_ARRAY|VT_VARIANT (VBA <c>Array(x)</c>); <see cref="DispatchArray"/>
/// gives VT_ARRAY|VT_DISPATCH (what SOLIDWORKS documents for .NET object-array inputs). SAFEARRAY results arrive as
/// <c>object[]</c>/<c>double[]</c>/<c>byte[]</c>, or null when empty.
/// </summary>
internal static class ComArgs
{
    public static readonly object? Empty = null;
    public static readonly object Nothing = new DispatchWrapper(null);
    public static readonly object Null = DBNull.Value;
    public static readonly object Missing = Type.Missing;

    /// <summary>VT_ARRAY|VT_VARIANT with each element as given (RCWs become VT_DISPATCH elements).</summary>
    public static object[] VariantArray(params object[] items) => items;

    /// <summary>VT_ARRAY|VT_DISPATCH.</summary>
    public static DispatchWrapper[] DispatchArray(IEnumerable<object> objects) =>
        objects.Select(o => new DispatchWrapper(o)).ToArray();

    /// <summary>A VARIANT array of doubles → double[] (empty when null).</summary>
    public static double[] Doubles(object? v) => v switch
    {
        null => [],
        double[] d => d,
        object[] o => o.Select(Convert.ToDouble).ToArray(),
        float[] f => f.Select(x => (double)x).ToArray(),
        int[] i => i.Select(x => (double)x).ToArray(),
        Array a => a.Cast<object>().Select(Convert.ToDouble).ToArray(),
        _ => throw new SwException(ErrorCodes.Internal, $"Expected a numeric array, got {v.GetType().Name}."),
    };

    /// <summary>A VARIANT array of ints → int[] (empty when null).</summary>
    public static int[] Ints(object? v) => v switch
    {
        null => [],
        int[] i => i,
        Array a => a.Cast<object>().Select(Convert.ToInt32).ToArray(),
        _ => throw new SwException(ErrorCodes.Internal, $"Expected an integer array, got {v.GetType().Name}."),
    };

    /// <summary>A VARIANT byte array → byte[] (null when null).</summary>
    public static byte[]? Bytes(object? v) => v switch
    {
        null => null,
        byte[] b => b,
        Array a => a.Cast<object>().Select(Convert.ToByte).ToArray(),
        _ => throw new SwException(ErrorCodes.Internal, $"Expected a byte array, got {v.GetType().Name}."),
    };

    /// <summary>A VARIANT array of strings → string[] (empty when null).</summary>
    public static string[] Strings(object? v) => v switch
    {
        null => [],
        string[] s => s,
        string s => [s],
        Array a => a.Cast<object?>().Select(x => x?.ToString() ?? "").ToArray(),
        _ => throw new SwException(ErrorCodes.Internal, $"Expected a string array, got {v.GetType().Name}."),
    };

    /// <summary>A VARIANT array of dispatch objects → typed list (nulls skipped; empty when null).</summary>
    public static List<T> Objects<T>(object? v) where T : class
    {
        if (v is null) return [];
        if (v is T single) return [single];
        if (v is not Array a) throw new SwException(ErrorCodes.Internal, $"Expected an object array, got {v.GetType().Name}.");
        var list = new List<T>(a.Length);
        foreach (var o in a)
            if (o is T t) list.Add(t);
        return list;
    }

    /// <summary>HRESULTs meaning the SOLIDWORKS process (or the object's server) is gone.</summary>
    public static bool IsDisconnect(Exception ex) =>
        ex is InvalidComObjectException ||
        ex is COMException ce && (uint)ce.HResult is
            0x80010108 /*RPC_E_DISCONNECTED*/ or 0x800706BA /*RPC_S_SERVER_UNAVAILABLE*/ or 0x800706BE /*RPC_S_CALL_FAILED*/ or
            0x800706BF /*RPC_S_CALL_FAILED_DNE*/ or 0x80010007 /*RPC_E_SERVER_DIED*/ or 0x80010012 /*RPC_E_SERVER_DIED_DNE*/ or
            0x800401FD /*CO_E_OBJNOTCONNECTED*/ or 0x80010114 /*RPC_E_INVALID_OBJECT*/;

    /// <summary>True for "the call was rejected because SOLIDWORKS is busy" (after the message filter gave up).</summary>
    public static bool IsRejected(Exception ex) =>
        ex is COMException ce && (uint)ce.HResult is 0x80010001 /*RPC_E_CALL_REJECTED*/ or 0x8001010A /*RPC_E_SERVERCALL_RETRYLATER*/;

    /// <summary>RPC_E_SERVERFAULT: SOLIDWORKS threw inside the member (it is still alive).</summary>
    public static bool IsServerFault(Exception ex) => ex is COMException ce && (uint)ce.HResult == 0x80010105;
}
