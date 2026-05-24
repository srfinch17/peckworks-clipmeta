using ClipMetaCore.Logging;
using ClipMetaCore.Schema;
using ClipMetaCore.Write;
using ClipMetaScribe.Commands;
using ClipMetaScribe.Tests.Helpers;

namespace ClipMetaScribe.Tests;

[TestClass]
public class StatsCommandTests
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

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string PrepareScratch(string pristine)
    {
        string scratch = ScratchClips.Prepare(pristine);
        _scratchFiles.Add(scratch);
        return scratch;
    }

    private static string WriteFields(string pristine, Dictionary<string, string> fields)
    {
        string scratch = PrepareScratch(pristine);
        var mutation = new MetadataMutation();
        foreach (var (atomName, value) in fields)
            mutation.SetFields[atomName] = value;
        new Mp4Writer().WriteMetadata(scratch, mutation, NullLogger.Instance);
        return scratch;
    }

    // ── Pristine clip (no metadata) ──────────────────────────────────────────

    [TestMethod]
    public void Run_PristineClip_FirstLineContainsFilename()
    {
        string pristine = TestClipsLocator.AllPristine().First();
        using var writer = new StringWriter();

        StatsCommand.Run(pristine, writer);

        string firstLine = writer.ToString().Split(Environment.NewLine)[0];
        StringAssert.Contains(firstLine, Path.GetFileName(pristine));
    }

    [TestMethod]
    public void Run_PristineClip_FirstLineContainsFileSize()
    {
        string pristine = TestClipsLocator.AllPristine().First();
        using var writer = new StringWriter();

        StatsCommand.Run(pristine, writer);

        string firstLine = writer.ToString().Split(Environment.NewLine)[0];
        // Size label appears in parentheses, e.g. "(14.2 MB)" or "(512 KB)"
        StringAssert.Contains(firstLine, "(");
        StringAssert.Contains(firstLine, ")");
    }

    [TestMethod]
    public void Run_PristineClip_PrintsNoMetadataMessage()
    {
        string pristine = TestClipsLocator.AllPristine().First();
        using var writer = new StringWriter();

        StatsCommand.Run(pristine, writer);

        StringAssert.Contains(writer.ToString(), "(no clipmeta metadata)");
    }

    [TestMethod]
    public void Run_AlwaysReturnsZero()
    {
        string pristine = TestClipsLocator.AllPristine().First();
        using var writer = new StringWriter();

        int exitCode = StatsCommand.Run(pristine, writer);

        Assert.AreEqual(0, exitCode);
    }

    // ── Clip with metadata ───────────────────────────────────────────────────

    [TestMethod]
    public void Run_AllKnownFieldsSet_ShowsAllFieldNames()
    {
        string pristine = TestClipsLocator.AllPristine().First();
        string scratch = WriteFields(pristine, new()
        {
            [ClipMetaSchema.AtomName(ClipMetaSchema.Game)]     = "Team Fortress 2",
            [ClipMetaSchema.AtomName(ClipMetaSchema.Players)]  = "Ben|Scott",
            [ClipMetaSchema.AtomName(ClipMetaSchema.Tags)]     = "headshot",
            [ClipMetaSchema.AtomName(ClipMetaSchema.Timecode)] = "00:01:23",
            [ClipMetaSchema.AtomName(ClipMetaSchema.Rating)]   = "4",
            [ClipMetaSchema.AtomName(ClipMetaSchema.Notes)]    = "great clip",
        });
        using var writer = new StringWriter();

        StatsCommand.Run(scratch, writer);

        string output = writer.ToString();
        StringAssert.Contains(output, "game");
        StringAssert.Contains(output, "players");
        StringAssert.Contains(output, "tags");
        StringAssert.Contains(output, "timecode");
        StringAssert.Contains(output, "rating");
        StringAssert.Contains(output, "notes");
    }

    [TestMethod]
    public void Run_AllKnownFieldsSet_NoUnsetLine()
    {
        string pristine = TestClipsLocator.AllPristine().First();
        string scratch = WriteFields(pristine, new()
        {
            [ClipMetaSchema.AtomName(ClipMetaSchema.Game)]     = "TF2",
            [ClipMetaSchema.AtomName(ClipMetaSchema.Players)]  = "Ben",
            [ClipMetaSchema.AtomName(ClipMetaSchema.Tags)]     = "headshot",
            [ClipMetaSchema.AtomName(ClipMetaSchema.Timecode)] = "00:01:23",
            [ClipMetaSchema.AtomName(ClipMetaSchema.Rating)]   = "4",
            [ClipMetaSchema.AtomName(ClipMetaSchema.Notes)]    = "notes",
        });
        using var writer = new StringWriter();

        StatsCommand.Run(scratch, writer);

        Assert.IsFalse(writer.ToString().Contains("Fields unset"),
            "Should not print 'Fields unset' when all known fields are set");
    }

    [TestMethod]
    public void Run_PartialFieldsSet_ShowsUnsetKnownFields()
    {
        string pristine = TestClipsLocator.AllPristine().First();
        // Write only game — the other 5 should appear as unset
        string scratch = WriteFields(pristine, new()
        {
            [ClipMetaSchema.AtomName(ClipMetaSchema.Game)] = "Team Fortress 2",
        });
        using var writer = new StringWriter();

        StatsCommand.Run(scratch, writer);

        string output = writer.ToString();
        StringAssert.Contains(output, "Fields unset");
        StringAssert.Contains(output, "players");
        StringAssert.Contains(output, "timecode");
    }

    [TestMethod]
    public void Run_CustomFieldSet_ShowsCustomFieldsLine()
    {
        string pristine = TestClipsLocator.AllPristine().First();
        string scratch = WriteFields(pristine, new()
        {
            [ClipMetaSchema.AtomName("event")] = "LAN party",
        });
        using var writer = new StringWriter();

        StatsCommand.Run(scratch, writer);

        string output = writer.ToString();
        StringAssert.Contains(output, "Custom fields");
        StringAssert.Contains(output, "event");
    }

    [TestMethod]
    public void Run_SchemaFieldExcluded_NotListedAsUserField()
    {
        // The write engine always writes a "schema" version field.
        // It should not appear in "Fields set:" or "Custom fields:".
        string pristine = TestClipsLocator.AllPristine().First();
        string scratch = WriteFields(pristine, new()
        {
            [ClipMetaSchema.AtomName(ClipMetaSchema.Game)] = "TF2",
        });
        using var writer = new StringWriter();

        StatsCommand.Run(scratch, writer);

        string output = writer.ToString();
        // "schema" should not appear on any output line
        // (it's an internal version marker, not a user-facing field)
        var lines = output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.IsFalse(
            lines.Any(l => l.Contains("schema")),
            $"Internal 'schema' field leaked into stats output:\n{output}");
    }
}
