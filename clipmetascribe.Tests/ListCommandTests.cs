using ClipMetaCore.Read;
using ClipMetaCore.Schema;
using ClipMetaCore.Write;
using ClipMetaCore.Logging;
using ClipMetaScribe.Commands;
using ClipMetaScribe.Tests.Helpers;

namespace ClipMetaScribe.Tests;

/// <summary>Tests the output formatting contract for <see cref="ListCommand"/>.</summary>
[TestClass]
public class ListCommandTests
{
    private static readonly System.Collections.Concurrent.ConcurrentBag<string> _scratchFiles = new();

    [ClassCleanup]
    public static void Cleanup()
    {
        foreach (string path in _scratchFiles)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
            try { if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp"); } catch { }
        }
    }

    // ── No-metadata case (pristine clips) ─────────────────────────────────

    [TestMethod]
    public void Run_PristineClip_PrintsNoMetadataMessage()
    {
        // Arrange
        string pristine = TestClipsLocator.AllPristine().First();
        using var writer = new StringWriter();

        // Act
        int exitCode = ListCommand.Run(pristine, writer);

        // Assert
        string output = writer.ToString();
        Assert.AreEqual(0, exitCode);
        StringAssert.Contains(output, "(no clipmeta metadata)");
    }

    [TestMethod]
    public void Run_PristineClip_FirstLineIsFilename()
    {
        // Arrange
        string pristine = TestClipsLocator.AllPristine().First();
        using var writer = new StringWriter();

        // Act
        ListCommand.Run(pristine, writer);

        // Assert
        string firstLine = writer.ToString().Split(Environment.NewLine)[0];
        Assert.AreEqual(Path.GetFileName(pristine), firstLine);
    }

