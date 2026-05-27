using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Sessions;
using Shouldly;

namespace BotNexus.Gateway.Tests.Sessions;

/// <summary>
/// Unit tests for <see cref="SessionWriteLock"/> — the per-session mutex that closes
/// the cross-world relay race window pinned by issue #551 (PR #550 bug-hunt critique
/// HIGH-1 and MEDIUM-2). Each test pins a property of the locking primitive that the
/// concurrency-bug fix at the call sites depends on.
/// </summary>
public sealed class SessionWriteLockTests
{
    [Fact]
    public async Task AcquireAsync_SameSessionId_SerializesConcurrentCallers()
    {
        // The whole point: two callers on the same session id must NOT run their
        // critical sections at the same time. A regression to a no-op lock would
        // let both progress past the WaitAsync and observe interleaved state.
        var sut = new SessionWriteLock();
        var sessionId = SessionId.Create();
        var enteredOrder = new List<int>();
        var releaseA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Acquire A synchronously FIRST so the ordering is deterministic — without
        // this, the first scheduled Task.Run could be either A or B and the test
        // would be racy on who acquires the lock first.
        var leaseA = await sut.AcquireAsync(sessionId);
        lock (enteredOrder) enteredOrder.Add(1);

        // Start B; it must block in WaitAsync until A releases.
        var b = Task.Run(async () =>
        {
            await using var lease = await sut.AcquireAsync(sessionId);
            lock (enteredOrder) enteredOrder.Add(2);
            releaseA.Task.IsCompleted.ShouldBeTrue(
                "Caller B entered the critical section while caller A's lease was still held; " +
                "SessionWriteLock did not serialize concurrent acquires on the same SessionId.");
        });

        // Give B time to attempt and block in WaitAsync.
        await Task.Delay(75);
        b.IsCompleted.ShouldBeFalse(
            "Caller B completed before A released its lease — SessionWriteLock did not block B.");

        releaseA.SetResult();
        await leaseA.DisposeAsync();
        await b.WaitAsync(TimeSpan.FromSeconds(2));

        enteredOrder.ShouldBe(new[] { 1, 2 });
    }

    [Fact]
    public async Task AcquireAsync_DifferentSessionIds_RunInParallel()
    {
        // Locks keyed by different SessionIds must not serialize each other —
        // otherwise the lock becomes a global gateway-wide bottleneck and any
        // long-running cross-world prompt blocks unrelated sessions.
        var sut = new SessionWriteLock();
        var sessionA = SessionId.Create();
        var sessionB = SessionId.Create();
        var bothEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entries = 0;

        async Task AcquireAndWait(SessionId id)
        {
            await using var lease = await sut.AcquireAsync(id);
            if (Interlocked.Increment(ref entries) == 2)
                bothEntered.SetResult();
            // Park until both callers are inside their critical sections.
            await bothEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }

        var taskA = AcquireAndWait(sessionA);
        var taskB = AcquireAndWait(sessionB);

        await Task.WhenAll(taskA, taskB).WaitAsync(TimeSpan.FromSeconds(5));
        // Reaching here without a TimeoutException is the proof; both callers
        // necessarily held their locks simultaneously.
        entries.ShouldBe(2);
    }

    [Fact]
    public async Task AcquireAsync_Cancelled_DoesNotLeakRefCount()
    {
        // A cancelled WaitAsync must still decrement the refcount we took in the
        // dictionary gate, otherwise a slot is permanently pinned and never
        // evicted — a slow leak that only manifests after long uptime.
        var sut = new SessionWriteLock();
        var sessionId = SessionId.Create();

        // Hold the lock so the second caller blocks in WaitAsync.
        var holder = await sut.AcquireAsync(sessionId);

        using var cts = new CancellationTokenSource();
        var waiterTask = sut.AcquireAsync(sessionId, cts.Token);

        // Yield to give the waiter a chance to actually enter WaitAsync before we cancel.
        await Task.Delay(50);
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(async () => await waiterTask);

        // Release the original holder — slot should now be removed because the
        // cancelled waiter correctly decremented its refcount.
        await holder.DisposeAsync();

        sut.OutstandingSlotCount.ShouldBe(0,
            "Cancelling a pending acquire must decrement the refcount the gate took. " +
            "A leaked refcount keeps the slot in the dictionary forever even though no " +
            "caller holds the lock.");
    }

    [Fact]
    public async Task AcquireAsync_AfterFullRelease_RemovesSlotFromDictionary()
    {
        // Refcount cleanup is the only mechanism preventing per-session-id memory
        // growth. If a regression switched to a never-evict ConcurrentDictionary
        // (the duck's "simpler but leaky" alternative), this test would catch it.
        var sut = new SessionWriteLock();
        var sessionId = SessionId.Create();

        var lease = await sut.AcquireAsync(sessionId);
        sut.OutstandingSlotCount.ShouldBe(1);

        await lease.DisposeAsync();
        sut.OutstandingSlotCount.ShouldBe(0,
            "After the last lease for a session id is released, the dictionary entry " +
            "must be removed so the lock primitive does not accumulate per-session " +
            "memory over the lifetime of the gateway.");
    }

    [Fact]
    public async Task AcquireAsync_RepeatedCycles_DoNotLeak()
    {
        // 100 sequential acquire/release cycles on the same session id must
        // leave the dictionary empty. Catches a class of bugs where slot
        // disposal is asymmetric with slot creation (e.g. semaphore reused
        // across cycles instead of recreated, or a release that doesn't
        // remove on RefCount==0).
        var sut = new SessionWriteLock();
        var sessionId = SessionId.Create();

        for (var i = 0; i < 100; i++)
        {
            await using var lease = await sut.AcquireAsync(sessionId);
        }

        sut.OutstandingSlotCount.ShouldBe(0,
            "Repeated acquire/release cycles on the same session id leaked dictionary entries.");
    }

    [Fact]
    public async Task DisposeAsync_MultipleTimes_OnlyReleasesOnce()
    {
        // Releaser is reentrancy-safe via Interlocked.Exchange — a stray double
        // dispose must not over-release the semaphore (which would corrupt the
        // semaphore count and let two future callers into the critical section).
        var sut = new SessionWriteLock();
        var sessionId = SessionId.Create();

        var lease = await sut.AcquireAsync(sessionId);
        await lease.DisposeAsync();
        await lease.DisposeAsync();

        // Acquire+release one more time — must succeed without a SemaphoreFullException
        // or any other corruption symptom.
        await using var probe = await sut.AcquireAsync(sessionId);
        sut.OutstandingSlotCount.ShouldBe(1);
    }

    [Fact]
    public async Task AcquireAsync_AfterDispose_Throws()
    {
        // Defensive: once the lock primitive is disposed, future acquires must
        // throw rather than silently no-op (which would be a correctness bomb
        // for any code that depends on the serialization guarantee).
        var sut = new SessionWriteLock();
        sut.Dispose();

        await Should.ThrowAsync<ObjectDisposedException>(
            () => sut.AcquireAsync(SessionId.Create()));
    }
}
