using ClipMetaView;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClipMetaView.Tests;

/// <summary>
/// Tests for the flag-aware arg grammar added to <see cref="AppRunner.RunAsync"/>:
/// <c>--json</c> (either position relative to the path) and <c>--definitions</c>.
/// </summary>
[TestClass]
public class AppRunnerTests
{
    [TestMethod]
    public async Task Definitions_EmitsJsonDictionary_NoPathNeeded()
    {
        var sw = new StringWriter();
        int code = await AppRunner.RunAsync(new[] { "--definitions" }, sw);
        Assert.AreEqual(AppRunner.ExitSuccess, code);
        string outp = sw.ToString();
        StringAssert.Contains(outp, "\"moov\":");
        StringAssert.Contains(outp, "\"friendlyName\":\"Movie\"");
    }

    [TestMethod]
    public async Task Json_WithPath_EmitsBoxTree_EitherFlagPosition()
    {
        // Reuses the same graceful-skip pristine-clip pattern as ProgramIntegrationTests
        // (TestClips.cs); this project has no MinimalMp4Builder, unlike clipmetascribe.Tests.
        string clip = TestClips.All().First();
        var a = new StringWriter();
        var b = new StringWriter();
        Assert.AreEqual(AppRunner.ExitSuccess, await AppRunner.RunAsync(new[] { clip, "--json" }, a));
        Assert.AreEqual(AppRunner.ExitSuccess, await AppRunner.RunAsync(new[] { "--json", clip }, b));
        StringAssert.Contains(a.ToString(), "\"boxes\":");
        Assert.AreEqual(a.ToString(), b.ToString(), "flag position must not change output");
    }

    [TestMethod]
    public async Task JsonAndDefinitions_Together_IsBadArgs()
    {
        var sw = new StringWriter();
        int code = await AppRunner.RunAsync(new[] { "--json", "--definitions" }, sw);
        Assert.AreEqual(AppRunner.ExitBadArgs, code);
    }

    [TestMethod]
    public async Task UnknownFlag_IsBadArgs()
    {
        var sw = new StringWriter();
        int code = await AppRunner.RunAsync(new[] { "--frobnicate" }, sw);
        Assert.AreEqual(AppRunner.ExitBadArgs, code);
    }

    [TestMethod]
    public async Task Json_OnUnparseableFile_IsParseError_NoPartialJson()
    {
        // NOTE: garbage bytes alone do NOT trigger ExitParseError here. Mp4Parser is
        // deliberately lenient on truncated/garbage content (parses to an empty tree, no
        // exception raised; see docs/PITFALLS.md "2026-06-11, Garbage bytes in an .mp4 parse
        // successfully to an empty tree" and the read-lenient/write-strict note in CLAUDE.md).
        // To exercise the real parse-failure path we exclusively lock the file so
        // Mp4Parser.ParseFile's own FileShare.Read open fails with a genuine IOException, the
        // same FileShare.None technique already used by ClipMetaIndexTests/ClipMetaFinderTests/
        // FindCommandTests elsewhere in this solution.
        string bad = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".mp4");
        File.WriteAllBytes(bad, new byte[] { 1, 2, 3 });
        var locker = new FileStream(bad, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        try
        {
            var sw = new StringWriter();
            int code = await AppRunner.RunAsync(new[] { bad, "--json" }, sw);
            Assert.AreEqual(AppRunner.ExitParseError, code);
            Assert.IsFalse(sw.ToString().Contains("\"boxes\""), "no partial JSON on parse failure");
        }
        finally
        {
            locker.Dispose();
            File.Delete(bad);
        }
    }
}
