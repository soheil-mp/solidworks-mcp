using System.Collections.Concurrent;
using System.Diagnostics;
using SolidWorksMcp.Core.Win32;

namespace SolidWorksMcp.Core.Com;

/// <summary>Snapshot of the operation the STA thread is running.</summary>
public sealed record OperationSnapshot(string Name, DateTime StartedUtc, double ElapsedS, int QueueLength);

/// <summary>An operation that timed out for its caller but kept running and finished later.</summary>
public sealed record LateCompletion(string Name, DateTime FinishedUtc, double DurationS, bool Succeeded, string? Error);

/// <summary>The most recent operation and how it ended (sw_status shows it, e.g. after a TIMEOUT).</summary>
public sealed record LastOperation(string Name, DateTime StartedUtc, double? DurationS, string Status, string? Error);

/// <summary>
/// Runs every SolidWorks COM call on one dedicated STA thread (SolidWorks objects are apartment-bound, and an
/// out-of-process STA server is happiest with a single caller). Calls are queued, timed, and reported:
/// <list type="bullet">
/// <item>A call that can't start within its queue wait (another call is running, or another solidworks-mcp process
/// holds the SolidWorks lock) is dropped and fails with <c>BUSY</c>, so it can never run later by surprise.</item>
/// <item>A call that started but didn't finish in time fails with <c>TIMEOUT</c>; it keeps running (COM calls can't be
/// aborted) and its eventual outcome is recorded in <see cref="LateCompletions"/> and <see cref="Last"/>.</item>
/// </list>
/// Each operation holds the machine-wide mutex <c>Local\solidworks-mcp-com</c>, so several server processes (several
/// Claude sessions, or the tests) never interleave their calls into one SOLIDWORKS.
/// </summary>
public sealed class StaDispatcher : IDisposable
{
    public const string MutexName = @"Local\solidworks-mcp-com";

    private readonly BlockingCollection<WorkItem> _queue = new();
    private readonly Thread _thread;
    private readonly ConcurrentQueue<LateCompletion> _late = new();
    private volatile WorkItem? _current;
    private volatile LastOperation? _last;
    private int _threadId;
    private Mutex? _mutex;

    private readonly string? _mutexName;

    /// <param name="mutexName">Cross-process lock name (null = none; tests use their own).</param>
    public StaDispatcher(string threadName = "SolidWorks STA", string? mutexName = MutexName)
    {
        _mutexName = mutexName;
        var ready = new ManualResetEventSlim();
        _thread = new Thread(() => Run(ready)) { IsBackground = true, Name = threadName };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait();
    }

