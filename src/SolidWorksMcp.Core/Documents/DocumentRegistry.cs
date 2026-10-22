using System.Text.Json;
using System.Text.Json.Serialization;
using SolidWorks.Interop.sldworks;

namespace SolidWorksMcp.Core.Documents;

/// <summary>Write permission class of an open document (design §3.6).</summary>
public enum DocClass
{
    /// <summary>Created, imported, or opened (not already open) by this server: full access.</summary>
    Owned,
    /// <summary>A user document the agent was explicitly allowed to edit (doc_activate allowEdit=true): edit, never discard.</summary>
    Adopted,
    /// <summary>Everything else: read-only for the server.</summary>
    Foreign,
}

/// <summary>
/// Tracks the documents this server knows: their handle (D1, D2, …), write class, the server's current target, the
/// update stamp after the server's last change (to detect edits by someone else) and the sketch the server opened.
/// Persisted per SOLIDWORKS session so a restarted server re-claims what it created. STA thread only.
/// </summary>
internal sealed class DocumentRegistry
{
    private readonly List<Entry> _entries = [];
    private int _next;
    private string? _file;

    internal sealed class Entry
    {
        public required IModelDoc2 Doc { get; set; }
        public required string Handle { get; init; }
        public string Title { get; set; } = "";
        public string Path { get; set; } = "";
        public DocClass Class { get; set; }
        public string How { get; set; } = "";
        public DateTime SinceUtc { get; init; } = DateTime.UtcNow;
        public int? StampAfterLastOp { get; set; }
        /// <summary>doc_undo's limit: server writes that changed the document since anyone else last changed it.</summary>
        public int ServerWrites { get; set; }
        /// <summary>The update stamp right after the last counted server write (the count holds only while it matches).</summary>
        public int? ServerWritesStamp { get; set; }
        /// <summary>The sketch the server opened and is editing (null when none).</summary>
        public string? ServerSketch { get; set; }
        /// <summary>The last sketch the server finished in this document (feature tools default to it).</summary>
        public string? LastSketch { get; set; }
        public string? BackupPath { get; set; }
    }

    /// <summary>The document server operations act on by default (set by doc_new / doc_open / doc_activate).</summary>
    public IModelDoc2? Target { get; set; }

    public IReadOnlyList<Entry> Entries => _entries;

    /// <summary>Returns (creating if needed) the entry for <paramref name="doc"/>; new entries are Foreign.</summary>
    public Entry Track(IModelDoc2 doc)
    {
        var e = Find(doc);
        if (e is not null)
        {
            e.Doc = doc;
            e.Title = SafeTitle(doc);
            e.Path = SafePath(doc);
            return e;
        }
        e = new Entry { Doc = doc, Handle = $"D{++_next}", Title = SafeTitle(doc), Path = SafePath(doc), Class = DocClass.Foreign, How = "seen" };
        _entries.Add(e);
        return e;
    }

    public void SetClass(IModelDoc2 doc, DocClass cls, string how)
    {
        var e = Track(doc);
        // Never downgrade Owned to Adopted.
        if (!(e.Class == DocClass.Owned && cls == DocClass.Adopted)) e.Class = cls;
        e.How = how;
        Persist();
    }

    public DocClass ClassOf(IModelDoc2 doc) => Find(doc)?.Class ?? DocClass.Foreign;

    public bool IsOwned(IModelDoc2 doc) => ClassOf(doc) == DocClass.Owned;

