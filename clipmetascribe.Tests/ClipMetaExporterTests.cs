using ClipMetaCore.Logging;
using ClipMetaCore.Read;
using ClipMetaCore.Schema;
using ClipMetaCore.Write;
using ClipMetaScribe.Tests.Helpers;

namespace ClipMetaScribe.Tests;

[TestClass]
public class ClipMetaExporterTests
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
    public void GetRecords_SingleFile_ReturnsOneRecord()
    {
        string path = PrepareClip("clip.mp4", "game", "Team Fortress 2");

        var records = ClipMetaExporter.GetRecords(new[] { path });

        Assert.AreEqual(1, records.Count);
        Assert.AreEqual(path, records[0].FilePath);
    }

    [TestMethod]
    public void GetRecords_SingleFile_ContainsWrittenField()
    {
        string path = PrepareClip("clip.mp4", "game", "Team Fortress 2");

        var records = ClipMetaExporter.GetRecords(new[] { path });

        Assert.IsTrue(records[0].Fields.Any(f => f.Field == "game" && f.Value == "Team Fortress 2"));
    }

    [TestMethod]
    public void GetRecords_ExcludesSchemaField()
    {
        string path = PrepareClip("clip.mp4", "game", "TF2");

        var records = ClipMetaExporter.GetRecords(new[] { path });

        Assert.IsFalse(records[0].Fields.Any(f => f.Field.Equals("schema", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void GetRecords_PristineFile_ReturnsRecordWithNoFields()
    {
        string source = TestClipsLocator.AllPristine().First();

        var records = ClipMetaExporter.GetRecords(new[] { source });

        Assert.AreEqual(1, records.Count);
        Assert.AreEqual(0, records[0].Fields.Count);
    }

    [TestMethod]
    public void GetRecords_MultipleFiles_ReturnsAll()
    {
        string p1 = PrepareClip("clip1.mp4", "game", "TF2");
        string p2 = PrepareClip("clip2.mp4", "game", "CS2");

        var records = ClipMetaExporter.GetRecords(new[] { p1, p2 });

        Assert.AreEqual(2, records.Count);
    }

    [TestMethod]
    public void GetRecords_EmptyFile_ReturnsRecordWithNoFields()
    {
        string emptyFile = Path.Combine(_tempDir, "empty.mp4");
        File.WriteAllBytes(emptyFile, Array.Empty<byte>());

        var records = ClipMetaExporter.GetRecords(new[] { emptyFile });

        Assert.AreEqual(1, records.Count);
        Assert.AreEqual(emptyFile, records[0].FilePath);
        Assert.AreEqual(0, records[0].Fields.Count);
    }

    [TestMethod]
    public void GetRecords_EmptyInput_ReturnsEmpty()
    {
        var records = ClipMetaExporter.GetRecords(Array.Empty<string>());

        Assert.AreEqual(0, records.Count);
    }
}
