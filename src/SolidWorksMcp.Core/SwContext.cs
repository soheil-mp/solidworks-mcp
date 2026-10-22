using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Documents;
using SolidWorksMcp.Core.Prefs;

namespace SolidWorksMcp.Core;

/// <summary>What a tool needs from the document it works on.</summary>
public enum DocAccess
{
    /// <summary>No document (session, listing, creation tools).</summary>
    None,
    /// <summary>Reads any document: the explicit one, else the server's target, else the active one.</summary>
    Read,
    /// <summary>Changes an owned or adopted document (never a foreign one); re-activates it if the user switched away.</summary>
    Write,
}

/// <summary>
/// What a service sees while its operation runs on the STA thread: the application, the session registries, the
/// bound target document, and helpers that turn common preconditions into typed errors. Never stored beyond one
/// operation.
/// </summary>
internal sealed class SwContext
{
    private IModelDoc2? _doc;

    public SwContext(SwSession session, ISldWorks app, OperationContext op)
    {
        Session = session;
        App = app;
        Op = op;
    }

    public SwSession Session { get; }
    public ISldWorks App { get; }
    public OperationContext Op { get; }
    public DocumentRegistry Documents => Session.Documents;
    public Geometry.EntityRegistry Entities => Session.Entities;
    public Files.PathPolicy Paths => Session.Paths;
    public DocAccess Access { get; private set; }

    public IModelDoc2? ActiveDocOrNull => App.Active();

    /// <summary>The document this operation is bound to (see <see cref="RunOptions.Access"/>).</summary>
    public IModelDoc2 Doc => _doc ?? throw new SwException(ErrorCodes.Internal,
        $"'{Op.Name}' did not declare document access (RunOptions.Access).");

    public DocumentRegistry.Entry DocEntry => Documents.Track(Doc);

    /// <summary>The bound document, or null when the call allowed running without one (<see cref="RunOptions.OptionalDoc"/>).</summary>
    public IModelDoc2? DocOrNull => _doc;

    /// <summary>Resolves and checks the target document. Called by <see cref="SwSession.RunAsync{T}"/>.</summary>
    internal void Bind(RunOptions options)
    {
        Access = options.Access;
        if (Access == DocAccess.None) return;

        var open = App.Documents();
        string? closedTarget = Documents.Target is not null && !open.Any(d => ReferenceEquals(d, Documents.Target))
            ? DocumentRegistry.SafeTitle(Documents.Target) : null;
        Documents.Prune(open);

        IModelDoc2 doc;
        if (!string.IsNullOrWhiteSpace(options.Doc)) doc = DocLocator.Find(Documents, open, options.Doc!);
        else if (Documents.Target is not null) doc = Documents.Target;
        else if (closedTarget is not null && Access == DocAccess.Write)
            throw new SwException(ErrorCodes.DocClosed, $"The document this session was working on ('{closedTarget}') was closed.",
                "Use doc_list, then doc_activate (or doc_new / doc_open) to choose the document to work on.");
        else if (ActiveDocOrNull is IModelDoc2 active0) doc = active0;
        else if (options.OptionalDoc && Access == DocAccess.Read) return;
        else throw new SwException(ErrorCodes.NoActiveDoc, "No document is open in SOLIDWORKS.",
            "Create one with doc_new or open one with doc_open.");

        var entry = Documents.Track(doc);
        if (Access == DocAccess.Write)
        {
            if (entry.Class == DocClass.Foreign)
                throw new SwException(ErrorCodes.NotOwned,
                    $"'{entry.Title}' ({entry.Handle}) is a document this server did not create or open, so it won't modify it.",
                    "If the user asked you to edit it, call doc_activate with allowEdit=true first (a backup copy is made). Otherwise create your own with doc_new.");
            CheckUserCommand();
            var active = ActiveDocOrNull;
            if (active is null || !DocLocator.SameDoc(active, doc))
            {
                int err = 0;
                App.ActivateDoc3(entry.Title, false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref err);
                var now = ActiveDocOrNull;
                if (now is null || !DocLocator.SameDoc(now, doc))
                    throw new SwException(ErrorCodes.VerifyFailed, $"Could not re-activate '{entry.Title}' (error {err}).");
                Warn($"Re-activated '{entry.Title}' (the active document was '{(active is null ? "none" : DocumentRegistry.SafeTitle(active))}').");
            }
            int stamp = doc.GetUpdateStamp();
            if (entry.StampAfterLastOp is int last && last != stamp)
                Warn($"'{entry.Title}' was modified outside this server since its last change (by the user?).");
        }
        _doc = doc;
    }