    public Entry? Find(IModelDoc2 doc)
    {
        foreach (var e in _entries)
            if (ReferenceEquals(e.Doc, doc)) return e;
        string path = SafePath(doc);
        if (path.Length > 0)
        {
            foreach (var e in _entries)
                if (string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase)) return e;
            return null;
        }
        // Untitled document: titles are unique within a SOLIDWORKS session.
        string title = SafeTitle(doc);
        foreach (var e in _entries)
            if (e.Path.Length == 0 && title.Length > 0 && string.Equals(e.Title, title, StringComparison.Ordinal)) return e;
        return null;
    }

    public Entry? FindHandle(string handle) =>
        _entries.FirstOrDefault(e => string.Equals(e.Handle, handle.Trim(), StringComparison.OrdinalIgnoreCase));

    public void Remove(IModelDoc2 doc)
    {
        var e = Find(doc);
        if (e is null) return;
        _entries.Remove(e);
        if (Target is not null && ReferenceEquals(Target, doc)) Target = null;
        Persist();
    }

    public void Clear()
    {
        _entries.Clear();
        Target = null;
        _file = null;
    }

    /// <summary>Drops entries of documents that are no longer open (and refreshes titles/paths of the rest).</summary>
    public void Prune(IReadOnlyCollection<IModelDoc2> openDocs)
    {
        int before = _entries.Count;
        _entries.RemoveAll(e => !openDocs.Any(d => ReferenceEquals(d, e.Doc) || Same(d, e)));
        foreach (var e in _entries)
        {
            var live = openDocs.FirstOrDefault(d => ReferenceEquals(d, e.Doc)) ?? openDocs.First(d => Same(d, e));
            e.Doc = live;
            e.Title = SafeTitle(live);
            e.Path = SafePath(live);
        }
        if (Target is not null && !openDocs.Any(d => ReferenceEquals(d, Target))) Target = null;
        if (_entries.Count != before) Persist();
    }

    private static bool Same(IModelDoc2 d, Entry e)
    {
        string p = SafePath(d);
        if (p.Length > 0 || e.Path.Length > 0) return string.Equals(p, e.Path, StringComparison.OrdinalIgnoreCase);
        return string.Equals(SafeTitle(d), e.Title, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ persistence

    private sealed record Saved(string Handle, string Title, string Path, DocClass Class, string How, string? BackupPath);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    /// <summary>Binds the registry to one SOLIDWORKS session and re-claims documents recorded by an earlier server.</summary>
    public void Load(string dataDir, int swPid, DateTime swStartUtc, IReadOnlyCollection<IModelDoc2> openDocs)
    {
        _file = System.IO.Path.Combine(dataDir, $"registry-{swPid}-{swStartUtc.Ticks}.json");
        if (!File.Exists(_file)) return;
        try
        {
            var saved = JsonSerializer.Deserialize<List<Saved>>(File.ReadAllText(_file), Json) ?? [];
            foreach (var s in saved)
            {
                var doc = openDocs.FirstOrDefault(d =>
                    s.Path.Length > 0 ? string.Equals(SafePath(d), s.Path, StringComparison.OrdinalIgnoreCase)
                                      : SafePath(d).Length == 0 && string.Equals(SafeTitle(d), s.Title, StringComparison.Ordinal));
                if (doc is null || s.Class == DocClass.Foreign) continue;
                var e = Track(doc);
                e.Class = s.Class;
                e.How = s.How + " (re-claimed)";
                e.BackupPath = s.BackupPath;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException) { /* start fresh */ }
    }

    private void Persist()
    {
        if (_file is null) return;
        try
        {
            var data = _entries.Where(e => e.Class != DocClass.Foreign)
                .Select(e => new Saved(e.Handle, e.Title, e.Path, e.Class, e.How, e.BackupPath)).ToList();
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file + ".tmp", JsonSerializer.Serialize(data, Json));
            File.Move(_file + ".tmp", _file, overwrite: true);
        }
        catch (IOException) { /* best effort */ }
    }

    // ------------------------------------------------------------------ safe accessors

    internal static string SafeTitle(IModelDoc2 doc)
    {
        try { return doc.GetTitle() ?? ""; }
        catch { return ""; }
    }

    internal static string SafePath(IModelDoc2 doc)
    {
        try { return doc.GetPathName() ?? ""; }
        catch { return ""; }
    }
}
