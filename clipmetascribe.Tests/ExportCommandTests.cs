using ClipMetaCore.Read;
using ClipMetaScribe.Commands;

namespace ClipMetaScribe.Tests;

[TestClass]
public class ExportCommandTests
{
    private static ExportRecord MakeRecord(string path, params (string Field, string Value)[] fields)
        => new ExportRecord(path, fields.ToList());

    [TestMethod]
    public void Run_Json_OutputsBrackets()
    {
        var records = new List<ExportRecord> { MakeRecord("clip.mp4", ("game", "TF2")) };
        using var writer = new StringWriter();

        ExportCommand.Run(records, "json", writer);

        string output = writer.ToString();
        StringAssert.Contains(output, "[");
        StringAssert.Contains(output, "]");
    }

    [TestMethod]
    public void Run_Json_ContainsFileKey()
    {
        var records = new List<ExportRecord> { MakeRecord("clip.mp4", ("game", "TF2")) };
        using var writer = new StringWriter();

        ExportCommand.Run(records, "json", writer);

        StringAssert.Contains(writer.ToString(), "\"file\":");
    }

    [TestMethod]
    public void Run_Json_ContainsFieldNameAndValue()
    {
        var records = new List<ExportRecord> { MakeRecord("clip.mp4", ("game", "Team Fortress 2")) };
        using var writer = new StringWriter();

        ExportCommand.Run(records, "json", writer);

        string output = writer.ToString();
        StringAssert.Contains(output, "\"game\":");
        StringAssert.Contains(output, "Team Fortress 2");
    }

    [TestMethod]
    public void Run_Json_EmptyRecords_OutputsEmptyArray()
    {
        using var writer = new StringWriter();

        ExportCommand.Run(new List<ExportRecord>(), "json", writer);

        string output = writer.ToString().Trim();
        Assert.IsTrue(output.StartsWith("["));
        Assert.IsTrue(output.EndsWith("]"));
    }

    [TestMethod]
    public void Run_Json_EscapesBackslashesInPath()
    {
        var records = new List<ExportRecord> { MakeRecord(@"C:\clips\clip.mp4") };
        using var writer = new StringWriter();

        ExportCommand.Run(records, "json", writer);

        StringAssert.Contains(writer.ToString(), @"C:\\clips\\clip.mp4");
    }

    [TestMethod]
    public void Run_Json_ReturnsZero()
    {
        using var writer = new StringWriter();

        int exitCode = ExportCommand.Run(new List<ExportRecord>(), "json", writer);

        Assert.AreEqual(0, exitCode);
    }

    [TestMethod]
    public void Run_Csv_FirstLineIsHeader()
    {
        var records = new List<ExportRecord> { MakeRecord("clip.mp4", ("game", "TF2")) };
        using var writer = new StringWriter();

        ExportCommand.Run(records, "csv", writer);

        string firstLine = writer.ToString().Split(Environment.NewLine)[0];
        Assert.IsTrue(firstLine.StartsWith("file,"), $"First line was: {firstLine}");
        StringAssert.Contains(firstLine, "game");
    }

    [TestMethod]
    public void Run_Csv_HeaderContainsAllKnownFields()
    {
        using var writer = new StringWriter();

        ExportCommand.Run(new List<ExportRecord>(), "csv", writer);

        string firstLine = writer.ToString().Split(Environment.NewLine)[0];
        foreach (string field in new[] { "game", "players", "tags", "timecode", "rating", "notes" })
            StringAssert.Contains(firstLine, field);
    }

    [TestMethod]
    public void Run_Csv_DataRowContainsFilePath()
    {
        var records = new List<ExportRecord> { MakeRecord("clip.mp4", ("game", "TF2")) };
        using var writer = new StringWriter();

        ExportCommand.Run(records, "csv", writer);

        string[] lines = writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        StringAssert.Contains(lines[1], "clip.mp4");
    }

    [TestMethod]
    public void Run_Csv_DataRowContainsFieldValue()
    {
        var records = new List<ExportRecord> { MakeRecord("clip.mp4", ("game", "Team Fortress 2")) };
        using var writer = new StringWriter();

        ExportCommand.Run(records, "csv", writer);

        StringAssert.Contains(writer.ToString(), "Team Fortress 2");
    }

    [TestMethod]
    public void Run_Csv_EmptyRecords_OutputsHeaderOnly()
    {
        using var writer = new StringWriter();

        ExportCommand.Run(new List<ExportRecord>(), "csv", writer);

        string[] lines = writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.AreEqual(1, lines.Length);
        Assert.IsTrue(lines[0].StartsWith("file,"));
    }

    [TestMethod]
    public void Run_Csv_QuotesValuesContainingCommas()
    {
        var records = new List<ExportRecord> { MakeRecord("clip.mp4", ("notes", "hello, world")) };
        using var writer = new StringWriter();

        ExportCommand.Run(records, "csv", writer);

        StringAssert.Contains(writer.ToString(), "\"hello, world\"");
    }

    [TestMethod]
    public void Run_Csv_ReturnsZero()
    {
        using var writer = new StringWriter();

        int exitCode = ExportCommand.Run(new List<ExportRecord>(), "csv", writer);

        Assert.AreEqual(0, exitCode);
    }

    [TestMethod]
    public void Run_UnknownFormat_ReturnsOne()
    {
        using var writer = new StringWriter();

        int exitCode = ExportCommand.Run(new List<ExportRecord>(), "xml", writer);

        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public void Run_Json_MultipleRecords_HasCommaBetweenObjects()
    {
        var records = new List<ExportRecord>
        {
            MakeRecord("clip1.mp4", ("game", "TF2")),
            MakeRecord("clip2.mp4", ("game", "CS2")),
        };
        using var writer = new StringWriter();

        ExportCommand.Run(records, "json", writer);

        string output = writer.ToString().Replace(" ", "").Replace("\n", "").Replace("\r", "");
        StringAssert.Contains(output, "},{");
    }

    [TestMethod]
    public void Run_Csv_CustomField_AppearsAsExtraColumn()
    {
        var records = new List<ExportRecord> { MakeRecord("clip.mp4", ("scene", "intro")) };
        using var writer = new StringWriter();

        ExportCommand.Run(records, "csv", writer);

        string output = writer.ToString();
        string[] lines = output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        StringAssert.Contains(lines[0], "scene");    // header has the custom field
        StringAssert.Contains(lines[1], "intro");    // data row has the value
    }

    [TestMethod]
    public void Run_DefaultOutput_UsesConsoleOut()
    {
        var records = new List<ExportRecord> { MakeRecord("clip.mp4") };
        var originalOut = Console.Out;
        try
        {
            using var writer = new StringWriter();
            Console.SetOut(writer);

            int exitCode = ExportCommand.Run(records, "json");

            Assert.AreEqual(0, exitCode);
            StringAssert.Contains(writer.ToString(), "[");
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }
}
