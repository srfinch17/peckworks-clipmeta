using ClipMetaCore.Mp4;

namespace ClipMetaScribe.Tests;

/// <summary>
/// Regression tests for the exact failure a nemesis review demonstrated (v1.0.1 hardening,
/// task B1): a 15-byte file declaring an extended-size (<c>size == 1</c>) box header whose
/// 8-byte extended-size field is cut off at EOF. Before the fix, <see cref="BigEndianReader"/>
/// fed a short byte array to <see cref="BitConverter"/>, which threw <see cref="ArgumentException"/>,
/// an undocumented type outside <see cref="Mp4Parser"/>'s documented <see cref="InvalidDataException"/>
/// contract and outside every directory scanner's catch list, crashing the whole scan with a raw
/// stack trace instead of skipping the one bad file.
/// </summary>
[TestClass]
public class Mp4ParserTruncatedFileTests
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

    /// <summary>
    /// 15 bytes: a 4-byte size field of 1 (extended), a 4-byte type, and only 7 of the 8 bytes
    /// the extended-size field requires. This is the exact byte layout the nemesis review used.
    /// </summary>
    private string WriteTruncatedExtendedSizeFile(string fileName = "truncated.mp4")
    {
        byte[] bytes =
        {
            0x00, 0x00, 0x00, 0x01,                         // size = 1 (extended)
            0x6D, 0x64, 0x61, 0x74,                         // 'mdat'
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,       // extended-size field, 7 of 8 bytes
        };
        string path = Path.Combine(_tempDir, fileName);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [TestMethod]
    public void ParseFile_TruncatedExtendedSizeHeader_NeverThrowsArgumentException()
    {
        string path = WriteTruncatedExtendedSizeFile();

        try
        {
            Mp4Parser.ParseFile(path);
        }
        catch (ArgumentException ex)
        {
            Assert.Fail(
                "ParseFile must never let a truncated file surface a raw ArgumentException " +
                $"from BitConverter (the demonstrated bug); got: {ex}");
        }
        // Any other well-typed outcome (success with a partial tree, or a documented
        // InvalidDataException) is acceptable, see the next test for what actually happens.
    }

    [TestMethod]
    public void ParseFile_TruncatedExtendedSizeHeader_ParsesLeniently()
    {
        // Mp4Parser is deliberately lenient: a box whose header cannot be fully read stops the
        // scan at that point rather than throwing, "a damaged file should still be viewable up
        // to the damage" (see the corrupt-box handling in Mp4Parser.ParseBoxes). Since this file
        // has no bytes before the damage, that means an empty (but validly parsed) root. This
        // documents the actual, intentional post-fix behavior so a future change to this
        // contract fails a test instead of silently drifting.
        string path = WriteTruncatedExtendedSizeFile();

        var root = Mp4Parser.ParseFile(path);

        Assert.IsNotNull(root);
        Assert.AreEqual(0, root.Children.Count);
    }
}