    /// <summary>Longest time a call waits for its turn before failing with BUSY (SWMCP_QUEUE_WAIT_S, default 20 s).</summary>
    public TimeSpan MaxQueueWait { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>The operation currently executing on the STA thread, or null when idle.</summary>
    public OperationSnapshot? Current
    {
        get
        {
            var c = _current;
            if (c is null || c.StartedUtc is null) return null;
            return new OperationSnapshot(c.Name, c.StartedUtc.Value, (DateTime.UtcNow - c.StartedUtc.Value).TotalSeconds,
                _queue.Count);
        }
    }

    /// <summary>The <see cref="OperationContext"/> of the running operation (null when idle).</summary>
    internal OperationContext? CurrentContext => _current?.Context;

    public int QueueLength => _queue.Count;

    public bool IsDispatcherThread => Environment.CurrentManagedThreadId == _threadId;

    /// <summary>Operations that timed out for their caller and finished afterwards (most recent last, max 20 kept).</summary>
    public IReadOnlyList<LateCompletion> LateCompletions => _late.ToArray();

    /// <summary>The last operation that started (running, completed or failed).</summary>
    public LastOperation? Last => _last;

    /// <summary>The last 10 operations (oldest first), for sw_status.recentOps.</summary>
    public IReadOnlyList<LastOperation> Recent
    {
        get { lock (_recent) return _recent.ToArray(); }
    }

    private readonly Queue<LastOperation> _recent = new();

    /// <summary>
    /// How long an unexpected modal dialog may stay up during an operation before the caller gets DIALOG_BLOCKING
    /// (instead of waiting for the full timeout). The operation itself keeps running, like after a timeout.
    /// </summary>
    public TimeSpan DialogGrace { get; set; } = TimeSpan.FromSeconds(2.5);

    /// <summary>Returns the unexpected modal dialogs currently blocking the running operation (set by the session).</summary>
    internal Func<OperationContext, IReadOnlyList<DialogInfo>>? BlockingDialogProbe { get; set; }

    /// <summary>
    /// Queue <paramref name="work"/> on the STA thread. It must start within <see cref="MaxQueueWait"/> (else BUSY) and
    /// finish within <paramref name="timeout"/> of starting (else TIMEOUT). Re-entrant: when called on the STA thread
    /// itself the work runs inline.
    /// </summary>
    internal Task<T> InvokeAsync<T>(string name, Func<OperationContext, T> work, TimeSpan timeout,
        IReadOnlyList<DialogRule>? dialogRules = null, CancellationToken cancellationToken = default)
    {
        if (IsDispatcherThread)
        {
            // Nested call from inside an operation: run inline with the outer context (no new deadline).
            var outer = _current?.Context ?? new OperationContext(name, dialogRules);
            try { return Task.FromResult(work(outer)); }
            catch (Exception ex) { return Task.FromException<T>(ex); }
        }
        if (_queue.IsAddingCompleted) throw new ObjectDisposedException(nameof(StaDispatcher));

        var startBy = DateTime.UtcNow + MaxQueueWait;
        var item = new WorkItem<T>(name, work, new OperationContext(name, dialogRules), startBy);
        _queue.Add(item, CancellationToken.None);
        return AwaitWithDeadline(item, timeout, startBy, cancellationToken);
    }

    private async Task<T> AwaitWithDeadline<T>(WorkItem<T> item, TimeSpan timeout, DateTime startBy, CancellationToken ct)
    {
        // Phase 1: wait for the item to start (or to finish quickly).
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            var wait = Max(startBy - DateTime.UtcNow, TimeSpan.Zero);
            await Task.WhenAny(item.Started, item.Task, Task.Delay(wait, cts.Token)).ConfigureAwait(false);
            cts.Cancel();
        }
        if (!item.Started.IsCompleted && item.TryCancelQueued())
        {
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            var cur = Current;
            string running = cur is null ? "another operation" : $"'{cur.Name}' (running for {cur.ElapsedS:0} s)";
            throw new SwException(ErrorCodes.Busy,
                $"'{item.Name}' could not start within {(startBy - item.EnqueuedUtc).TotalSeconds:0} s because {running} is still in progress.",
                "The request was dropped and will not run. Call sw_status / dialog_list (a dialog may be blocking SOLIDWORKS), then retry.");
        }

        // Phase 2: wait for completion until start + timeout, checking for an unexpected modal dialog meanwhile.
        var deadline = (item.StartedUtc ?? DateTime.UtcNow) + timeout;
        DateTime? blockedSince = null;
        IReadOnlyList<DialogInfo> blocking = [];
        while (true)
        {
            var wait = Min(Max(deadline - DateTime.UtcNow, TimeSpan.Zero), TimeSpan.FromMilliseconds(500));
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                var finished = await Task.WhenAny(item.Task, Task.Delay(wait, cts.Token)).ConfigureAwait(false);
                cts.Cancel();
                if (finished == item.Task) return await item.Task.ConfigureAwait(false);
            }
            if (ct.IsCancellationRequested || DateTime.UtcNow >= deadline) break;
            blocking = BlockingDialogProbe?.Invoke(item.Context) ?? [];
            if (blocking.Count == 0) { blockedSince = null; continue; }
            blockedSince ??= DateTime.UtcNow;
            if (DateTime.UtcNow - blockedSince.Value >= DialogGrace) break;
        }

        // Started but not finished: abandon (it keeps running) and report its eventual outcome as a late completion.
        if (!item.TryAbandon(RecordLate)) return await item.Task.ConfigureAwait(false); // finished in the meantime
        if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
        double elapsed = item.StartedUtc is null ? 0 : (DateTime.UtcNow - item.StartedUtc.Value).TotalSeconds;
        if (blocking.Count > 0 && DateTime.UtcNow < deadline)
            throw new SwException(ErrorCodes.DialogBlocking,
                $"'{item.Name}' is waiting on a SOLIDWORKS dialog: {string.Join("; ", blocking.Select(d => $"{d.HandleHex} {d.Describe()}"))}. " +
                "The command is still pending inside SOLIDWORKS and will continue once the dialog is answered.",
                "Tell the user what the dialog asks. Answer it with dialog_respond (handle) only if the answer is clearly safe " +
                "(e.g. OK on an error message); then check sw_status (recentOps) and the model before continuing.");
        throw new SwException(ErrorCodes.Timeout,
            $"'{item.Name}' has been running for {elapsed:0} s and exceeded its {timeout.TotalSeconds:0} s limit. " +
            "SOLIDWORKS is still executing it and it may still complete — do not simply retry." +
            item.Context.DescribeSeenDialogs(),
            "Check dialog_list for a blocking dialog (ask the user before answering one), then sw_status (lastOperation) and the model state before doing anything else.");
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private void RecordLate(LateCompletion late)
    {
        _late.Enqueue(late);
        while (_late.Count > 20 && _late.TryDequeue(out _)) { }
    }

