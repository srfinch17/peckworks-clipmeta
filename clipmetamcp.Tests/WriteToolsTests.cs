using System.Security.Cryptography;
using System.Text.Json.Nodes;
using ClipMetaCore.Mp4;
using ClipMetaCore.Read;
using ClipMetaCore.Schema;
using ClipMetaMcp.Tests.Helpers;
using ClipMetaScribe.Tests.Helpers;

namespace ClipMetaMcp.Tests;

/// <summary>
/// End-to-end tests for the phase-3 write tools, driven through the full session → registry →
/// sandbox → Mp4Writer pipeline against real clips. Unlike the read-tool tests these MUST copy
/// a pristine clip per test, every test mutates its own file.
///
/// The safety contract under test (spec §3):
/// backup defaults ON, dry_run never touches bytes, clear_all demands confirm:true,
/// writes refuse without a configured library, and, the one that matters most to the user, 
/// the media bytes survive every MCP-driven write (proved by the independent integrity
/// scanner, source-linked from clipmetascribe.Tests).
/// </summary>
[TestClass]
public class WriteToolsTests
{
    private string _lib = null!;

    [TestInitialize]
    public void SetUp()
    {
        _lib = Path.Combine(Path.GetTempPath(), "clipmeta-p3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_lib);
    }

    [TestCleanup]
    public void TearDown()
    {
        if (Directory.Exists(_lib))
            Directory.Delete(_lib, recursive: true);
    }

    /// <summary>Copies the smallest pristine clip into the library and returns its path.</summary>
    private string PrepareClip(string fileName = "clip.mp4")
    {
        string dest = Path.Combine(_lib, fileName);
        File.Copy(TestClipsLocator.SmallestPristine(), dest);
        return dest;
    }

    private JsonObject Call(string tool, JsonObject arguments, string? libraryRoot = "lib")
    {
        string? root = libraryRoot == "lib" ? _lib : libraryRoot;
        var responses = McpHarness.Run(root,
            McpHarness.InitializeRequest,
            McpHarness.ToolCall(2, tool, arguments));
        return (JsonObject)responses[1]["result"]!;
    }

    private JsonObject ReadFields(string path)
    {
        JsonObject result = Call("clip_get_metadata", new JsonObject { ["path"] = path });
        Assert.IsNull(result["isError"]);
        return (JsonObject)result["structuredContent"]!["fields"]!;
    }

    private static JsonObject Structured(JsonObject result) => (JsonObject)result["structuredContent"]!;

    private static string ErrorText(JsonObject result) =>
        result["content"]![0]!["text"]!.GetValue<string>();

    private static void AssertOk(JsonObject result) =>
        Assert.IsNull(result["isError"], "expected success but got: " + ErrorText(result));

    private static void AssertRefused(JsonObject result, string messageFragment)
    {
        Assert.IsTrue(result["isError"]?.GetValue<bool>(), "expected a tool refusal");
        StringAssert.Contains(ErrorText(result), messageFragment);
    }

    private string[] BackupFiles() => Directory.GetFiles(_lib, "*.bak-*");

    private static IReadOnlyList<(string Field, string Value)> RawFields(string clip) =>
        ClipMetaReader.GetFields(Mp4Parser.ParseFile(clip));

    // ── clip_set_fields ──────────────────────────────────────────────────────────────────

    [TestMethod]
    public void SetFields_StampsProvenanceByDefault_OptOutSuppresses()
    {
        string clip = PrepareClip();
        AssertOk(Call("clip_set_fields", new JsonObject
        {
            ["path"] = clip,
            ["fields"] = new JsonObject { ["game"] = "Team Fortress 2" },
            ["backup"] = false,
        }));
        Assert.IsTrue(RawFields(clip).Any(f => f.Field == ClipMetaSchema.TaggedBy),
            "provenance is stamped by default");

        string optOut = PrepareClip("optout.mp4");
        AssertOk(Call("clip_set_fields", new JsonObject
        {
            ["path"] = optOut,
            ["fields"] = new JsonObject { ["game"] = "Team Fortress 2" },
            ["backup"] = false,
            ["stamp_provenance"] = false,
        }));
        Assert.IsFalse(RawFields(optOut).Any(f => f.Field == ClipMetaSchema.TaggedBy),
            "stamp_provenance:false suppresses the provenance stamp");
    }

    [TestMethod]
    public void DryRun_PreviewMatchesRealWrite_OnAFieldThatAlreadyHasData()
    {
        string clip = PrepareClip();
        // Seed existing data so preview-vs-actual matters, the bug only showed when the field
        // already had a value (dry_run read the UNCHANGED file back, so it showed current state).
        AssertOk(Call("clip_set_fields", new JsonObject
        {
            ["path"] = clip,
            ["fields"] = new JsonObject { ["tags"] = "alpha|beta", ["notes"] = "first moment" },
            ["backup"] = false,
        }));

        var change = new JsonObject { ["tags"] = "gamma", ["game"] = "Team Fortress 2" };

        JsonObject dry = Call("clip_set_fields", new JsonObject
        {
            ["path"] = clip, ["fields"] = change.DeepClone(), ["dry_run"] = true,
        });
        AssertOk(dry);
        Assert.IsTrue(Structured(dry)["dryRun"]!.GetValue<bool>());
        var dryFields = (JsonObject)Structured(dry)["fields"]!;

        JsonObject real = Call("clip_set_fields", new JsonObject
        {
            ["path"] = clip, ["fields"] = change.DeepClone(), ["backup"] = false,
        });
        AssertOk(real);
        var realFields = (JsonObject)Structured(real)["fields"]!;

        Assert.AreEqual(realFields.Count, dryFields.Count,
            "dry-run preview must list the same fields a real write produces");
        foreach (var kv in realFields)
            Assert.AreEqual(kv.Value!.GetValue<string>(), dryFields[kv.Key]?.GetValue<string>(),
                $"field '{kv.Key}': dry-run preview must equal the real write's value");
    }

    [TestMethod]
    public void SetFields_WritesValues_VerifiedByIndependentReRead()
    {
        string clip = PrepareClip();

        JsonObject result = Call("clip_set_fields", new JsonObject
        {
            ["path"] = clip,
            ["fields"] = new JsonObject
            {
                ["game"] = "Team Fortress 2",
                ["tags"] = "win|comeback",
                ["map"] = "2fort",
            },
        });

        AssertOk(result);
        JsonObject s = Structured(result);
        Assert.IsFalse(s["dryRun"]!.GetValue<bool>());
        CollectionAssert.AreEquivalent(
            new[] { "game", "tags", "map" },
            s["setFields"]!.AsArray().Select(n => n!.GetValue<string>()).ToList());
        // The response's fields block is a post-write read-back, but verify independently too.
        JsonObject fields = ReadFields(clip);
        Assert.AreEqual("Team Fortress 2", fields["game"]!.GetValue<string>());
        Assert.AreEqual("win|comeback", fields["tags"]!.GetValue<string>());
    }

    [TestMethod]
    public void SetFields_BackupDefaultsOn_TimestampedSiblingCreated()
    {
        string clip = PrepareClip();
        long originalLength = new FileInfo(clip).Length;

        JsonObject result = Call("clip_set_fields", new JsonObject
        {
            ["path"] = clip,
            ["fields"] = new JsonObject { ["game"] = "TF2" },
        });

        AssertOk(result);
        string? backupPath = Structured(result)["backupPath"]?.GetValue<string>();
        Assert.IsNotNull(backupPath, "backup must be on by default");
        StringAssert.Contains(backupPath, ".bak-");
        Assert.IsTrue(File.Exists(backupPath));
        Assert.AreEqual(originalLength, new FileInfo(backupPath).Length,
            "the backup is the pre-write original");
    }

    [TestMethod]
    public void SetFields_BackupFalse_NoBakFileAndNullInResponse()
    {
        string clip = PrepareClip();

        JsonObject result = Call("clip_set_fields", new JsonObject
        {
            ["path"] = clip,
            ["fields"] = new JsonObject { ["game"] = "TF2" },
            ["backup"] = false,
        });

        AssertOk(result);
        Assert.IsNull(Structured(result)["backupPath"]?.GetValue<string>());
        Assert.AreEqual(0, BackupFiles().Length);
    }

    [TestMethod]
    public void SetFields_EmptyString_DeletesFieldAndReportsIt()
    {
        string clip = PrepareClip();
        AssertOk(Call("clip_set_fields", new JsonObject
        {
            ["path"] = clip,
            ["fields"] = new JsonObject { ["game"] = "TF2", ["notes"] = "temp" },
            ["backup"] = false,
        }));

        JsonObject result = Call("clip_set_fields", new JsonObject
        {
            ["path"] = clip,
            ["fields"] = new JsonObject { ["notes"] = "" },
            ["backup"] = false,
        });

        AssertOk(result);
        CollectionAssert.AreEqual(new[] { "notes" },
            Structured(result)["deletedFields"]!.AsArray().Select(n => n!.GetValue<string>()).ToList());
        JsonObject fields = ReadFields(clip);
        Assert.IsNull(fields["notes"], "empty string is the delete idiom");
        Assert.AreEqual("TF2", fields["game"]!.GetValue<string>(), "other fields untouched");
    }

    [TestMethod]
    public void SetFields_InvalidRating_IsFriendlyRefusal_SessionSurvives()
    {
        string clip = PrepareClip();

        var responses = McpHarness.Run(_lib,
            McpHarness.InitializeRequest,
            McpHarness.ToolCall(2, "clip_set_fields", new JsonObject
            {
                ["path"] = clip,
                ["fields"] = new JsonObject { ["rating"] = "five stars" },
                ["backup"] = false,
            }),
            McpHarness.Request(3, "ping"));

        var result = (JsonObject)responses[1]["result"]!;
        AssertRefused(result, "Invalid value");
        Assert.AreEqual(3, responses[2]["id"]?.GetValue<int>(), "session must survive the refusal");
        Assert.AreEqual(0, ReadFields(clip).Count, "nothing may have been written");
    }

    // ── clip_append_field ────────────────────────────────────────────────────────────────

    [TestMethod]
    public void AppendField_MergesIntoPipeList()
    {
        string clip = PrepareClip();
        AssertOk(Call("clip_set_fields", new JsonObject
        {
            ["path"] = clip,
            ["fields"] = new JsonObject { ["tags"] = "win" },
            ["backup"] = false,
        }));

        JsonObject result = Call("clip_append_field", new JsonObject
        {
            ["path"] = clip,
            ["field"] = "tags",
            ["value"] = "comeback",
            ["backup"] = false,
        });

        AssertOk(result);
        Assert.AreEqual("win|comeback", ReadFields(clip)["tags"]!.GetValue<string>());
    }

    // ── clip_clear_fields ────────────────────────────────────────────────────────────────

    [TestMethod]
    public void ClearFields_RemovesOnlyNamedFields()
    {
        string clip = PrepareClip();
        AssertOk(Call("clip_set_fields", new JsonObject
        {
            ["path"] = clip,
            ["fields"] = new JsonObject { ["game"] = "TF2", ["tags"] = "win" },
            ["backup"] = false,
        }));

        JsonObject result = Call("clip_clear_fields", new JsonObject
        {
            ["path"] = clip,
            ["fields"] = new JsonArray("tags"),
            ["backup"] = false,
        });

        AssertOk(result);
        JsonObject fields = ReadFields(clip);
        Assert.IsNull(fields["tags"]);
        Assert.AreEqual("TF2", fields["game"]!.GetValue<string>());
    }

    // ── clip_clear_all ───────────────────────────────────────────────────────────────────

    [TestMethod]
    public void ClearAll_WithoutConfirm_IsRefusedAndNothingChanges()
    {
        string clip = PrepareClip();
        AssertOk(Call("clip_set_fields", new JsonObject
        {
            ["path"] = clip,
            ["fields"] = new JsonObject { ["game"] = "TF2" },
            ["backup"] = false,
        }));

        // No confirm at all, and the string "true", both must refuse (literal boolean only).
        AssertRefused(Call("clip_clear_all", new JsonObject { ["path"] = clip }), "confirm:true");
        AssertRefused(Call("clip_clear_all", new JsonObject { ["path"] = clip, ["confirm"] = "true" }),
            "confirm:true");
        Assert.AreEqual("TF2", ReadFields(clip)["game"]!.GetValue<string>());
    }

    [TestMethod]
    public void ClearAll_WithConfirm_RemovesEverything()
    {
        string clip = PrepareClip();
        AssertOk(Call("clip_set_fields", new JsonObject
        {
            ["path"] = clip,
            ["fields"] = new JsonObject { ["game"] = "TF2", ["map"] = "2fort" },
            ["backup"] = false,
        }));

        JsonObject result = Call("clip_clear_all", new JsonObject
        {
            ["path"] = clip,
            ["confirm"] = true,
            ["backup"] = false,
        });

        AssertOk(result);
        Assert.IsTrue(Structured(result)["clearedAll"]!.GetValue<bool>());
        Assert.AreEqual(0, ReadFields(clip).Count,
            "clear-all must leave no clipmeta fields (including the schema stamp)");
    }

    // ── dry run ──────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void DryRun_LeavesEveryByteUntouched_NoBackup()
    {
        string clip = PrepareClip();
        byte[] before = SHA256.HashData(File.ReadAllBytes(clip));

        JsonObject result = Call("clip_set_fields", new JsonObject
        {
            ["path"] = clip,
            ["fields"] = new JsonObject { ["game"] = "TF2" },
            ["dry_run"] = true,
        });

        AssertOk(result);
        JsonObject s = Structured(result);
        Assert.IsTrue(s["dryRun"]!.GetValue<bool>());
        Assert.IsNull(s["backupPath"]?.GetValue<string>(), "a dry run must not create backups");
        CollectionAssert.AreEqual(before, SHA256.HashData(File.ReadAllBytes(clip)),
            "dry run must not change a single byte");
        Assert.AreEqual(0, BackupFiles().Length);
    }

    // ── sandbox ──────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void Write_NoRootConfigured_IsRefusedOutright()
    {
        // Reads are allowed anywhere when unconfigured; writes are NOT (spec §3).
        string clip = PrepareClip();

        JsonObject result = Call("clip_set_fields", new JsonObject
        {
            ["path"] = clip,
            ["fields"] = new JsonObject { ["game"] = "TF2" },
        }, libraryRoot: null);

        AssertRefused(result, "Writing is disabled");
    }

    [TestMethod]
    public void Write_PathOutsideRoot_IsRefused()
    {
        string clip = PrepareClip();
        string innerRoot = Path.Combine(_lib, "library");
        Directory.CreateDirectory(innerRoot);

        JsonObject result = Call("clip_set_fields", new JsonObject
        {
            ["path"] = clip,
            ["fields"] = new JsonObject { ["game"] = "TF2" },
        }, libraryRoot: innerRoot);

        AssertRefused(result, "outside the configured clips library");
    }

    // ── player roster advisory ───────────────────────────────────────────────────────────

    /// <summary>
    /// (a) An unknown player name (not in library vocab, not in roster) generates an
    /// "unknownPlayer" advisory, but the write STILL lands (soft, not a gate).
    /// </summary>
    [TestMethod]
    public void SetFields_UnknownPlayer_AdvisoryFires_WriteStillLands()
    {
        string clip = PrepareClip();
        // Library has one clip with no metadata → players vocab is empty.
        JsonObject result = Call("clip_set_fields", new JsonObject
        {
            ["path"] = clip,
            ["fields"] = new JsonObject { ["players"] = "miami element" },
            ["backup"] = false,
        });

        AssertOk(result);
        JsonObject s = Structured(result);

        // Advisory must be present with the right shape.
        var review = s["review"]?.AsArray();
        Assert.IsNotNull(review, "expected a 'review' array for an unknown player");
        Assert.AreEqual(1, review!.Count, "exactly one advisory entry");
        Assert.AreEqual("unknownPlayer", review[0]!["type"]!.GetValue<string>());
        Assert.AreEqual("miami element", review[0]!["token"]!.GetValue<string>());

        // knownPlayers must be present (an array; empty when vocab and roster are both empty).
        var entry = review[0]!.AsObject();
        Assert.IsTrue(entry.ContainsKey("knownPlayers"),
            "advisory must include a 'knownPlayers' array");
        Assert.AreEqual(0, entry["knownPlayers"]!.AsArray().Count,
            "knownPlayers must be empty when vocab and roster are both empty");

        // Write must have landed (soft advisory, not a gate).
        Assert.AreEqual("miami element", ReadFields(clip)["players"]!.GetValue<string>(),
            "players field must be written even when the advisory fires");
    }

    /// <summary>
    /// (b) Same unknown player but listed in the roster arg → no advisory.
    /// </summary>
    [TestMethod]
    public void SetFields_UnknownPlayer_SuppressedByRoster()
    {
        string clip = PrepareClip();
        JsonObject result = Call("clip_set_fields", new JsonObject
        {
            ["path"] = clip,
            ["fields"] = new JsonObject { ["players"] = "miami element" },
            ["roster"] = new JsonArray("miami element"),
            ["backup"] = false,
        });

        AssertOk(result);
        Assert.IsNull(Structured(result)["review"],
            "no advisory when the player is named in the roster");
    }

    /// <summary>
    /// (c) Player already in the library vocab (seeded first) → no advisory.
    /// </summary>
    [TestMethod]
    public void SetFields_KnownPlayer_NoAdvisory()
    {
        // Seed the vocab: write players=chuck to a clip so the library knows the name.
        string seed = PrepareClip("seed.mp4");
        AssertOk(Call("clip_set_fields", new JsonObject
        {
            ["path"] = seed,
            ["fields"] = new JsonObject { ["players"] = "chuck" },
            ["backup"] = false,
        }));

        // A second call with players=chuck should not trigger the advisory.
        string clip = PrepareClip("clip2.mp4");
        JsonObject result = Call("clip_set_fields", new JsonObject
        {
            ["path"] = clip,
            ["fields"] = new JsonObject { ["players"] = "chuck" },
            ["backup"] = false,
        });

        AssertOk(result);
        Assert.IsNull(Structured(result)["review"],
            "no advisory when the player is already in the library vocab");
    }

    /// <summary>
    /// (d) clip_append_field with field:"players" and an unknown name fires the advisory but
    /// the append still lands (soft advisory, not a gate). Mirrors test (a) for the append path.
    /// </summary>
    [TestMethod]
    public void AppendField_UnknownPlayer_AdvisoryFires_AppendStillLands()
    {
        string clip = PrepareClip();
        // Library has one clip with no metadata → players vocab is empty; no roster given.
        JsonObject result = Call("clip_append_field", new JsonObject
        {
            ["path"] = clip,
            ["field"] = "players",
            ["value"] = "newguy",
            ["backup"] = false,
        });

        AssertOk(result);
        JsonObject s = Structured(result);

        // Advisory must be present.
        var review = s["review"]?.AsArray();
        Assert.IsNotNull(review, "expected a 'review' array for an unknown player in append");
        Assert.AreEqual(1, review!.Count, "exactly one advisory entry");
        Assert.AreEqual("unknownPlayer", review[0]!["type"]!.GetValue<string>());
        Assert.AreEqual("newguy", review[0]!["token"]!.GetValue<string>());

        // Append must have landed.
        Assert.AreEqual("players", s["appendedField"]!.GetValue<string>(),
            "appendedField must confirm the field name");
        Assert.AreEqual("newguy", s["appendedValue"]!.GetValue<string>(),
            "appendedValue must confirm the appended value");
        Assert.AreEqual("newguy", ReadFields(clip)["players"]!.GetValue<string>(),
            "players field must be written even when the advisory fires");
    }

    // ── media integrity (the test that matters most) ─────────────────────────────────────

    [TestMethod]
    public void McpWriteLifecycle_MediaBytesIdentical_ByIndependentScanner()
    {
        // set → append → clear one → clear all, then prove the video/audio payload and every
        // chunk offset survived, using the scanner that shares no code with the writer.
        string pristine = TestClipsLocator.SmallestPristine();
        string clip = PrepareClip();

        AssertOk(Call("clip_set_fields", new JsonObject
        {
            ["path"] = clip,
            ["fields"] = new JsonObject { ["game"] = "TF2", ["tags"] = "a|b", ["rating"] = "5" },
            ["backup"] = false,
        }));
        AssertOk(Call("clip_append_field", new JsonObject
        {
            ["path"] = clip, ["field"] = "tags", ["value"] = "c", ["backup"] = false,
        }));
        AssertOk(Call("clip_clear_fields", new JsonObject
        {
            ["path"] = clip, ["fields"] = new JsonArray("rating"), ["backup"] = false,
        }));
        MediaIntegrityScanner.AssertMediaUnchanged(pristine, clip);

        AssertOk(Call("clip_clear_all", new JsonObject
        {
            ["path"] = clip, ["confirm"] = true, ["backup"] = false,
        }));
        MediaIntegrityScanner.AssertMediaUnchanged(pristine, clip);
    }
}