    /// <summary>After a successful write: remember the update stamp to detect edits by others.</summary>
    internal void AfterWrite()
    {
        if (Access != DocAccess.Write || _doc is null) return;
        try
        {
            var e = Documents.Find(_doc);
            if (e is not null) e.StampAfterLastOp = _doc.GetUpdateStamp();
        }
        catch { /* the document may have been closed by the operation */ }
    }

    /// <summary>USER_BUSY when the user is in the middle of a SOLIDWORKS command (a PropertyManager is open).</summary>
    public void CheckUserCommand()
    {
        App.GetRunningCommandInfo(out int commandId, out string pmTitle, out bool uiActive);
        if (!string.IsNullOrWhiteSpace(pmTitle))
            throw new SwException(ErrorCodes.UserBusy,
                $"SOLIDWORKS is running the command '{pmTitle}' (id {commandId}) — probably the user is working in it.",
                "Ask the user to finish or cancel that command, then retry. The server never cancels a user's command.");
    }

    /// <summary>The bound document as a part, or WRONG_DOC_TYPE.</summary>
    public IPartDoc Part(IModelDoc2? doc = null) => As<IPartDoc>(doc ?? Doc, swDocumentTypes_e.swDocPART);

    public IAssemblyDoc Assembly(IModelDoc2? doc = null) => As<IAssemblyDoc>(doc ?? Doc, swDocumentTypes_e.swDocASSEMBLY);

    public IDrawingDoc Drawing(IModelDoc2? doc = null) => As<IDrawingDoc>(doc ?? Doc, swDocumentTypes_e.swDocDRAWING);

    /// <summary>Throws WRONG_DOC_TYPE unless the bound document is one of <paramref name="types"/>.</summary>
    public IModelDoc2 DocOfType(params swDocumentTypes_e[] types)
    {
        var doc = Doc;
        int t = doc.GetType();
        if (types.Any(x => (int)x == t)) return doc;
        throw WrongType(doc, string.Join(" or ", types.Select(TypeName)));
    }

    private static T As<T>(IModelDoc2 doc, swDocumentTypes_e type) where T : class
    {
        if (doc.GetType() == (int)type && doc is T typed) return typed;
        throw WrongType(doc, TypeName(type));
    }

    private static SwException WrongType(IModelDoc2 doc, string expected) => new(ErrorCodes.WrongDocType,
        $"This tool needs a {expected}, but '{DocumentRegistry.SafeTitle(doc)}' is a {TypeName((swDocumentTypes_e)doc.GetType())}.",
        "Switch with doc_activate (doc_list shows what is open), or pass 'doc'.");

    public static string TypeName(swDocumentTypes_e t) => t switch
    {
        swDocumentTypes_e.swDocPART => "part",
        swDocumentTypes_e.swDocASSEMBLY => "assembly",
        swDocumentTypes_e.swDocDRAWING => "drawing",
        _ => "document",
    };

    public static string TypeName(int t) => TypeName((swDocumentTypes_e)t);

    /// <summary>A preference scope for system options; restore problems become warnings of this operation.</summary>
    public PreferenceScope SystemPrefs() => new(App, Session.Journal, Op.Name, Session.Revision, Op.AddWarning);

    public void Warn(string warning) => Op.AddWarning(warning);

    public IMathUtility Math => App.MathUtil();

    public string Title(IModelDoc2 doc) => DocumentRegistry.SafeTitle(doc);
}