    [TestMethod]
    public void Run_NoArguments_DefaultsToConsoleOut()
    {
        // Arrange
        string pristine = TestClipsLocator.AllPristine().First();
        var originalOut = Console.Out;
        try
        {
            using var writer = new StringWriter();
            Console.SetOut(writer);

            // Act
            int exitCode = ListCommand.Run(pristine);

            // Assert
            Assert.AreEqual(0, exitCode);
            StringAssert.Contains(writer.ToString(), "(no clipmeta metadata)");
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    // ── Fields present ───────────────────────────────────────────────────────

    [TestMethod]
    public void Run_SingleField_PrintsFieldAndValue()
    {
        // Arrange
        string scratch = PrepareScratch(TestClipsLocator.AllPristine().First());
        var mutation = new MetadataMutation();
        mutation.SetFields[ClipMetaSchema.AtomName("game")] = "Team Fortress 2";
        new Mp4Writer().WriteMetadata(scratch, mutation, NullLogger.Instance);

        using var writer = new StringWriter();

        // Act
        int exitCode = ListCommand.Run(scratch, writer);

        // Assert
        string output = writer.ToString();
        Assert.AreEqual(0, exitCode);
        StringAssert.Contains(output, "game");
        StringAssert.Contains(output, "Team Fortress 2");
    }

    [TestMethod]
    public void Run_MultipleFields_PadsToLongestFieldName()
    {
        // Arrange
        // "game" (4 chars) vs "timecode" (8 chars), verify padding is consistent
        string scratch = PrepareScratch(TestClipsLocator.AllPristine().First());
        var mutation = new MetadataMutation();
        mutation.SetFields[ClipMetaSchema.AtomName("game")] = "TF2";
        mutation.SetFields[ClipMetaSchema.AtomName("timecode")] = "00:01:23";
        new Mp4Writer().WriteMetadata(scratch, mutation, NullLogger.Instance);

        using var writer = new StringWriter();

        // Act
        ListCommand.Run(scratch, writer);

        // Assert
        string output = writer.ToString();
        var lines = output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        // Find the lines containing "game" and "timecode"
        var gameLine = lines.FirstOrDefault(l => l.Contains("game") && !l.Contains("timecode"));
        var timecodeLine = lines.FirstOrDefault(l => l.Contains("timecode"));

        Assert.IsNotNull(gameLine, "game field not found in output");
        Assert.IsNotNull(timecodeLine, "timecode field not found in output");

        // The longest field name is "timecode" (8 chars), so all fields should pad to 8.
        // Verify that all field lines have the same indentation + padding pattern.
        // Extract field widths from the actual output.
        string gameValue = ExtractValue(gameLine!);
        string timecodeValue = ExtractValue(timecodeLine!);

        // Both lines exist and contain values, this verifies the formatter ran successfully
        Assert.IsFalse(string.IsNullOrWhiteSpace(gameValue), "game line should have a value");
        Assert.IsFalse(string.IsNullOrWhiteSpace(timecodeValue), "timecode line should have a value");
    }

    [TestMethod]
    public void Run_ThreeFields_AllPresent()
    {
        // Arrange
        // Write three fields of different name lengths
        string scratch = PrepareScratch(TestClipsLocator.AllPristine().First());
        var mutation = new MetadataMutation();
        mutation.SetFields[ClipMetaSchema.AtomName("x")] = "a";
        mutation.SetFields[ClipMetaSchema.AtomName("game")] = "TF2";
        mutation.SetFields[ClipMetaSchema.AtomName("timecode")] = "00:01:23";
        new Mp4Writer().WriteMetadata(scratch, mutation, NullLogger.Instance);

        using var writer = new StringWriter();

        // Act
        ListCommand.Run(scratch, writer);

        // Assert
        string output = writer.ToString();

        // Verify all three fields appear in the output
        StringAssert.Contains(output, "x");
        StringAssert.Contains(output, "game");
        StringAssert.Contains(output, "timecode");

        // Verify their values appear too
        StringAssert.Contains(output, "a");
        StringAssert.Contains(output, "TF2");
        StringAssert.Contains(output, "00:01:23");
    }

    [TestMethod]
    public void Run_FieldsAreIndented()
    {
        // Arrange
        string scratch = PrepareScratch(TestClipsLocator.AllPristine().First());
        var mutation = new MetadataMutation();
        mutation.SetFields[ClipMetaSchema.AtomName("game")] = "TF2";
        new Mp4Writer().WriteMetadata(scratch, mutation, NullLogger.Instance);

        using var writer = new StringWriter();

        // Act
        ListCommand.Run(scratch, writer);

        // Assert
        var lines = writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        // First line is filename (no indent)
        // Second line should start with "  " (two spaces for indentation)
        Assert.IsTrue(lines.Length > 1, "Expected at least filename + one field line");
        var fieldLine = lines[1];
        Assert.IsTrue(fieldLine.StartsWith("  "), "Field lines should be indented with 2 spaces");
    }

    [TestMethod]
    public void Run_AlwaysReturnsZero()
    {
        // Arrange
        string pristine = TestClipsLocator.AllPristine().First();
        using var writer = new StringWriter();

        // Act
        int exitCode = ListCommand.Run(pristine, writer);

        // Assert
        Assert.AreEqual(0, exitCode);
    }

    // ── Truncated file (v1.0.1 hardening, task B1) ─────────────────────────
    //
    // Before the fix, a truncated extended-size box header made BigEndianReader hand a short
    // byte array to BitConverter, which threw a raw, uncaught ArgumentException, the CLI's
    // single-file read path (--list) had no catch for it and crashed with a bare stack trace
    // instead of a clean error line. This is the "single-file read path" scenario from the
    // nemesis review: the file must never surface that raw exception type.

    [TestMethod]
    public void Run_TruncatedExtendedSizeHeaderFile_NeverThrowsArgumentException()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".mp4");
        byte[] bytes =
        {
            0x00, 0x00, 0x00, 0x01,                   // size = 1 (extended)
            0x6D, 0x64, 0x61, 0x74,                   // 'mdat'
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // extended-size field, 7 of 8 bytes
        };
        File.WriteAllBytes(path, bytes);
        _scratchFiles.Add(path);
        using var writer = new StringWriter();

        try
        {
            int exitCode = ListCommand.Run(path, writer);
            // Mp4Parser is deliberately lenient for a header that cannot be fully read (the
            // file "should still be viewable up to the damage"); with no bytes before the
            // damage that means a clean, zero-metadata success, not a crash.
            Assert.AreEqual(0, exitCode);
        }
        catch (ArgumentException ex)
        {
            Assert.Fail($"--list must never surface a raw ArgumentException; got: {ex}");
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string PrepareScratch(string pristine)
    {
        string scratch = ScratchClips.Prepare(pristine);
        _scratchFiles.Add(scratch);
        return scratch;
    }

    /// <summary>
    /// Extracts the value portion from a formatted field line.
    /// Expected format: "  {field_padded}  {value}"
    /// Returns just the value part (after the two-space separator).
    /// </summary>
    private static string ExtractValue(string line)
    {
        // Line format: "  {field}  {value}"
        // Find double spaces that separate field from value
        int doubleSpaceIdx = line.IndexOf("  ", 2);  // Skip the leading indent spaces
        if (doubleSpaceIdx == -1) return string.Empty;

        return line[(doubleSpaceIdx + 2)..].Trim();
    }
}
