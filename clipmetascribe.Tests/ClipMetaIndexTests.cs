using ClipMetaCore.Logging;
using ClipMetaCore.Read;
using ClipMetaCore.Schema;
using ClipMetaCore.Write;
using ClipMetaScribe.Tests.Helpers;

namespace ClipMetaScribe.Tests;

[TestClass]
public class ClipMetaIndexTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string PrepareClip(string fileName, string field, string value)
    {
        string source = TestClipsLocator.AllPristine().First();
        string dest   = Path.Combine(_tempDir, fileName);
        File.Copy(source, dest);
        var mutation = new MetadataMutation();
        mutation.SetFields[ClipMetaSchema.AtomName(field)] = value;
        new Mp4Writer().WriteMetadata(dest, mutation, NullLogger.Instance);
        return dest;
    }

    [TestMethod]
    public void Build_EmptyDirectory_ReturnsZeroEntries()
    {
        var data = ClipMetaIndex.Build(_tempDir);

        Assert.AreEqual(0, data.Entries.Count);
    }

    [TestMethod]
    public void Build_WithMetadataClip_ReturnsOneEntry()
    {
        PrepareClip("clip.mp4", "game", "Team Fortress 2");

        var data = ClipMetaIndex.Build(_tempDir);

        Assert.AreEqual(1, data.Entries.Count);
    }

    [TestMethod]
    public void Build_EntryContainsWrittenField()
    {
        PrepareClip("clip.mp4", "game", "Team Fortress 2");

        var data = ClipMetaIndex.Build(_tempDir);

        Assert.IsTrue(data.Entries[0].Fields.Any(f => f.Field == "game" && f.Value == "Team Fortress 2"));
    }

    [TestMethod]
    public void Build_ExcludesSchemaField()
    {
        PrepareClip("clip.mp4", "game", "TF2");

        var data = ClipMetaIndex.Build(_tempDir);

        Assert.IsFalse(data.Entries[0].Fields.Any(f => f.Field.Equals("schema", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void Build_SetsDirectory()
    {
        var data = ClipMetaIndex.Build(_tempDir);

        Assert.AreEqual(_tempDir, data.Directory);
    }

    // ── Unreadable file mixed in (v1.0.1 hardening, task B1) ────────────────
    //
    // A locked file (still being written by another process) throws IOException when the
    // scanner opens it. Before this fix that was silently swallowed with no record of which
    // file was skipped. The scan must complete, index the good clip, and report the skip.

    [TestMethod]
    public void Build_LockedFileMixedIn_IndexesGoodFile()
    {
        PrepareClip("good.mp4", "game", "TF2");
        string locked = Path.Combine(_tempDir, "locked.mp4");
        File.WriteAllBytes(locked, new byte[] { 0, 0, 0, 0 });
        using var handle = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var data = ClipMetaIndex.Build(_tempDir);

        Assert.AreEqual(1, data.Entries.Count);
        Assert.AreEqual("good.mp4", Path.GetFileName(data.Entries[0].FilePath));
    }

    [TestMethod]
    public void Build_LockedFileMixedIn_ReportsSkippedPath()
    {
        PrepareClip("good.mp4", "game", "TF2");
        string locked = Path.Combine(_tempDir, "locked.mp4");
        File.WriteAllBytes(locked, new byte[] { 0, 0, 0, 0 });
        using var handle = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var skipped = new List<string>();
        ClipMetaIndex.Build(_tempDir, onFileSkipped: (path, _) => skipped.Add(path));

        CollectionAssert.Contains(skipped, locked);
    }

    [TestMethod]
    public void WriteRead_RoundTrips_Directory()
    {
        var data = ClipMetaIndex.Build(_tempDir);
        using var sw = new StringWriter();
        ClipMetaIndex.Write(data, sw);
        using var sr = new StringReader(sw.ToString());

        var result = ClipMetaIndex.Read(sr);

        Assert.AreEqual(data.Directory, result.Directory);
    }

    [TestMethod]
    public void WriteRead_RoundTrips_EntryFilePath()
    {
        PrepareClip("clip.mp4", "game", "TF2");
        var data = ClipMetaIndex.Build(_tempDir);
        using var sw = new StringWriter();
        ClipMetaIndex.Write(data, sw);
        using var sr = new StringReader(sw.ToString());

        var result = ClipMetaIndex.Read(sr);

        Assert.AreEqual(data.Entries[0].FilePath, result.Entries[0].FilePath);
    }

    [TestMethod]
    public void WriteRead_RoundTrips_Fields()
    {
        PrepareClip("clip.mp4", "game", "Team Fortress 2");
        var data = ClipMetaIndex.Build(_tempDir);
        using var sw = new StringWriter();
        ClipMetaIndex.Write(data, sw);
        using var sr = new StringReader(sw.ToString());

        var result = ClipMetaIndex.Read(sr);

        Assert.IsTrue(result.Entries[0].Fields.Any(f => f.Field == "game" && f.Value == "Team Fortress 2"));
    }

    [TestMethod]
    public void WriteRead_EmptyEntries_ReturnsZeroEntries()
    {
        var data = ClipMetaIndex.Build(_tempDir);
        using var sw = new StringWriter();
        ClipMetaIndex.Write(data, sw);
        using var sr = new StringReader(sw.ToString());

        var result = ClipMetaIndex.Read(sr);

        Assert.AreEqual(0, result.Entries.Count);
    }

    [TestMethod]
    public void WriteRead_RoundTrips_FileSizeAndModified()
    {
        PrepareClip("clip.mp4", "game", "TF2");
        var data = ClipMetaIndex.Build(_tempDir);
        using var sw = new StringWriter();
        ClipMetaIndex.Write(data, sw);
        using var sr = new StringReader(sw.ToString());

        var result = ClipMetaIndex.Read(sr);

        Assert.AreEqual(data.Entries[0].FileSizeBytes, result.Entries[0].FileSizeBytes);
        Assert.AreEqual(
            data.Entries[0].LastModified.ToUnixTimeSeconds(),
            result.Entries[0].LastModified.ToUnixTimeSeconds());
    }

    [TestMethod]
    public void WriteRead_RoundTrips_FieldValueWithNewline()
    {
        var fields = new List<(string, string)> { ("notes", "line one\nline two") };
        var entry = new IndexEntry("clip.mp4", 0, DateTimeOffset.UtcNow, fields);
        var data = new IndexData("C:\\clips", DateTimeOffset.UtcNow, new[] { entry }.ToList());
        using var sw = new StringWriter();
        ClipMetaIndex.Write(data, sw);
        using var sr = new StringReader(sw.ToString());

        var result = ClipMetaIndex.Read(sr);

        Assert.AreEqual("line one\nline two", result.Entries[0].Fields[0].Value);
    }

    [TestMethod]
    public void WriteRead_RoundTrips_FieldValueWithBackslash()
    {
        var fields = new List<(string, string)> { ("notes", @"C:\path\to\file") };
        var entry = new IndexEntry("clip.mp4", 0, DateTimeOffset.UtcNow, fields);
        var data = new IndexData("C:\\clips", DateTimeOffset.UtcNow, new[] { entry }.ToList());
        using var sw = new StringWriter();
        ClipMetaIndex.Write(data, sw);
        using var sr = new StringReader(sw.ToString());

        var result = ClipMetaIndex.Read(sr);

        Assert.AreEqual(@"C:\path\to\file", result.Entries[0].Fields[0].Value);
    }

    // ── Field names containing spaces (v1.0.1 hardening, task B7) ─────────────
    //
    // The bug: "field {Escape(field)} {Escape(value)}" is space-delimited and Escape() did not
    // escape spaces, so the reader (which splits the "field" line at the first space to recover
    // the field name) silently mis-parses a field name like "kill count" as field "kill", value
    // "count 5". `--set "kill count" 5` writes fine to the MP4, but the cached index and a live
    // `--find` then silently disagree.

    [TestMethod]
    public void WriteRead_RoundTrips_FieldNameWithSpace()
    {
        var fields = new List<(string, string)> { ("kill count", "5") };
        var entry = new IndexEntry("clip.mp4", 0, DateTimeOffset.UtcNow, fields);
        var data = new IndexData("C:\\clips", DateTimeOffset.UtcNow, new[] { entry }.ToList());
        using var sw = new StringWriter();
        ClipMetaIndex.Write(data, sw);
        using var sr = new StringReader(sw.ToString());

        var result = ClipMetaIndex.Read(sr);

        Assert.AreEqual("kill count", result.Entries[0].Fields[0].Field);
        Assert.AreEqual("5", result.Entries[0].Fields[0].Value);
    }

    [TestMethod]
    public void WriteRead_RoundTrips_FieldNameWithMultipleSpaces()
    {
        var fields = new List<(string, string)> { ("total kill count today", "5") };
        var entry = new IndexEntry("clip.mp4", 0, DateTimeOffset.UtcNow, fields);
        var data = new IndexData("C:\\clips", DateTimeOffset.UtcNow, new[] { entry }.ToList());
        using var sw = new StringWriter();
        ClipMetaIndex.Write(data, sw);
        using var sr = new StringReader(sw.ToString());

        var result = ClipMetaIndex.Read(sr);

        Assert.AreEqual("total kill count today", result.Entries[0].Fields[0].Field);
        Assert.AreEqual("5", result.Entries[0].Fields[0].Value);
    }

    // Pin the adversarial escape cases the space-escaping fix's correctness depends on, so a
    // future edit to the escape table cannot silently reintroduce a collision. These are pins,
    // not fixes: they passed immediately against the fixed code.

    [TestMethod]
    public void WriteRead_RoundTrips_FieldNameContainingLiteralBackslashS()
    {
        // A field name literally containing the 2-char sequence backslash+s must survive:
        // Escape doubles the backslash first, so on disk it is backslash-backslash-s, which
        // Unescape must decode back to backslash+s, NOT collapse into a space.
        var fields = new List<(string, string)> { (@"kill\scount", "5") };
        var entry = new IndexEntry("clip.mp4", 0, DateTimeOffset.UtcNow, fields);
        var data = new IndexData("C:\\clips", DateTimeOffset.UtcNow, new[] { entry }.ToList());
        using var sw = new StringWriter();
        ClipMetaIndex.Write(data, sw);
        using var sr = new StringReader(sw.ToString());

        var result = ClipMetaIndex.Read(sr);

        Assert.AreEqual(@"kill\scount", result.Entries[0].Fields[0].Field);
        Assert.AreEqual("5", result.Entries[0].Fields[0].Value);
    }

    [TestMethod]
    public void WriteRead_RoundTrips_FieldNameWithBackslashesIncludingOneBeforeDelimiter()
    {
        // Raw backslashes in the name, including a trailing one that sits immediately before
        // the name/value delimiter space on the serialized line.
        var fields = new List<(string, string)> { (@"dir\sub\", "x") };
        var entry = new IndexEntry("clip.mp4", 0, DateTimeOffset.UtcNow, fields);
        var data = new IndexData("C:\\clips", DateTimeOffset.UtcNow, new[] { entry }.ToList());
        using var sw = new StringWriter();
        ClipMetaIndex.Write(data, sw);
        using var sr = new StringReader(sw.ToString());

        var result = ClipMetaIndex.Read(sr);

        Assert.AreEqual(@"dir\sub\", result.Entries[0].Fields[0].Field);
        Assert.AreEqual("x", result.Entries[0].Fields[0].Value);
    }

    [TestMethod]
    public void WriteRead_RoundTrips_ValueWithLeadingSpaces()
    {
        var fields = new List<(string, string)> { ("notes", "  two leading spaces") };
        var entry = new IndexEntry("clip.mp4", 0, DateTimeOffset.UtcNow, fields);
        var data = new IndexData("C:\\clips", DateTimeOffset.UtcNow, new[] { entry }.ToList());
        using var sw = new StringWriter();
        ClipMetaIndex.Write(data, sw);
        using var sr = new StringReader(sw.ToString());

        var result = ClipMetaIndex.Read(sr);

        Assert.AreEqual("notes", result.Entries[0].Fields[0].Field);
        Assert.AreEqual("  two leading spaces", result.Entries[0].Fields[0].Value);
    }

    [TestMethod]
    public void Read_PreFixFormat_FieldsWithoutSpaces_ParseIdentically()
    {
        // Pins today's on-disk format (no space-escaping) so the space-escaping fix cannot
        // change how index files already on disk before this change are read. Hand-crafted
        // rather than round-tripped through Write(), so this test fails independently of
        // whatever the writer does. All backslashes are doubled, exactly as the pre-fix
        // Escape wrote them (it always doubled a real backslash).
        string raw = string.Join("\n", new[]
        {
            "version 1",
            "built 2026-01-01T00:00:00.0000000+00:00",
            @"directory C:\\clips",
            "---",
            @"path C:\\clips\\clip.mp4",
            "size 1234",
            "modified 2026-01-01T00:00:00.0000000+00:00",
            "field game Team Fortress 2",
            @"field notes C:\\path\\to\\file",
            "",
        });

        var result = ClipMetaIndex.Read(new StringReader(raw));

        Assert.AreEqual(@"C:\clips", result.Directory);
        Assert.AreEqual(1, result.Entries.Count);
        var entry = result.Entries[0];
        Assert.AreEqual(@"C:\clips\clip.mp4", entry.FilePath);
        Assert.AreEqual(1234, entry.FileSizeBytes);
        Assert.IsTrue(entry.Fields.Any(f => f.Field == "game" && f.Value == "Team Fortress 2"));
        Assert.IsTrue(entry.Fields.Any(f => f.Field == "notes" && f.Value == @"C:\path\to\file"));
    }

    // ── Atomic WriteToFile: a failed write must never corrupt the existing index ──

    private string IndexPath() => Path.Combine(_tempDir, ClipMetaIndex.IndexFileName);

    private static IndexData OneEntry(string value) => new(
        "C:\\clips", DateTimeOffset.UtcNow,
        new[] { new IndexEntry("a.mp4", 1, DateTimeOffset.UtcNow, new[] { ("game", value) }) });

    [TestMethod]
    public void WriteToFile_FailureMidSerialization_LeavesExistingIndexIntactAndNoTemp()
    {
        // The bug this feature fixes: the old WriteToFile opened the target with
        // StreamWriter(append:false), truncating it on open, so a write that fails partway
        // (here, an entry list that throws mid-enumeration, standing in for a crash / disk-full)
        // would leave the user's previously-built index truncated. Atomic temp-then-swap must
        // leave the existing index byte-for-byte intact.
        string idx = IndexPath();
        ClipMetaIndex.WriteToFile(OneEntry("original"), idx);
        byte[] before = File.ReadAllBytes(idx);

        var poisoned = new IndexData("C:\\clips", DateTimeOffset.UtcNow, new ThrowingEntryList());
        Assert.ThrowsExactly<InvalidOperationException>(() => ClipMetaIndex.WriteToFile(poisoned, idx));

        CollectionAssert.AreEqual(before, File.ReadAllBytes(idx),
            "a failed write must not corrupt the existing index");
        Assert.IsFalse(Directory.EnumerateFiles(_tempDir, "*.tmp").Any(),
            "a failed write must not leave a temp file behind");
    }

    [TestMethod]
    public void WriteToFile_Success_LeavesNoTempFile()
    {
        ClipMetaIndex.WriteToFile(OneEntry("v"), IndexPath());
        Assert.IsFalse(Directory.EnumerateFiles(_tempDir, "*.tmp").Any(),
            "a successful write must clean up its temp file");
    }

    [TestMethod]
    public void WriteToFile_FirstWriteThenOverwrite_RoundTrips()
    {
        string idx = IndexPath();
        ClipMetaIndex.WriteToFile(OneEntry("first"), idx);          // no pre-existing target (Move path)
        ClipMetaIndex.WriteToFile(OneEntry("second"), idx);         // overwrite existing (Replace path)

        var read = ClipMetaIndex.ReadFromFile(idx);
        Assert.AreEqual("second", read.Entries[0].Fields[0].Value);
    }

    // ── CheckEntry: detect clips changed/removed since the index was built ────────

    private string MakeFile(string name, byte[] bytes)
    {
        string path = Path.Combine(_tempDir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static IndexEntry EntryFor(string path) =>
        new(path, new FileInfo(path).Length,
            new DateTimeOffset(new FileInfo(path).LastWriteTimeUtc, TimeSpan.Zero),
            new[] { ("game", "TF2") });

    [TestMethod]
    public void CheckEntry_UnchangedFile_ReturnsNull()
    {
        string path = MakeFile("clip.mp4", new byte[] { 1, 2, 3, 4 });
        Assert.IsNull(ClipMetaIndex.CheckEntry(EntryFor(path)));
    }

    [TestMethod]
    public void CheckEntry_SizeChanged_ReturnsModified()
    {
        string path = MakeFile("clip.mp4", new byte[] { 1, 2, 3, 4 });
        var entry = EntryFor(path) with { FileSizeBytes = 999 };   // recorded size no longer matches
        Assert.AreEqual(StaleReason.Modified, ClipMetaIndex.CheckEntry(entry));
    }

    [TestMethod]
    public void CheckEntry_LastModifiedChanged_ReturnsModified()
    {
        string path = MakeFile("clip.mp4", new byte[] { 1, 2, 3, 4 });
        // Same size, but recorded one hour earlier than the file's real mtime.
        var entry = EntryFor(path) with { LastModified = EntryFor(path).LastModified.AddHours(-1) };
        Assert.AreEqual(StaleReason.Modified, ClipMetaIndex.CheckEntry(entry));
    }

    [TestMethod]
    public void CheckEntry_MissingFile_ReturnsMissing()
    {
        var entry = new IndexEntry(
            Path.Combine(_tempDir, "gone.mp4"), 10, DateTimeOffset.UtcNow, new[] { ("game", "TF2") });
        Assert.AreEqual(StaleReason.Missing, ClipMetaIndex.CheckEntry(entry));
    }

    /// <summary>An entry list that yields nothing and throws as soon as it is enumerated, 
    /// stands in for an interrupted serialization (crash / disk-full) mid-write.</summary>
    private sealed class ThrowingEntryList : IReadOnlyList<IndexEntry>
    {
        public int Count => 1;
        public IndexEntry this[int index] => throw new InvalidOperationException("simulated write failure");
        public IEnumerator<IndexEntry> GetEnumerator()
            => throw new InvalidOperationException("simulated write failure");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
