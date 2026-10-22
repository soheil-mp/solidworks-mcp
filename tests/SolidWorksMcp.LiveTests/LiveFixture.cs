using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Services;
using Xunit;

[assembly: AssemblyFixture(typeof(SolidWorksMcp.LiveTests.LiveFixture))]
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]

namespace SolidWorksMcp.LiveTests;

/// <summary>
/// One SOLIDWORKS connection for the whole run (live tests only run with SWMCP_LIVE_TESTS=1). If live tests are enabled
/// and SOLIDWORKS can't be attached, initialization throws so every live test FAILS instead of silently skipping.
/// </summary>
public sealed class LiveFixture : IAsyncLifetime
{
    public static bool Enabled => Environment.GetEnvironmentVariable("SWMCP_LIVE_TESTS") == "1";
    public const string SkipReason = "Live test: set SWMCP_LIVE_TESTS=1 with SOLIDWORKS running (it creates and closes its own documents).";

    private SwSession? _session;
    private SwSession? _vba;

    /// <summary>The normal session (VBA off, like the default server).</summary>
    public SwSession Session => _session ?? throw new InvalidOperationException("Live tests are not enabled.");

    /// <summary>A second session with VBA on (documents created here belong to this session).</summary>
    public SwSession VbaSession => _vba ??= new SwSession(SwOptions.FromEnvironment() with { EnableVba = true });

    /// <summary>The user's documents at the start of the run: they must come out unchanged.</summary>
    public IReadOnlyList<DocInfo> UserDocsAtStart { get; private set; } = [];

    public async ValueTask InitializeAsync()
    {
        if (!Enabled) return;
        _session = new SwSession(SwOptions.FromEnvironment());
        await _session.ConnectAsync(launch: false);
        var docs = await new DocumentService(_session).ListAsync();
        if (docs.Documents.Any(d => d.Dirty) && Environment.GetEnvironmentVariable("SWMCP_TEST_ALLOW_USER_DOCS") != "1")
            throw new InvalidOperationException(
                "A document with unsaved changes is open in SOLIDWORKS. Save or close it first (or set SWMCP_TEST_ALLOW_USER_DOCS=1).");
        UserDocsAtStart = docs.Documents;
    }

    public async ValueTask DisposeAsync()
    {
        if (_session is null) return;
        var after = await new DocumentService(_session).ListAsync();
        _vba?.Dispose();
        _session.Dispose();
        foreach (var d in UserDocsAtStart)
        {
            var now = after.Documents.FirstOrDefault(x => x.Title == d.Title);
            if (now is null) throw new InvalidOperationException($"The user's document '{d.Title}' was closed by the test run.");
            if (now.Dirty && !d.Dirty) throw new InvalidOperationException($"The user's document '{d.Title}' was modified by the test run.");
        }
    }

    /// <summary>The allow-listed system preferences (the ones the server may touch), for before/after comparison.</summary>
    public Task<Dictionary<string, string>> PreferencesAsync() => TestHooks.PreferencesAsync(Session);
}

/// <summary>
/// Base for live tests: skips without SWMCP_LIVE_TESTS=1, and checks around every test that no allow-listed system
/// preference changed, no dialog was left open and no server-owned document leaked.
/// </summary>
public abstract class LiveTest(LiveFixture fx) : IAsyncLifetime
{
    protected LiveFixture Fx { get; } = fx;
    protected SwSession S => Fx.Session;
    private Dictionary<string, string>? _prefs;
    private readonly List<(SwSession Session, string Doc)> _docs = [];

    public virtual async ValueTask InitializeAsync()
    {
        Assert.SkipUnless(LiveFixture.Enabled, LiveFixture.SkipReason);
        _prefs = await Fx.PreferencesAsync();
    }

    public virtual async ValueTask DisposeAsync()
    {
        if (!LiveFixture.Enabled) return;
        foreach (var (session, doc) in _docs)
        {
            try { await new DocumentService(session).CloseAsync(doc, save: false, discard: true); }
            catch (SwException) { /* already closed by the test */ }
        }
        var prefs = await Fx.PreferencesAsync();
        Assert.Equal(_prefs, prefs);
        Assert.Empty(S.ListDialogs());
        Assert.Empty(S.Journal.Entries);
    }

    /// <summary>A new owned part (closed with discard after the test).</summary>
    protected async Task<string> NewPartAsync(string name, SwSession? session = null)
    {
        session ??= S;
        var r = await new DocumentService(session).NewAsync("part", "mmgs", null, $"{name}_{Guid.NewGuid().ToString("N")[..6]}");
        _docs.Add((session, r.Document.Handle));
        return r.Document.Handle;
    }
}
