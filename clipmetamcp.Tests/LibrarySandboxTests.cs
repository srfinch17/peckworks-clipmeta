using System.Diagnostics;
using System.Text.Json.Nodes;
using ClipMetaMcp.Tests.Helpers;
using ClipMetaMcp.Tools;

namespace ClipMetaMcp.Tests;

/// <summary>
/// Regression tests for the 2026-06-11 adversarial sandbox review: junction escapes (F1),
/// alternate-data-stream suffix bypass (F2), and drive-root containment breakage (F3).
/// </summary>
[TestClass]
public class LibrarySandboxTests
{
    private string _tempDir = null!;
    private readonly List<string> _junctions = [];

    [TestInitialize]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void TearDown()
    {
        // Directory.Delete(recursive) refuses junctions (UnauthorizedAccessException) —
        // remove each link itself first (non-recursive delete removes the reparse point
        // without touching its target), then the rest of the tree.
        foreach (string junction in _junctions)
        {
            try
            {
                if (Directory.Exists(junction))
                {
                    new DirectoryInfo(junction).Attributes = FileAttributes.Directory;
                    Directory.Delete(junction, recursive: false);
                }
            }
            catch (IOException) { /* best effort; the recursive delete below gets a chance too */ }
            catch (UnauthorizedAccessException) { }
        }
        _junctions.Clear();

        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private static JsonObject CallGetMetadata(string libraryRoot, string path)
    {
        var responses = McpHarness.Run(libraryRoot,
            McpHarness.InitializeRequest,
            McpHarness.ToolCall(2, "clip_get_metadata", new JsonObject { ["path"] = path }));
        return (JsonObject)responses[1]["result"]!;
    }

    private static string ErrorText(JsonObject result) =>
        result["content"]![0]!["text"]!.GetValue<string>();

    // ── F3: drive-root library must not refuse everything ──────────────────────────────

    [TestMethod]
    public void IsContained_DriveRootLibrary_AcceptsFilesOnTheDrive()
    {
        // TrimEndingDirectorySeparator keeps the separator on a root, so naive
        // Root + separator built "C:\\\\" and refused every clip on a whole-drive library.
        Assert.IsTrue(LibrarySandbox.IsContained(@"C:\clips\game.mp4", @"C:\"));
    }

    [TestMethod]
    public void IsContained_NormalRoot_AcceptsContainedFile()
    {
        Assert.IsTrue(LibrarySandbox.IsContained(@"C:\clips\sub\game.mp4", @"C:\clips"));
    }

    [TestMethod]
    public void IsContained_SiblingPrefixDirectory_IsRejected()
    {
        Assert.IsFalse(LibrarySandbox.IsContained(@"C:\clips-evil\game.mp4", @"C:\clips"));
    }

    [TestMethod]
    public void IsContained_RootItself_IsRejected()
    {
        // The root is a directory, never a clip.
        Assert.IsFalse(LibrarySandbox.IsContained(@"C:\clips", @"C:\clips"));
    }

    // ── F2: alternate-data-stream syntax must be refused ───────────────────────────────

    [TestMethod]
    public void GetMetadata_AlternateDataStreamPath_IsRefused()
    {
        // "real.mp4:payload.mp4" satisfies a naive .mp4 suffix check with the STREAM name
        // while opening arbitrary hidden content.
        string clip = Path.Combine(_tempDir, "real.mp4");
        File.WriteAllBytes(clip, [0, 0, 0, 0]);

        JsonObject result = CallGetMetadata(_tempDir, clip + ":payload.mp4");

        Assert.IsTrue(result["isError"]?.GetValue<bool>());
        StringAssert.Contains(ErrorText(result), "alternate-data-stream");
    }

    [TestMethod]
    public void GetMetadata_AdsOnNonMp4Base_IsRefused()
    {
        string textFile = Path.Combine(_tempDir, "notes.txt");
        File.WriteAllText(textFile, "not a clip");

        JsonObject result = CallGetMetadata(_tempDir, textFile + ":x.mp4");

        Assert.IsTrue(result["isError"]?.GetValue<bool>());
        StringAssert.Contains(ErrorText(result), "alternate-data-stream");
    }

    // ── F1: junction inside the library pointing outside it must not tunnel through ────

    [TestMethod]
    public void GetMetadata_ThroughJunctionPointingOutsideLibrary_IsRefused()
    {
        // Layout: _tempDir\outside\secret.mp4  and  _tempDir\library\vault → junction to outside.
        // Lexically, library\vault\secret.mp4 is inside the library; physically it is not.
        string outside = Path.Combine(_tempDir, "outside");
        string library = Path.Combine(_tempDir, "library");
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(library);
        File.WriteAllBytes(Path.Combine(outside, "secret.mp4"), [0, 0, 0, 0]);

        string junction = Path.Combine(library, "vault");
        CreateJunctionOrInconclusive(junction, outside);

        JsonObject result = CallGetMetadata(library, Path.Combine(junction, "secret.mp4"));

        Assert.IsTrue(result["isError"]?.GetValue<bool>(),
            "a junction must not tunnel reads outside the library");
        StringAssert.Contains(ErrorText(result), "outside the configured clips library");
    }

    [TestMethod]
    public void GetMetadata_ThroughJunctionPointingInsideLibrary_StillWorks()
    {
        // A junction whose target is INSIDE the library is legitimate and must keep working —
        // the canonicalization must not be a blanket reparse-point ban (cloud-placeholder files
        // are reparse points too).
        string library = Path.Combine(_tempDir, "library");
        string realDir = Path.Combine(library, "real");
        Directory.CreateDirectory(realDir);
        File.WriteAllBytes(Path.Combine(realDir, "clip.mp4"), [0, 0, 0, 0]);

        string junction = Path.Combine(library, "alias");
        CreateJunctionOrInconclusive(junction, realDir);

        JsonObject result = CallGetMetadata(library, Path.Combine(junction, "clip.mp4"));

        // The 4-byte fake parses to an empty tree (lenient parser), so success here means the
        // sandbox allowed the read; only the containment verdict is under test.
        Assert.IsNull(result["isError"],
            $"in-library junction was wrongly refused: {result.ToJsonString()}");
    }

    [TestMethod]
    public void GetMetadata_LibraryRootItselfBehindJunction_AcceptsItsClips()
    {
        // The CONFIGURED root may be a junction (relocated-folder setups). Clips resolve to the
        // junction's target; containment must compare canonical-to-canonical or every
        // legitimate clip would be refused.
        string realLibrary = Path.Combine(_tempDir, "real-library");
        Directory.CreateDirectory(realLibrary);
        File.WriteAllBytes(Path.Combine(realLibrary, "clip.mp4"), [0, 0, 0, 0]);

        string junctionRoot = Path.Combine(_tempDir, "linked-library");
        CreateJunctionOrInconclusive(junctionRoot, realLibrary);

        JsonObject result = CallGetMetadata(junctionRoot, "clip.mp4");

        Assert.IsNull(result["isError"],
            $"clip under a junction-root library was wrongly refused: {result.ToJsonString()}");
    }

    /// <summary>
    /// Creates a directory junction (no admin rights needed, unlike symlinks) or marks the test
    /// inconclusive on filesystems where mklink /J is unavailable (e.g. non-NTFS temp).
    /// Registers the junction for explicit removal in TearDown.
    /// </summary>
    private void CreateJunctionOrInconclusive(string junctionPath, string targetPath)
    {
        _junctions.Add(junctionPath);
        var startInfo = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junctionPath}\" \"{targetPath}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using Process? process = Process.Start(startInfo);
        process!.WaitForExit(10_000);

        if (process.ExitCode != 0 || !Directory.Exists(junctionPath))
            Assert.Inconclusive("could not create a directory junction in the temp directory");
    }
}
