using SolidWorks.Interop.sldworks;

namespace SolidWorksMcp.Core.Documents;

/// <summary>Finds open documents by handle (D2), title (with or without extension), file name or full path.</summary>
internal static class DocLocator
{
    public static IModelDoc2 Find(DocumentRegistry registry, IReadOnlyList<IModelDoc2> open, string spec)
    {
        string want = spec.Trim();
        if (want.Length > 1 && (want[0] == 'D' || want[0] == 'd') && int.TryParse(want.AsSpan(1), out _))
        {
            var e = registry.FindHandle(want);
            var live = e is null ? null : open.FirstOrDefault(d => ReferenceEquals(d, e.Doc) || SameDoc(d, e.Doc));
            if (live is not null) return live;
            throw new SwException(ErrorCodes.DocClosed, $"Document {want}{(e is null ? "" : $" ('{e.Title}')")} is not open any more.",
                "doc_list shows the open documents.");
        }
        var matches = open.Where(d => string.Equals(DocumentRegistry.SafeTitle(d), want, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0)
            matches = open.Where(d => string.Equals(DocumentRegistry.SafePath(d), want, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0)
            matches = open.Where(d => string.Equals(Path.GetFileName(DocumentRegistry.SafePath(d)), want, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0)
            matches = open.Where(d => string.Equals(Path.GetFileNameWithoutExtension(DocumentRegistry.SafeTitle(d)),
                Path.GetFileNameWithoutExtension(want), StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 1) return matches[0];
        if (matches.Count > 1)
            throw new SwException(ErrorCodes.EntityAmbiguous,
                $"'{spec}' matches {matches.Count} open documents: {string.Join(", ", matches.Select(d => $"{registry.Track(d).Handle} {DocumentRegistry.SafePath(d)}"))}.",
                "Pass the handle (D…) or the full path.");
        throw new SwException(ErrorCodes.InvalidArgument, $"No open document matches '{spec}'.",
            $"Open documents: {string.Join(", ", open.Select(d => $"{registry.Track(d).Handle} '{DocumentRegistry.SafeTitle(d)}'"))}.");
    }

    public static bool SameDoc(IModelDoc2 a, IModelDoc2 b)
    {
        if (ReferenceEquals(a, b)) return true;
        string pa = DocumentRegistry.SafePath(a), pb = DocumentRegistry.SafePath(b);
        if (pa.Length > 0 || pb.Length > 0) return string.Equals(pa, pb, StringComparison.OrdinalIgnoreCase);
        return string.Equals(DocumentRegistry.SafeTitle(a), DocumentRegistry.SafeTitle(b), StringComparison.Ordinal);
    }
}
