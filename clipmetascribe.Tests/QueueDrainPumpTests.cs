using ClipMetaCore.Abstractions;
using ClipMetaCore.Logging;
using ClipMetaCore.Schema;
using ClipMetaCore.Watching;
using ClipMetaCore.Write;

namespace ClipMetaScribe.Tests;

/// <summary>
/// The zero-touch background flush pump (spec B §3): while the queue is non-empty it polls the
/// queued clips' lock state and drains them as locks clear, idle otherwise. Driven here with fakes
/// (recording writer, scripted lock predicate, short interval) — no real player or MP4 needed.
/// </summary>
[TestClass]
public class QueueDrainPumpTests
{
    private string _dir = null!;

    [TestInitialize]
    public void Init()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cmpump-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Enqueues one entry for an existing (empty) clip file and returns its path.</summary>
    private string EnqueueClip(string name = "clip.mp4")
    {
        string clip = Path.Combine(_dir, name);
        File.WriteAllBytes(clip, Array.Empty<byte>());
        var m = new MetadataMutation();
        m.AppendFields[ClipMetaSchema.AtomName("tags")] = "headshot";
        TagQueue.Enqueue(_dir, clip, m, "high");
        return clip;
    }

    private static readonly Action<Action> DirectExclusive = a => a();
    private static readonly TimeSpan ShortPoll = TimeSpan.FromMilliseconds(20);

    private sealed class RecordingWriter(Exception? toThrow = null) : IMediaWriter
    {
        public readonly ManualResetEventSlim Wrote = new(false);
        private int _count;
        public int Count => Volatile.Read(ref _count);
        public bool CanWrite(string filePath) => true;
        public void WriteMetadata(string filePath, MetadataMutation mutation, IClipMetaLogger logger)
        {
            Interlocked.Increment(ref _count);
            Wrote.Set();
            if (toThrow is not null) throw toThrow;
        }
    }

    [TestMethod]
    public void Pump_DrainsEntry_AfterLockClears()
    {
        EnqueueClip();
        var writer = new RecordingWriter();
        // Locked on the first probe, free thereafter — exercises the poll loop, not just one drain.
        int probes = 0;
        Func<string, bool> isInUse = _ => Interlocked.Increment(ref probes) == 1;

        using var pump = new QueueDrainPump(_dir, writer, NullLogger.Instance, isInUse, DirectExclusive, ShortPoll);
        pump.Start();
        pump.Wake();

        Assert.IsTrue(writer.Wrote.Wait(TimeSpan.FromSeconds(15)), "pump must drain the entry once its lock clears");
        // Wrote fires inside WriteMetadata, just before Drain persists the queue removal — poll for it.
        bool emptied = SpinWait.SpinUntil(() => TagQueue.Load(_dir).Entries.Count == 0, TimeSpan.FromSeconds(2));
        Assert.IsTrue(emptied, "drained entry removed from the queue");
    }

    [TestMethod]
    public void Pump_EmptyQueue_DoesNotWrite()
    {
        var writer = new RecordingWriter();
        using var pump = new QueueDrainPump(_dir, writer, NullLogger.Instance, _ => false, DirectExclusive, ShortPoll);
        pump.Start();
        pump.Wake();

        Assert.IsFalse(writer.Wrote.Wait(TimeSpan.FromMilliseconds(300)), "nothing queued → nothing written");
        Assert.AreEqual(0, writer.Count);
    }

    [TestMethod]
    public void Pump_DrainsUnderRunExclusive()
    {
        EnqueueClip();
        var writer = new RecordingWriter();
        bool wasExclusiveDuringWrite = false;
        int depth = 0;
        Action<Action> runExclusive = a =>
        {
            Interlocked.Increment(ref depth);
            try { a(); } finally { Interlocked.Decrement(ref depth); }
        };
        var probingWriter = new ExclusiveAssertingWriter(() => Volatile.Read(ref depth) > 0, writer);

        using var pump = new QueueDrainPump(_dir, probingWriter, NullLogger.Instance, _ => false, runExclusive, ShortPoll);
        pump.Start();
        pump.Wake();

        Assert.IsTrue(writer.Wrote.Wait(TimeSpan.FromSeconds(15)));
        wasExclusiveDuringWrite = probingWriter.WasExclusive;
        Assert.IsTrue(wasExclusiveDuringWrite, "every drain must run inside the runExclusive section");
    }

    [TestMethod]
    public void Pump_DrainThrows_DoesNotCrashAndDisposesCleanly()
    {
        EnqueueClip();
        // Throw a type TagQueue.Drain does NOT catch, so it propagates to the pump's own guard.
        var writer = new RecordingWriter(new NotImplementedException("boom"));

        using var pump = new QueueDrainPump(_dir, writer, NullLogger.Instance, _ => false, DirectExclusive, ShortPoll);
        pump.Start();
        pump.Wake();

        Assert.IsTrue(writer.Wrote.Wait(TimeSpan.FromSeconds(15)), "drain attempt still happens");
        // The background thread must have swallowed the exception; Dispose must return promptly.
        // (An unhandled background exception would have crashed the test host before here.)
    }

    [TestMethod]
    public void Pump_Dispose_ReturnsPromptly()
    {
        var writer = new RecordingWriter();
        var pump = new QueueDrainPump(_dir, writer, NullLogger.Instance, _ => false, DirectExclusive, ShortPoll);
        pump.Start();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        pump.Dispose();
        sw.Stop();
        // Generous bound: Dispose joins with a 2s cap internally; this only asserts it doesn't hang.
        Assert.IsTrue(sw.Elapsed < TimeSpan.FromSeconds(5), "Dispose must stop and join the loop, not hang");
    }

    private sealed class ExclusiveAssertingWriter(Func<bool> isExclusive, RecordingWriter inner) : IMediaWriter
    {
        public bool WasExclusive { get; private set; }
        public bool CanWrite(string filePath) => true;
        public void WriteMetadata(string filePath, MetadataMutation mutation, IClipMetaLogger logger)
        {
            WasExclusive = isExclusive();
            inner.WriteMetadata(filePath, mutation, logger);
        }
    }
}