    private void Run(ManualResetEventSlim ready)
    {
        _threadId = Environment.CurrentManagedThreadId;
        try { OleMessageFilter.Register(); }
        catch (Exception ex) { Trace.TraceWarning($"OLE message filter not registered: {ex.Message}"); }
        try { if (_mutexName is not null) _mutex = new Mutex(false, _mutexName); }
        catch (Exception ex) { Trace.TraceWarning($"Cross-process lock unavailable: {ex.Message}"); }
        ready.Set();

        foreach (var item in _queue.GetConsumingEnumerable())
        {
            if (!item.TryStart()) continue; // dropped while queued
            _current = item;
            _last = new LastOperation(item.Name, item.StartedUtc!.Value, null, "running", null);
            bool locked = false;
            var started = DateTime.UtcNow;
            OleMessageFilter.ShouldStop = () => item.Abandoned;
            try
            {
                locked = AcquireLock(item);
                if (locked) item.Execute();
            }
            finally
            {
                OleMessageFilter.ShouldStop = null;
                if (locked) _mutex?.ReleaseMutex();
                _current = null;
                // Release the transient RCWs (faces, edges, features…) the operation created, so SOLIDWORKS can free
                // the API objects they pin. Cheap for small heaps; only after operations that did real work.
                if (DateTime.UtcNow - started > TimeSpan.FromMilliseconds(100))
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                }
                string status = (item.Succeeded ? "completed" : "failed") + (item.Abandoned ? " (after the caller gave up)" : "");
                _last = new LastOperation(item.Name, item.StartedUtc!.Value,
                    Math.Round((DateTime.UtcNow - item.StartedUtc!.Value).TotalSeconds, 2), status, item.Error);
                lock (_recent)
                {
                    _recent.Enqueue(_last);
                    while (_recent.Count > 10) _recent.Dequeue();
                }
            }
        }
        OleMessageFilter.Revoke();
        _mutex?.Dispose();
    }

    /// <summary>Takes the machine-wide SolidWorks lock, waiting at most until the item's start deadline.</summary>
    private bool AcquireLock(WorkItem item)
    {
        if (_mutex is null) return true;
        var wait = Max(item.StartBy - DateTime.UtcNow, TimeSpan.FromMilliseconds(50));
        try
        {
            if (_mutex.WaitOne(wait)) return true;
        }
        catch (AbandonedMutexException)
        {
            return true; // a process died holding it: we own it now
        }
        item.Fail(new SwException(ErrorCodes.Busy,
            "Another solidworks-mcp process (another Claude session, or the test suite) is using SOLIDWORKS right now.",
            "Retry in a moment. Running two agents against one SOLIDWORKS at the same time is not recommended."));
        return false;
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        if (!IsDispatcherThread) _thread.Join(TimeSpan.FromSeconds(5));
    }

    private abstract class WorkItem
    {
        private readonly object _gate = new();
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _state; // 0 queued, 1 running, 2 finished, 3 dropped
        private Action<LateCompletion>? _onLate;

        protected WorkItem(string name, OperationContext context, DateTime startBy)
        {
            Name = name;
            Context = context;
            StartBy = startBy;
        }

        public string Name { get; }
        public OperationContext Context { get; }
        public DateTime EnqueuedUtc { get; } = DateTime.UtcNow;
        public DateTime StartBy { get; }
        public DateTime? StartedUtc { get; private set; }
        public Task Started => _started.Task;
        public bool Succeeded { get; protected set; }
        public string? Error { get; protected set; }

        public bool TryStart()
        {
            if (Interlocked.CompareExchange(ref _state, 1, 0) != 0) return false;
            StartedUtc = DateTime.UtcNow;
            Context.StartedUtc = StartedUtc.Value;
            _started.TrySetResult();
            return true;
        }

        public bool TryCancelQueued() => Interlocked.CompareExchange(ref _state, 3, 0) == 0;

        /// <summary>Marks a running item as abandoned by its caller; false if it already finished.</summary>
        public bool TryAbandon(Action<LateCompletion> onLate)
        {
            lock (_gate)
            {
                if (_state == 2) return false;
                _onLate = onLate;
                return true;
            }
        }

        public bool Abandoned
        {
            get { lock (_gate) return _onLate is not null; }
        }

        protected void Finish(bool ok, string? error)
        {
            Succeeded = ok;
            Error = error;
            Action<LateCompletion>? cb;
            lock (_gate)
            {
                _state = 2;
                cb = _onLate;
            }
            if (cb is not null && StartedUtc is not null)
                cb(new LateCompletion(Name, DateTime.UtcNow, (DateTime.UtcNow - StartedUtc.Value).TotalSeconds, ok, error));
        }

        public abstract void Execute();
        public abstract void Fail(Exception ex);
    }

    private sealed class WorkItem<T> : WorkItem
    {
        private readonly Func<OperationContext, T> _work;
        private readonly TaskCompletionSource<T> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public WorkItem(string name, Func<OperationContext, T> work, OperationContext context, DateTime startBy)
            : base(name, context, startBy) => _work = work;

        public Task<T> Task => _tcs.Task;

        public override void Execute()
        {
            try
            {
                var result = _work(Context);
                _tcs.TrySetResult(result);
                Finish(true, null);
            }
            catch (Exception ex)
            {
                Fail(ex);
            }
        }

        public override void Fail(Exception ex)
        {
            _tcs.TrySetException(ex);
            Finish(false, ex is SwException sw ? sw.ToAgentText() : ex.Message);
        }
    }
}
