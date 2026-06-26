using ClipMetaCore.Logging;
using ClipMetaCore.Mp4;
using ClipMetaCore.Read;
using ClipMetaCore.Schema;
using ClipMetaCore.Write;
using ClipMetaScribe.Tests.Helpers;

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
    public void WriteMetadata_TransientSourceLock_RidesItOutAndWrites()
    {
        // The source open now retries a transient sharing violation (a player's lingering handle,
        // the Search indexer, AV) the same way the final swap does. Hold a deny-all handle briefly,
        // release it, and the in-flight write must ride out the lock and complete.
        string dir = Path.Combine(Path.GetTempPath(), "cmlock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string clip = Path.Combine(dir, "clip.mp4");
            using (var ms = MinimalMp4Builder.BuildMp4WithStco(9999, ClipMetaSchema.Domain, "game", "TF2"))
                File.WriteAllBytes(clip, ms.ToArray());

            var holder = new FileStream(clip, FileMode.Open, FileAccess.Read, FileShare.None);
            var write = Task.Run(() =>
            {
                var m = new MetadataMutation();
                m.SetFields[ClipMetaSchema.AtomName("tags")] = "rode-it-out";
                new Mp4Writer().WriteMetadata(clip, m, NullLogger.Instance);
            });

            Thread.Sleep(150);   // first open attempt(s) fail while the file is held...
            holder.Dispose();    // ...then the lock clears and a retry wins
            write.GetAwaiter().GetResult();   // must complete without throwing

            var fields = ClipMetaReader.GetFields(Mp4Parser.ParseFile(clip));
            Assert.IsTrue(fields.Any(f => f.Field == "tags" && f.Value == "rode-it-out"),
                "the write should have ridden out the transient lock and stored the tag");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
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
