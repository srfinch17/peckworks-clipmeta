using ClipMetaCore.Schema;

namespace ClipMetaScribe.Tests;

/// <summary>
/// Tests for clipmetascribe's argument parsing (<see cref="Program.BuildMutation"/>, made
/// internal for exactly this purpose). The bug class under test: a flag where a value should
/// be. Before validation, <c>--set notes --backup</c> silently stored the literal string
/// "--backup" as the notes text — while ALSO still enabling backup mode, because flag
/// detection scans the whole argument list independently of positional consumption.
/// </summary>
[TestClass]
public class ProgramArgumentTests
{
    private const string File = "clip.mp4";

    private static ClipMetaCore.Write.MetadataMutation Build(params string[] args)
        => Program.BuildMutation(args, File, dryRun: false, backup: false);

    // ── Swallowed-flag detection ───────────────────────────────────────────────

    [TestMethod]
    public void Set_FlagWhereValueExpected_Throws()
    {
        // The canonical mistake: user forgot the value, next flag slides into its place.
        var ex = Assert.ThrowsExactly<ArgumentException>(() =>
            Build(File, "--set", "notes", "--backup"));
        StringAssert.Contains(ex.Message, "--backup", "error should name the misplaced flag");
        StringAssert.Contains(ex.Message, "--set <field> <value>", "error should show usage");
    }

    [TestMethod]
    public void Set_FlagWhereFieldExpected_Throws()
    {
        Assert.ThrowsExactly<ArgumentException>(() => Build(File, "--set", "--dry-run", "x"));
    }

    [TestMethod]
    public void Append_FlagWhereValueExpected_Throws()
    {
        Assert.ThrowsExactly<ArgumentException>(() => Build(File, "--append", "tags", "--yes"));
    }

    [TestMethod]
    public void Clear_FlagWhereFieldExpected_Throws()
    {
        Assert.ThrowsExactly<ArgumentException>(() => Build(File, "--clear", "--verbose"));
    }

    // ── Missing trailing arguments ─────────────────────────────────────────────

    [TestMethod]
    public void Set_MissingValueAtEndOfArgs_Throws()
    {
        // Previously "--set tags" at the end of the line was silently IGNORED — the user
        // thought they tagged the clip and nothing happened. Now it is a hard error.
        var ex = Assert.ThrowsExactly<ArgumentException>(() => Build(File, "--set", "tags"));
        StringAssert.Contains(ex.Message, "--set");
    }

    [TestMethod]
    public void Set_MissingFieldAndValue_Throws()
    {
        Assert.ThrowsExactly<ArgumentException>(() => Build(File, "--set"));
    }

    [TestMethod]
    public void Clear_MissingField_Throws()
    {
        Assert.ThrowsExactly<ArgumentException>(() => Build(File, "--clear"));
    }

    // ── Expressiveness preserved: only KNOWN flags are rejected as values ──────

    [TestMethod]
    public void Set_DashyButNotAFlag_ValueAccepted()
    {
        // Values that merely look dashy are legitimate — e.g. notes containing an em-dash
        // decoration. Only exact (case-insensitive) matches of this tool's flags reject.
        var mutation = Build(File, "--set", "notes", "--great clip--");
        Assert.AreEqual("--great clip--",
            mutation.SetFields[ClipMetaSchema.AtomName("notes")]);
    }

    [TestMethod]
    public void Set_KnownFlagRejectedCaseInsensitively()
    {
        // Flag matching is case-insensitive everywhere else in the CLI, so the swallowed-flag
        // check must be too — "--BACKUP" still activates backup mode.
        Assert.ThrowsExactly<ArgumentException>(() => Build(File, "--set", "notes", "--BACKUP"));
    }

    // ── Normal operation still parses correctly ────────────────────────────────

    [TestMethod]
    public void MixedOperations_AllCollected()
    {
        var mutation = Build(File,
            "--set", "game", "TF2",
            "--append", "tags", "headshot",
            "--clear", "notes");

        Assert.AreEqual("TF2", mutation.SetFields[ClipMetaSchema.AtomName("game")]);
        Assert.AreEqual("headshot", mutation.AppendFields[ClipMetaSchema.AtomName("tags")]);
        Assert.IsTrue(mutation.DeleteFields.Contains(ClipMetaSchema.AtomName("notes")));
    }

    [TestMethod]
    public void FlagNames_MatchCaseInsensitively()
    {
        // ContainsFlag/GetFlag have always been case-insensitive; BuildMutation used to be
        // case-SENSITIVE, so "--SET game TF2" was silently ignored. Now consistent.
        var mutation = Build(File, "--SET", "game", "TF2");
        Assert.AreEqual("TF2", mutation.SetFields[ClipMetaSchema.AtomName("game")]);
    }
}
