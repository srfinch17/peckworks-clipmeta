// clipmetascribe.Tests/CrossProcessSerializationTests.cs
using ClipMetaCore.Abstractions;
using ClipMetaCore.Logging;
using ClipMetaCore.Mp4;
using ClipMetaCore.Read;
using ClipMetaCore.Schema;
using ClipMetaCore.Watching;
using ClipMetaCore.Write;
using ClipMetaScribe.Tests.Helpers;

namespace ClipMetaScribe.Tests;

/// <summary>
/// Serialization tests for the two multi-writer failure modes the cross-process lock retires
/// (nemesis review, task B4). Deployment is explicitly multi-process (one MCP server per host app
/// plus CLI batch runs); named mutexes serialize in-process threads exactly the same way they
/// serialize processes, so these tests drive the contention with threads, deterministic where the
/// real bug was a cross-process race.
/// <list type="number">
///   <item>A <c>TagQueue.Drain</c> holds its queue snapshot in memory across (multi-second) MP4
///       writes and then saves the survivors; a concurrent <c>Enqueue</c> between the load and
///       that save was silently overwritten, a spoken tag vanished.</item>
///   <item>Two writers both snapshot the same clip, both rebuild from their (now stale) parse,
///       and both <c>File.Replace</c>; the loser's committed fields are silently discarded
///       (never torn output, but a stale-based write).</item>
/// </list>
/// </summary>
[TestClass]
public class CrossProcessSerializationTests
{
    private string _dir = null!;

    [TestInitialize]
    public void Init()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cmxproc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Waits generously; a stuck thread should fail the assert, not hang the suite.</summary>
    private static readonly TimeSpan TestWait = TimeSpan.FromSeconds(15);

    private void EnqueueTag(string clipPath, string value)
    {
        var m = new MetadataMutation();
        m.AppendFields[ClipMetaSchema.AtomName("tags")] = value;
        TagQueue.Enqueue(_dir, clipPath, m, "high");
    }

    /// <summary>A writer that signals when a drain has entered it, then blocks until released,
    /// pinning the drain mid-flight exactly where its in-memory queue snapshot is stale.</summary>
    private sealed class GatedWriter(ManualResetEventSlim entered, ManualResetEventSlim release) : IMediaWriter
    {
        public bool CanWrite(string filePath) => true;
        public void WriteMetadata(string filePath, MetadataMutation mutation, IClipMetaLogger logger)
        {
            entered.Set();
            if (!release.Wait(TestWait))
                throw new TimeoutException("GatedWriter was never released; test orchestration bug.");
        }
    }

    /// <summary>A slow writer that counts every write, for the double-drain double-write check.</summary>
    private sealed class CountingSlowWriter(int perWriteDelayMs) : IMediaWriter
    {
        private int _totalWrites;
        public int TotalWrites => Volatile.Read(ref _totalWrites);
        public bool CanWrite(string filePath) => true;
        public void WriteMetadata(string filePath, MetadataMutation mutation, IClipMetaLogger logger)
        {
            Interlocked.Increment(ref _totalWrites);
            Thread.Sleep(perWriteDelayMs);
        }
    }

    [TestMethod]
    public void Enqueue_DuringSlowDrain_SurvivesTheDrainsFinalSave()
    {
        // Failure mode 1: Drain loads the queue, spends seconds writing MP4s, then persists the
        // survivors it computed from that stale snapshot. An Enqueue that lands in between must
        // NOT be overwritten by that save.
        string clipA = Path.Combine(_dir, "a.mp4");
        File.WriteAllBytes(clipA, new byte[] { 0 }); // must exist so the drain attempts the write
        EnqueueTag(clipA, "headshot");

        using var drainInWrite = new ManualResetEventSlim(false);
        using var releaseWrite = new ManualResetEventSlim(false);
        Task drain = Task.Run(() => TagQueue.Drain(
            _dir, new GatedWriter(drainInWrite, releaseWrite), NullLogger.Instance, _ => false));
        Assert.IsTrue(drainInWrite.Wait(TestWait), "the drain never reached the writer");

        // The drain is mid-write with its queue snapshot in memory. A second process now
        // enqueues a fresh spoken tag for a different clip.
        string clipB = Path.Combine(_dir, "b.mp4");
        Task enqueue = Task.Run(() => EnqueueTag(clipB, "airshot"));

        // Under the lock the enqueue blocks here; unserialized it lands now and the drain's
        // final save wipes it. Either way, release the drain and let both finish.
        Thread.Sleep(400);
        releaseWrite.Set();
        Assert.IsTrue(Task.WaitAll(new[] { drain, enqueue }, TestWait), "drain/enqueue did not finish");

        TagQueueData data = TagQueue.Load(_dir);
        Assert.IsTrue(
            data.Entries.Any(e => string.Equals(e.ClipPath, clipB, StringComparison.OrdinalIgnoreCase)),
            "a tag enqueued during a drain must survive the drain's final save, " +
            "an unserialized drain silently overwrites it and the spoken tag vanishes");
    }

