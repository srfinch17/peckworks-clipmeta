using ClipMetaCore.Write;

namespace ClipMetaScribe.Tests;

/// <summary>
/// Unit tests for <see cref="Mp4Writer.RetryOnTransientLock"/> — the bounded retry wrapped around
/// the final atomic swap so a momentary antivirus/indexer lock on a freshly-written file doesn't
/// fail an otherwise-good write (field-discovered 2026-06-12). Driven with a controllable
/// delegate and zero delay, so these are deterministic and fast — no real file locks, no timing.
/// </summary>
[TestClass]
public class Mp4WriterRetryTests
{
    [TestMethod]
    public void Succeeds_FirstTry_NoRetry()
    {
        int calls = 0, retries = 0;
        Mp4Writer.RetryOnTransientLock(() => calls++, maxAttempts: 5, baseDelayMs: 0,
            onRetry: (_, _) => retries++);

        Assert.AreEqual(1, calls);
        Assert.AreEqual(0, retries, "a first-try success must not retry");
    }

    [TestMethod]
    public void Retries_ThenSucceeds_WhenTransientLockClears()
    {
        // Fail twice with a sharing-violation-style IOException, then succeed — exactly the
        // antivirus-releases-the-file case the retry exists for.
        int calls = 0, retries = 0;
        Mp4Writer.RetryOnTransientLock(
            () =>
            {
                calls++;
                if (calls < 3) throw new IOException("The process cannot access the file...");
            },
            maxAttempts: 5, baseDelayMs: 0, onRetry: (_, _) => retries++);

        Assert.AreEqual(3, calls, "should have retried until the third attempt succeeded");
        Assert.AreEqual(2, retries);
    }

    [TestMethod]
    public void Throws_LastException_AfterMaxAttempts()
    {
        // A lock that never clears: every attempt fails, the final exception propagates
        // (the caller then leaves the original file untouched — fail safe).
        int calls = 0;
        var ex = Assert.ThrowsExactly<IOException>(() =>
            Mp4Writer.RetryOnTransientLock(
                () => { calls++; throw new IOException($"locked #{calls}"); },
                maxAttempts: 4, baseDelayMs: 0));

        Assert.AreEqual(4, calls, "should attempt exactly maxAttempts times");
        Assert.AreEqual("locked #4", ex.Message, "the LAST failure must be the one rethrown");
    }

    [TestMethod]
    public void RetriesOn_UnauthorizedAccess_Too()
    {
        // ReplaceFile can surface a transient lock as UnauthorizedAccessException as well.
        int calls = 0;
        Mp4Writer.RetryOnTransientLock(
            () => { calls++; if (calls < 2) throw new UnauthorizedAccessException(); },
            maxAttempts: 5, baseDelayMs: 0);

        Assert.AreEqual(2, calls);
    }

    [TestMethod]
    public void DoesNotRetry_NonTransientException()
    {
        // A logic error (not a lock) must surface immediately, not be retried away.
        int calls = 0;
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            Mp4Writer.RetryOnTransientLock(
                () => { calls++; throw new InvalidOperationException("bug"); },
                maxAttempts: 5, baseDelayMs: 0));

        Assert.AreEqual(1, calls, "a non-transient exception must not be retried");
    }
}
