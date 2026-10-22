using System.Text.RegularExpressions;

namespace SolidWorksMcp.Core.Win32;

/// <summary>A button on a dialog.</summary>
public sealed record DialogButton(string Caption, int ControlId, bool Enabled, IntPtr Handle);

/// <summary>A visible top-level dialog window owned by the SOLIDWORKS process.</summary>
public sealed record DialogInfo(IntPtr Handle, string Title, string ClassName, string Text, IReadOnlyList<DialogButton> Buttons)
{
    public string HandleHex => $"0x{Handle.ToInt64():X}";

    public string Describe()
    {
        var buttons = string.Join(", ", Buttons.Select(b => b.Caption));
        var text = Text.Length > 300 ? Text[..300] + "…" : Text;
        return $"'{Title}'{(text.Length > 0 ? $": \"{text}\"" : "")} [buttons: {buttons}]";
    }
}

/// <summary>
/// An automatic answer the watchdog may give to a dialog while a specific operation runs.
/// <see cref="Button"/> is a caption (ampersands ignored, case-insensitive) or the special values "ok"/"cancel"
/// (sent as IDOK/IDCANCEL, which also works for owner-drawn dialogs).
/// </summary>
public sealed record DialogRule(string TitlePattern, string? TextPattern, string Button, string Reason, bool IsError = false)
{
    public bool Matches(DialogInfo dialog) =>
        Regex.IsMatch(dialog.Title, TitlePattern, RegexOptions.IgnoreCase) &&
        (TextPattern is null || Regex.IsMatch(dialog.Text, TextPattern, RegexOptions.IgnoreCase | RegexOptions.Singleline));
}

/// <summary>A dialog the watchdog answered automatically.</summary>
public sealed record HandledDialog(DialogInfo Dialog, DialogRule Rule, string Pressed, DateTime AtUtc);

/// <summary>
/// Win32 discovery of, and responses to, SOLIDWORKS dialogs. Needs no COM, so it works while the STA thread is blocked
/// by the very dialog being inspected.
/// </summary>
public static class Dialogs
{
    /// <summary>Visible top-level <c>#32770</c> dialogs of the given process.</summary>
    public static IReadOnlyList<DialogInfo> List(int processId)
    {
        var handles = new List<IntPtr>();
        NativeMethods.EnumWindows((h, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(h, out uint pid);
            if (pid == (uint)processId && NativeMethods.IsWindowVisible(h) && NativeMethods.GetClass(h) == "#32770")
                handles.Add(h);
            return true;
        }, IntPtr.Zero);
        return handles.Select(Describe).ToList();
    }

    public static DialogInfo Describe(IntPtr dialog)
    {
        var texts = new List<string>();
        var buttons = new List<DialogButton>();
        NativeMethods.EnumChildWindows(dialog, (h, _) =>
        {
            if (!NativeMethods.IsWindowVisible(h)) return true;
            string cls = NativeMethods.GetClass(h);
            if (cls == "Button")
            {
                string caption = NativeMethods.GetTextSafe(h);
                if (caption.Length > 0)
                    buttons.Add(new DialogButton(caption.Replace("&", ""), NativeMethods.GetDlgCtrlID(h), NativeMethods.IsWindowEnabled(h), h));
            }
            else if (cls is "Static" or "Edit" or "RichEdit20W" or "RICHEDIT50W")
            {
                string t = NativeMethods.GetTextSafe(h).Trim();
                if (t.Length > 0) texts.Add(t.Replace("\r\n", " ").Replace('\n', ' '));
            }
            return true;
        }, IntPtr.Zero);
        return new DialogInfo(dialog, NativeMethods.GetTextSafe(dialog), NativeMethods.GetClass(dialog),
            string.Join(" / ", texts), buttons);
    }

    /// <summary>
    /// Presses <paramref name="button"/> (caption, or "ok"/"cancel") on <paramref name="dialog"/> by posting WM_COMMAND,
    /// which works without focus (BM_CLICK does not). Returns the caption that was pressed.
    /// </summary>
    public static string Press(DialogInfo dialog, string button)
    {
        string want = button.Replace("&", "").Trim();
        var match = dialog.Buttons.FirstOrDefault(b => string.Equals(b.Caption.Trim(), want, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            Post(dialog.Handle, match.ControlId, match.Handle);
            return match.Caption;
        }
        if (want.Equals("ok", StringComparison.OrdinalIgnoreCase) || want.Equals("cancel", StringComparison.OrdinalIgnoreCase))
        {
            int id = want.Equals("ok", StringComparison.OrdinalIgnoreCase) ? NativeMethods.IDOK : NativeMethods.IDCANCEL;
            Post(dialog.Handle, id, IntPtr.Zero);
            return want.ToUpperInvariant();
        }
        throw new SwException(ErrorCodes.InvalidArgument,
            $"Dialog '{dialog.Title}' has no button '{button}'.",
            $"Buttons: {string.Join(", ", dialog.Buttons.Select(b => b.Caption))} (or use 'ok' / 'cancel').");
    }

    private static void Post(IntPtr dialog, int controlId, IntPtr buttonHandle)
    {
        // wParam: BN_CLICKED (0) in the high word, control id in the low word; lParam: the button window (or zero).
        if (!NativeMethods.PostMessage(dialog, NativeMethods.WM_COMMAND, (IntPtr)(controlId & 0xFFFF), buttonHandle))
            throw new SwException(ErrorCodes.Internal, "PostMessage to the dialog failed (it may have just closed).");
    }

    /// <summary>True when the window still exists and is visible.</summary>
    public static bool IsOpen(IntPtr handle) => NativeMethods.IsWindow(handle) && NativeMethods.IsWindowVisible(handle);
}
