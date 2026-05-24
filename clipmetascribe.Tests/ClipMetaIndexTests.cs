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
}
