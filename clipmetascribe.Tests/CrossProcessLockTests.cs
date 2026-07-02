// clipmetascribe.Tests/CrossProcessLockTests.cs
using ClipMetaCore.Write;

namespace ClipMetaScribe.Tests;

/// <summary>
/// Unit tests for <see cref="CrossProcessLock"/>, the named-mutex serialization primitive behind
/// every MP4 write and tag-queue operation (task B4). Named mutexes serialize threads of one
/// process exactly as they serialize processes, so contention is driven with threads here;
/// distinct GUID-suffixed paths per test keep parallel test runs from colliding on real mutexes.
/// </summary>
[TestClass]
public class CrossProcessLockTests
{
    /// <summary>A path unique to this test run; the file never needs to exist.</summary>
    private static string UniquePath() =>
        Path.Combine(Path.GetTempPath(), "cmlocktest-" + Guid.NewGuid().ToString("N") + ".mp4");

    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan TestWait = TimeSpan.FromSeconds(15);

    [TestMethod]
    public void Acquire_ThenDispose_CanBeAcquiredAgain()
    {
        string path = UniquePath();
        using (CrossProcessLock.Acquire(path, Short)) { }
        using (CrossProcessLock.Acquire(path, Short)) { }
    }

    [TestMethod]
    public void MutexName_IsCanonicalized_CaseAndRelativeSegmentsCollapse()
    {
        // Two spellings of the same file must contend on the same mutex, otherwise the lock
        // silently fails open for a caller that passes a differently-cased or relative path.
        string dir = Path.GetTempPath();
        string a = Path.Combine(dir, "Clip.MP4");
        string b = Path.Combine(dir, "sub", "..", "clip.mp4");

        Assert.AreEqual(CrossProcessLock.MutexNameFor(a), CrossProcessLock.MutexNameFor(b),
            "same file, different spelling: must map to the same mutex");
        Assert.AreNotEqual(
            CrossProcessLock.MutexNameFor(Path.Combine(dir, "other.mp4")),
            CrossProcessLock.MutexNameFor(a),
            "different files must map to different mutexes");
        StringAssert.StartsWith(CrossProcessLock.MutexNameFor(a), @"Local\",
            "session-local namespace is the documented, deliberate choice");
    }

    [TestMethod]
    public void SecondAcquire_WhileHeldByAnotherThread_TimesOutWithClearError()
    {
        string path = UniquePath();
        using var held = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var holder = new Thread(() =>
        {
            using (CrossProcessLock.Acquire(path, Short))
            {
                held.Set();
                release.Wait(TestWait);
            }
        });
        holder.Start();
        Assert.IsTrue(held.Wait(TestWait), "holder thread never acquired");

        var ex = Assert.ThrowsExactly<CrossProcessLockTimeoutException>(
            () => CrossProcessLock.Acquire(path, Short).Dispose());
        StringAssert.Contains(ex.Message, path, "the timeout error must name the contended resource");
        Assert.IsInstanceOfType<IOException>(ex,
            "the timeout must be an IOException so every existing fail-safe catch path covers it");

        release.Set();
        holder.Join(TestWait);
        using (CrossProcessLock.Acquire(path, Short)) { } // released: acquirable again
    }

    [TestMethod]
    public void DifferentResources_DoNotContend()
    {
        string a = UniquePath(), b = UniquePath();
        using var lockA = CrossProcessLock.Acquire(a, Short);
        using var lockB = CrossProcessLock.Acquire(b, Short); // must not block or throw
    }

    [TestMethod]
    public void AbandonedByDeadThread_IsTreatedAsAcquired()
    {
        // A holder that dies without releasing (crashed process / killed host) must not brick
        // the resource: the next acquirer gets AbandonedMutexException from the OS, which the
        // lock treats as a successful acquisition.
        string path = UniquePath();
        var dying = new Thread(() => CrossProcessLock.Acquire(path, Short)); // never disposed
        dying.Start();
        Assert.IsTrue(dying.Join(TestWait), "abandoning thread never exited");

        using (CrossProcessLock.Acquire(path, Short)) { } // must succeed, not throw
    }

    [TestMethod]
    public void Reentrant_SameThread_NestedAcquireHoldsUntilOuterDispose()
    {
        // Load-bearing recursion: TagQueue.Save acquires the queue lock and is also called from
        // Enqueue/Drain which already hold it. The lock is only fully released when every
        // instance on the owning thread has been disposed.
        string path = UniquePath();
        using var outer = CrossProcessLock.Acquire(path, Short);
        var inner = CrossProcessLock.Acquire(path, Short); // same thread: must not block
        inner.Dispose();

        // Still held by `outer`: another thread must time out.
        Exception? observed = null;
        var probe = new Thread(() =>
        {
            try { CrossProcessLock.Acquire(path, Short).Dispose(); }
            catch (Exception ex) { observed = ex; }
        });
        probe.Start();
        Assert.IsTrue(probe.Join(TestWait), "probe thread never finished");
        Assert.IsInstanceOfType<CrossProcessLockTimeoutException>(observed,
            "disposing the nested inner acquire must NOT release the outer hold");
    }

    [TestMethod]
    public void Dispose_Twice_IsSafe()
    {
        string path = UniquePath();
        var l = CrossProcessLock.Acquire(path, Short);
        l.Dispose();
        l.Dispose(); // must not throw (would surface as ApplicationException: not owner)
        using (CrossProcessLock.Acquire(path, Short)) { }
    }
}
