using System.Text;

namespace SolidWorksMcp.Core.Services;

/// <summary>Builds the SolidWorks BASIC wrapper for vba_run and decodes its output (pure; unit-tested).</summary>
public static class VbaText
{
    /// <summary>Wraps user code: prelude, error trap, output file. Non-cp1252 characters in strings become ChrW().</summary>
    public static string Wrap(string code, string outFile, string docName)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Dim swApp As Object");
        sb.AppendLine("Dim swDoc As Object");
        sb.AppendLine("Dim mcpFn As Integer");
        // Echo writes pure ASCII: numbers with a decimal point whatever the locale, and every non-ASCII character as
        // \uXXXX (Print # would turn it into '?' in the ANSI code page). The server decodes it.
        sb.AppendLine("Sub Echo(ByVal s As Variant)");
        sb.AppendLine("    Dim t As String, o As String, i As Long, c As Long");
        sb.AppendLine("    Select Case VarType(s)");
        sb.AppendLine("        Case 2, 3, 4, 5, 6, 14");
        sb.AppendLine("            t = Trim(Str(s))");
        sb.AppendLine("            If Left(t, 1) = \".\" Then t = \"0\" & t");
        sb.AppendLine("            If Left(t, 2) = \"-.\" Then t = \"-0\" & Mid(t, 2)");
        sb.AppendLine("        Case Else");
        sb.AppendLine("            t = CStr(s)");
        sb.AppendLine("    End Select");
        sb.AppendLine("    o = \"\"");
        sb.AppendLine("    For i = 1 To Len(t)");
        sb.AppendLine("        c = AscW(Mid(t, i, 1))");
        sb.AppendLine("        If c < 0 Then c = c + 65536");
        sb.AppendLine("        If c > 126 Or c = 92 Or c < 32 Then");
        sb.AppendLine("            o = o & \"\\u\" & Right(\"000\" & Hex(c), 4)");
        sb.AppendLine("        Else");
        sb.AppendLine("            o = o & Chr(c)");
        sb.AppendLine("        End If");
        sb.AppendLine("    Next i");
        sb.AppendLine("    Print #mcpFn, \"O|\" & o");
        sb.AppendLine("End Sub");
        sb.AppendLine("Function MM(ByVal x As Double) As Double");
        sb.AppendLine("    MM = x / 1000#");
        sb.AppendLine("End Function");
        sb.AppendLine("Function DEG(ByVal a As Double) As Double");
        sb.AppendLine("    DEG = a * 3.14159265358979 / 180#");
        sb.AppendLine("End Function");
        sb.AppendLine("Sub main()");
        sb.AppendLine("    mcpFn = FreeFile");
        sb.AppendLine($"    Open {EscapeNonAnsi(Literal(outFile))} For Output As #mcpFn");
        sb.AppendLine("    Print #mcpFn, \"started\"");
        sb.AppendLine("    Set swApp = Application.SldWorks");
        sb.AppendLine(docName.Length == 0
            ? "    Set swDoc = swApp.ActiveDoc"
            : $"    Set swDoc = swApp.GetOpenDocumentByName({EscapeNonAnsi(Literal(docName))})");
        sb.AppendLine("    If swDoc Is Nothing Then Set swDoc = swApp.ActiveDoc");
        sb.AppendLine("    On Error GoTo mcpErr");
        sb.AppendLine("    mcpUser");
        sb.AppendLine("    Print #mcpFn, \"OK\"");
        sb.AppendLine("    Close #mcpFn");
        sb.AppendLine("    Exit Sub");
        sb.AppendLine("mcpErr:");
        sb.AppendLine("    Print #mcpFn, \"ERR \" & Err.Number & \": \" & Err.Description");
        sb.AppendLine("    Close #mcpFn");
        sb.AppendLine("End Sub");
        sb.AppendLine("Sub mcpUser()");
        sb.AppendLine(EscapeNonAnsi(code));
        sb.AppendLine("End Sub");
        return sb.ToString();
    }

    private static string Literal(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";

    /// <summary>Decodes Echo's \uXXXX escapes.</summary>
    public static string Unescape(string s)
    {
        if (!s.Contains('\\')) return s;
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 5 < s.Length + 1 && s[i + 1] == 'u' && i + 6 <= s.Length &&
                int.TryParse(s.AsSpan(i + 2, 4), System.Globalization.NumberStyles.HexNumber, null, out int code))
            {
                sb.Append((char)code);
                i += 5;
            }
            else sb.Append(s[i]);
        }
        return sb.ToString();
    }

    /// <summary>Replaces characters cp1252 can't hold, inside string literals, with "..." &amp; ChrW(n) &amp; "...".</summary>
    public static string EscapeNonAnsi(string code)
    {
        // Pure ASCII file: the BASIC engine's code page then doesn't matter.
        static bool Representable(char ch) => ch < 128;
        var sb = new StringBuilder(code.Length + 16);
        bool inString = false;
        foreach (char ch in code)
        {
            if (ch == '"') { inString = !inString; sb.Append(ch); continue; }
            if (Representable(ch)) { sb.Append(ch); continue; }
            if (!inString)
                throw SwException.InvalidArgument($"Character '{ch}' (U+{(int)ch:X4}) outside a string literal can't be used in SolidWorks BASIC.");
            sb.Append("\" & ChrW(").Append((int)ch).Append(") & \"");
        }
        return sb.ToString();
    }
}
