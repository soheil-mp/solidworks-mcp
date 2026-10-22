using System.Diagnostics;
using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Files;
using SolidWorksMcp.Core.Prefs;
using SolidWorksMcp.Core.Win32;
using Xunit;

namespace SolidWorksMcp.UnitTests;

public sealed class PathPolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "swmcp-pathpolicy-" + Guid.NewGuid().ToString("N"));
    private readonly PathPolicy _p;

    public PathPolicyTests()
    {
        Directory.CreateDirectory(_root);
        _p = new PathPolicy(Path.Combine(_root, "out"), [_root], allowNetwork: false, backupDir: Path.Combine(_root, "backups"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Relative_paths_go_under_the_output_folder()
    {
        string full = _p.ForWrite(@"parts\bracket.step", overwrite: false, out _);
        Assert.Equal(Path.Combine(_root, "out", "parts", "bracket.step"), full);
        Assert.True(Directory.Exists(Path.GetDirectoryName(full))); // folders are created
    }

    [Theory]
    [InlineData(@"\\server\share\x.step")]
    [InlineData(@"\\?\C:\x.step")]
    [InlineData(@"C:\work\x.step:hidden")]
    [InlineData(@"C:\work\CON\x.step")]
    [InlineData(@"C:\work\nul.txt")]
    [InlineData(@"C:\work.\x.step")]
    [InlineData("")]
    public void Rejects_dangerous_paths(string path) =>
        Assert.Equal(ErrorCodes.InvalidArgument, Assert.Throws<SwException>(() => _p.ForWrite(path, true, out _)).Code);

    [Fact]
    public void Refuses_writes_outside_the_allowed_roots()
    {
        var ex = Assert.Throws<SwException>(() => _p.ForWrite(@"C:\Windows\x.step", true, out _));
        Assert.Contains("not allowed", ex.Message);
    }

    [Fact]
    public void Overwrite_needs_consent_and_backs_up_foreign_files()
    {
        string file = Path.Combine(_root, "user.step");
        File.WriteAllText(file, "user data");
        var refused = Assert.Throws<SwException>(() => _p.ForWrite(file, false, out _));
        Assert.Equal(ErrorCodes.IoError, refused.Code);

        _p.ForWrite(file, true, out var backup);
        Assert.NotNull(backup);
        Assert.Equal("user data", File.ReadAllText(backup!));

        _p.RecordWritten(file);
        _p.ForWrite(file, true, out var second);
        Assert.Null(second); // our own file: no backup needed
    }
}

public sealed class PreferenceJournalTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "swmcp-journal-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private static JournalEntry Entry(int pref, string old = "True", string now = "False") =>
        new(PrefKind.Toggle, pref, old, now, DateTime.UtcNow, Environment.ProcessId, "test", "33.1.2");

    [Fact]
    public void First_entry_wins_and_only_its_owner_removes_it()
    {
        var j = new PreferenceJournal(_dir);
        Assert.True(j.Add(Entry(10, "True", "False")));
        Assert.False(j.Add(Entry(10, "False", "True"))); // a nested change must not replace the user's original
        Assert.Equal("True", Assert.Single(j.Entries).OldValue);
        j.Remove(PrefKind.Toggle, 10);
        Assert.Empty(j.Entries);
        Assert.False(File.Exists(j.FilePath)); // empty journals are deleted
    }

    [Fact]
    public void Journals_of_dead_processes_are_orphans_and_live_ones_are_not()
    {
        Directory.CreateDirectory(_dir);
        // A process id that is certainly not running (and a start time that can't match).
        string dead = Path.Combine(_dir, "prefs-journal-999999-1.json");
        File.WriteAllText(dead, "[{\"Kind\":\"Toggle\",\"Pref\":10,\"OldValue\":\"True\",\"NewValue\":\"False\",\"SetAtUtc\":\"2026-01-01T00:00:00Z\",\"ServerPid\":999999,\"Operation\":\"x\"}]");
        using var me = Process.GetCurrentProcess();
        string alive = Path.Combine(_dir, $"prefs-journal-{Environment.ProcessId}-{me.StartTime.ToUniversalTime().Ticks}0.json");
        File.WriteAllText(alive, "[]");
        var j = new PreferenceJournal(_dir);
        var orphans = j.Orphans();
        var o = Assert.Single(orphans, x => x.File == dead);
        Assert.Equal(10, Assert.Single(o.Entries).Pref);
        j.ReplaceOrphan(dead, []);
        Assert.False(File.Exists(dead));
    }

    [Fact]
    public void A_corrupt_journal_is_kept_for_inspection()
    {
        var j = new PreferenceJournal(_dir);
        Directory.CreateDirectory(_dir);
        File.WriteAllText(j.FilePath, "{ not json");
        Assert.Empty(j.Entries);
        Assert.True(File.Exists(j.FilePath + ".corrupt"));
    }

    [Fact]
    public void Only_allow_listed_system_preferences_are_writable()
    {
        Assert.True(SystemPrefIds.IsAllowed(PrefKind.Toggle, SystemPrefIds.InputDimValOnCreate));
        Assert.True(SystemPrefIds.IsAllowed(PrefKind.Integer, SystemPrefIds.StepAp));
        Assert.False(SystemPrefIds.IsAllowed(PrefKind.Toggle, 5));   // swDisplayPlanes is a document toggle
        Assert.False(SystemPrefIds.IsAllowed(PrefKind.Integer, 99)); // background colour: never
    }
}

public class DialogRuleTests
{
    private static DialogInfo D(string title, string text, params string[] buttons) =>
        new(IntPtr.Zero, title, "#32770", text, buttons.Select((b, i) => new DialogButton(b, i + 1, true, IntPtr.Zero)).ToList());

    [Fact]
    public void Rules_match_title_and_text()
    {
        Assert.True(DialogRules.VbaRuntimeError.Matches(D("Microsoft Visual Basic", "Run-time error '11'", "Continue", "End", "Debug")));
        Assert.True(DialogRules.SimulationSaveNo.Matches(D("Simulation", "Do you want to save Simulation data?", "Yes", "No")));
        Assert.False(DialogRules.SimulationSaveNo.Matches(D("Simulation", "Mesh failed", "OK")));
        Assert.False(DialogRules.ModifyDimension.Matches(D("Modify Something", "")));
    }

    [Fact]
    public void Describe_is_compact()
    {
        var d = D("SOLIDWORKS", new string('x', 400), "OK");
        Assert.True(d.Describe().Length < 400);
        Assert.Contains("[buttons: OK]", d.Describe());
    }
}
