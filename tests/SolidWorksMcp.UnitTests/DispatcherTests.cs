using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Com;
using Xunit;

namespace SolidWorksMcp.UnitTests;

/// <summary>The STA dispatcher's queueing, BUSY, TIMEOUT, late-completion and cancellation rules (no COM involved).</summary>
public class DispatcherTests
{
    private static StaDispatcher New(TimeSpan? queueWait = null) =>
        new("test STA", mutexName: null) { MaxQueueWait = queueWait ?? TimeSpan.FromSeconds(5), DialogGrace = TimeSpan.FromSeconds(30) };

    [Fact]
    public async Task Runs_work_on_an_sta_thread_and_returns_the_result()
    {
        using var d = New();
        var apt = await d.InvokeAsync("probe", _ => Thread.CurrentThread.GetApartmentState(), TimeSpan.FromSeconds(5));
        Assert.Equal(ApartmentState.STA, apt);
        Assert.Equal("probe", d.Last!.Name);
        Assert.Equal("completed", d.Last.Status);
    }

    [Fact]
    public async Task Exceptions_propagate_and_are_recorded()
    {
        using var d = New();
        var ex = await Assert.ThrowsAsync<SwException>(() =>
            d.InvokeAsync<int>("bad", _ => throw new SwException(ErrorCodes.InvalidArgument, "nope"), TimeSpan.FromSeconds(5)));
        Assert.Equal(ErrorCodes.InvalidArgument, ex.Code);
        Assert.Equal("failed", d.Last!.Status);
    }

    [Fact]
    public async Task A_call_that_cannot_start_is_dropped_with_BUSY_and_never_runs()
    {
        using var d = New(queueWait: TimeSpan.FromMilliseconds(300));
        var release = new ManualResetEventSlim();
        var blocker = d.InvokeAsync("blocker", _ => { release.Wait(); return 1; }, TimeSpan.FromSeconds(30));
        bool ran = false;
        var ex = await Assert.ThrowsAsync<SwException>(() => d.InvokeAsync("queued", _ => { ran = true; return 2; }, TimeSpan.FromSeconds(30)));
        Assert.Equal(ErrorCodes.Busy, ex.Code);
        Assert.Contains("blocker", ex.Message);
        release.Set();
        Assert.Equal(1, await blocker);
        await d.InvokeAsync("after", _ => 0, TimeSpan.FromSeconds(5));
        Assert.False(ran); // dropped work must never run later
    }

    [Fact]
    public async Task An_overrunning_call_times_out_and_its_outcome_is_recorded_later()
    {
        using var d = New();
        var release = new ManualResetEventSlim();
        var ex = await Assert.ThrowsAsync<SwException>(() =>
            d.InvokeAsync("slow", _ => { release.Wait(); return 1; }, TimeSpan.FromMilliseconds(300)));
        Assert.Equal(ErrorCodes.Timeout, ex.Code);
        Assert.Contains("may still complete", ex.Message);
        release.Set();
        await d.InvokeAsync("next", _ => 0, TimeSpan.FromSeconds(5)); // runs after 'slow' finished
        var late = Assert.Single(d.LateCompletions);
        Assert.Equal("slow", late.Name);
        Assert.True(late.Succeeded);
        Assert.Contains(d.Recent, r => r.Name == "slow" && r.Status.Contains("after the caller gave up"));
    }

    [Fact]
    public async Task Cancelling_a_queued_call_drops_it()
    {
        using var d = New();
        var release = new ManualResetEventSlim();
        var blocker = d.InvokeAsync("blocker", _ => { release.Wait(); return 1; }, TimeSpan.FromSeconds(30));
        using var cts = new CancellationTokenSource(200);
        bool ran = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            d.InvokeAsync("cancelled", _ => { ran = true; return 2; }, TimeSpan.FromSeconds(30), null, cts.Token));
        release.Set();
        await blocker;
        await d.InvokeAsync("after", _ => 0, TimeSpan.FromSeconds(5));
        Assert.False(ran);
    }

    [Fact]
    public async Task Nested_calls_run_inline_without_deadlock()
    {
        using var d = New();
        int v = await d.InvokeAsync("outer", _ => d.InvokeAsync("inner", __ => 41, TimeSpan.FromSeconds(1)).Result + 1, TimeSpan.FromSeconds(5));
        Assert.Equal(42, v);
    }

    [Fact]
    public async Task Recent_keeps_the_last_ten()
    {
        using var d = New();
        for (int i = 0; i < 12; i++) await d.InvokeAsync($"op{i}", _ => i, TimeSpan.FromSeconds(5));
        Assert.Equal(10, d.Recent.Count);
        Assert.Equal("op11", d.Recent[^1].Name);
    }
}