    [TestMethod]
    public void TwoConcurrentDrains_WriteEachQueuedTagExactlyOnce()
    {
        // Two processes flushing the same queue at once (e.g. Claude Desktop's pump and a CLI
        // --flush-queue) must not both load the same entries and write every clip twice.
        string clipA = Path.Combine(_dir, "a.mp4");
        string clipB = Path.Combine(_dir, "b.mp4");
        File.WriteAllBytes(clipA, new byte[] { 0 });
        File.WriteAllBytes(clipB, new byte[] { 0 });
        EnqueueTag(clipA, "headshot");
        EnqueueTag(clipB, "airshot");

        var writer = new CountingSlowWriter(perWriteDelayMs: 250);
        using var barrier = new Barrier(2);
        Task Drain() => Task.Run(() =>
        {
            barrier.SignalAndWait(TestWait);
            TagQueue.Drain(_dir, writer, NullLogger.Instance, _ => false);
        });
        Task d1 = Drain(), d2 = Drain();
        Assert.IsTrue(Task.WaitAll(new[] { d1, d2 }, TestWait), "concurrent drains did not finish");

        Assert.AreEqual(2, writer.TotalWrites,
            "each queued tag must be written exactly once across concurrent drains, " +
            "unserialized drains both load the same snapshot and double-write every clip");
        Assert.AreEqual(0, TagQueue.Load(_dir).Entries.Count, "the queue must end empty");
    }

    [TestMethod]
    public void TwoConcurrentWrites_SameClip_NeitherFieldIsLost()
    {
        // Failure mode 2: two writers snapshot the same original, both rebuild and File.Replace;
        // the second swap is based on a parse that predates the first writer's fields, so those
        // fields are silently discarded. Serialized, every field from every writer must survive.
        // A handful of rounds widens the overlap window; each round writes two distinct fields.
        string clip = Path.Combine(_dir, "c.mp4");
        using (var ms = MinimalMp4Builder.BuildMp4WithStco(9999, ClipMetaSchema.Domain, "game", "TF2"))
            File.WriteAllBytes(clip, ms.ToArray());

        const int Rounds = 5;
        var failures = new List<Exception>();
        for (int round = 0; round < Rounds; round++)
        {
            using var barrier = new Barrier(2);
            Task WriteField(string field, string value) => Task.Run(() =>
            {
                barrier.SignalAndWait(TestWait);
                var m = new MetadataMutation();
                m.SetFields[ClipMetaSchema.AtomName(field)] = value;
                try { new Mp4Writer().WriteMetadata(clip, m, NullLogger.Instance); }
                catch (Exception ex) { lock (failures) failures.Add(ex); }
            });
            Task t1 = WriteField($"alpha{round}", $"a{round}");
            Task t2 = WriteField($"beta{round}", $"b{round}");
            Assert.IsTrue(Task.WaitAll(new[] { t1, t2 }, TestWait), "concurrent writes did not finish");
        }

        Assert.AreEqual(0, failures.Count,
            "serialized writes must all succeed; got: " + string.Join("; ", failures.Select(f => f.Message)));

        var fields = ClipMetaReader.GetUserFields(Mp4Parser.ParseFile(clip))
            .ToDictionary(f => f.Field, f => f.Value, StringComparer.Ordinal);
        for (int round = 0; round < Rounds; round++)
        {
            Assert.IsTrue(fields.TryGetValue($"alpha{round}", out string? a) && a == $"a{round}",
                $"alpha{round} was lost, a stale-based concurrent write discarded a committed field");
            Assert.IsTrue(fields.TryGetValue($"beta{round}", out string? b) && b == $"b{round}",
                $"beta{round} was lost, a stale-based concurrent write discarded a committed field");
        }
    }
}
