using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SolidWorksMcp.Core.Prefs;

/// <summary>Kinds of SOLIDWORKS system (application-level) user preferences.</summary>
public enum PrefKind { Toggle, Integer, Double, String }

/// <summary>One system preference the server changed and must put back.</summary>
public sealed record JournalEntry(
    PrefKind Kind,
    int Pref,
    string OldValue,
    string NewValue,
    DateTime SetAtUtc,
    int ServerPid,
    string Operation,
    string? SwRevision = null);

/// <summary>
/// Crash-recovery journal for system preferences, one file per server process
/// (<c>%LOCALAPPDATA%\solidworks-mcp\prefs-journal-&lt;pid&gt;-&lt;startTicks&gt;.json</c>). An entry is written
/// <em>before</em> a preference changes and removed once it is restored. Files left by dead processes are replayed
/// on every attach with compare-and-swap semantics: a value is put back only if it still equals what the dead
/// process set, so a deliberate later change by the user is never overwritten.
/// </summary>
public sealed class PreferenceJournal
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _gate = new();
    private readonly string _dir;
    private readonly string _path;

    public PreferenceJournal(string dataDir)
    {
        _dir = dataDir;
        using var me = Process.GetCurrentProcess();
        _path = Path.Combine(dataDir, $"prefs-journal-{Environment.ProcessId}-{me.StartTime.ToUniversalTime().Ticks}.json");
    }

    /// <summary>This process's journal file.</summary>
    public string FilePath => _path;

    /// <summary>This process's pending entries (non-empty only while a scope is open, or if a restore failed).</summary>
    public IReadOnlyList<JournalEntry> Entries
    {
        get { lock (_gate) return Load(_path); }
    }

    /// <summary>
    /// Records an entry. Returns false (and records nothing) when this process already journals the preference:
    /// the existing, older entry holds the user's original value, and only its owner may remove it.
    /// </summary>
    public bool Add(JournalEntry entry)
    {
        lock (_gate)
        {
            var all = Load(_path);
            if (all.Any(e => e.Kind == entry.Kind && e.Pref == entry.Pref)) return false;
            all.Add(entry);
            Save(_path, all);
            return true;
        }
    }

    /// <summary>
    /// Records the value this process left a preference at (after each write, side effects of coupled preferences
    /// included), so crash recovery's compare-and-swap recognises it as the server's.
    /// </summary>
    public void SetNewValue(PrefKind kind, int pref, string newValue)
    {
        lock (_gate)
        {
            var all = Load(_path);
            int i = all.FindIndex(e => e.Kind == kind && e.Pref == pref);
            if (i < 0 || all[i].NewValue == newValue) return;
            all[i] = all[i] with { NewValue = newValue };
            Save(_path, all);
        }
    }

    public void Remove(PrefKind kind, int pref)
    {
        lock (_gate)
        {
            var all = Load(_path);
            if (all.RemoveAll(e => e.Kind == kind && e.Pref == pref) > 0) Save(_path, all);
        }
    }

    /// <summary>Journal files of server processes that are no longer running, with their entries.</summary>
    public IReadOnlyList<(string File, IReadOnlyList<JournalEntry> Entries)> Orphans()
    {
        var result = new List<(string, IReadOnlyList<JournalEntry>)>();
        if (!Directory.Exists(_dir)) return result;
        foreach (var file in Directory.EnumerateFiles(_dir, "prefs-journal-*.json"))
        {
            if (string.Equals(file, _path, StringComparison.OrdinalIgnoreCase)) continue;
            if (OwnerAlive(file)) continue;
            result.Add((file, Load(file)));
        }
        return result;
    }

    /// <summary>Rewrites (or deletes when empty) an orphaned journal file after recovery.</summary>
    public void ReplaceOrphan(string file, IReadOnlyList<JournalEntry> remaining)
    {
        lock (_gate) Save(file, remaining.ToList());
    }

    private static bool OwnerAlive(string file)
    {
        // prefs-journal-<pid>-<startTicks>.json
        var parts = Path.GetFileNameWithoutExtension(file).Split('-');
        if (parts.Length < 4 || !int.TryParse(parts[2], out int pid) || !long.TryParse(parts[3], out long ticks)) return false;
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited && p.StartTime.ToUniversalTime().Ticks == ticks;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return true; } // can't inspect: assume alive, leave it alone
    }

    private static List<JournalEntry> Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return [];
            return JsonSerializer.Deserialize<List<JournalEntry>>(File.ReadAllText(path), Json) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Keep the evidence for a human; never silently discard originals.
            try { File.Copy(path, path + ".corrupt", overwrite: true); } catch { /* best effort */ }
            Trace.TraceWarning($"Preference journal {path} unreadable ({ex.Message}); copied to .corrupt.");
            return [];
        }
    }

    private static void Save(string path, List<JournalEntry> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (entries.Count == 0)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        string tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(fs, entries, Json);
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
    }
}
