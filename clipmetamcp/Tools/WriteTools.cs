using System.Text.Json.Nodes;
using ClipMetaCore;
using ClipMetaCore.Logging;
using ClipMetaCore.Mp4;
using ClipMetaCore.Read;
using ClipMetaCore.Schema;
using ClipMetaCore.Watching;
using ClipMetaCore.Write;

namespace ClipMetaMcp.Tools;

/// <summary>
/// Registers the write tools (phase 3). Every mutation goes through Core's
/// <see cref="Mp4Writer"/>, the engine with the temp-file/verify/<c>File.Replace</c> golden
/// rule, parser-coverage gate, and media-integrity guards, so these tools contain parameter
/// mapping and safety policy, never write logic.
///
/// Safety policy (spec §3):
/// - <c>backup</c> defaults ON, a timestamped sibling copy survives every write unless the
///   caller explicitly opts out. An LLM is the caller; the user's clips get belt and braces.
/// - <c>dry_run</c> reports what would change without touching the file.
/// - <c>clip_clear_all</c> additionally requires the literal argument <c>confirm: true</c>.
/// - Writes hard-require the configured library (<see cref="LibrarySandbox.ResolveWritePath"/>).
/// - Single-flight: one write at a time process-wide (risk R8), two concurrent rewrites of
///   the same file would race at <c>File.Replace</c>.
/// </summary>
public static class WriteTools
{
    /// <summary>
    /// Registers all write tools against the given sandbox. When <paramref name="ledger"/> is
    /// supplied, every successful metadata write marks the path in the ledger so
    /// <c>library_watching</c>'s gaming-mode signal can exclude clips ClipMeta itself tagged
    /// (they are not fresh user game-saves).
    /// </summary>
    public static void RegisterAll(
        ToolRegistry registry, LibrarySandbox sandbox, SelfActionLedger? ledger = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(sandbox);

        registry.Register(new ToolDefinition(
            "clip_set_fields",
            "Writes metadata into one MP4 game clip (stored inside the file, so tags travel " +
            "with it). 'fields' maps field names to string values, well-known fields: " +
            KnownFieldsSentence + "; any other name becomes a custom field. For searchability, put " +
            "people in 'players' and searchable nouns/moments (objects, places, events) in 'tags' " +
            "rather than burying them in free-text 'notes'. Setting a field " +
            "to an empty string DELETES it. Multi-value fields use pipe-delimited strings " +
            "(e.g. tags: \"win|comeback\"). 'rating' must be 1-5. 'timecode' is normalized to " +
            "HH:MM:SS: a bare number is seconds and a two-part value is MM:SS, so \"90\" and " +
            "\"1:30\" both become 00:01:30. A timestamped backup copy is kept next to the file " +
            "unless backup:false; dry_run:true previews without writing. " +
            "Name players up front in the optional 'roster' arg to reduce unknown-player advisories.",
            SetFieldsSchema(),
            args => SetFields(args, sandbox, ledger),
            clipPath => new JsonObject
            {
                ["path"] = clipPath,
                ["fields"] = new JsonObject { ["game"] = "TF2" },
                ["backup"] = false,
            }));

        registry.Register(new ToolDefinition(
            "clip_append_field",
            "Appends a value to one metadata field of an MP4 clip without disturbing what is " +
            "already there, for multi-value fields (players, tags, timecode) the new items " +
            "merge into the pipe-delimited list (duplicates removed); for text fields the " +
            "value is appended. Use clip_set_fields to replace a value outright. Timestamped " +
            "backup unless backup:false; dry_run:true previews.",
            AppendFieldSchema(),
            args => AppendField(args, sandbox, ledger),
            clipPath => new JsonObject
            {
                ["path"] = clipPath,
                ["field"] = "tags",
                ["value"] = "purity-probe",
                ["backup"] = false,
            }));

        registry.Register(new ToolDefinition(
            "clip_clear_fields",
            "Deletes the named metadata fields from one MP4 clip (the listed fields only, " +
            "other metadata is untouched). Clearing a field that is not set is not an error. " +
            "Timestamped backup unless backup:false; dry_run:true previews.",
            ClearFieldsSchema(),
            args => ClearFields(args, sandbox, ledger),
            clipPath => new JsonObject
            {
                ["path"] = clipPath,
                ["fields"] = new JsonArray("game"),
                ["backup"] = false,
            }));

        registry.Register(new ToolDefinition(
            "clip_clear_all",
            "Removes ALL clipmeta metadata from one MP4 clip. Destructive: requires the " +
            "explicit argument confirm:true, ask the user before calling this unless they " +
            "already clearly asked for a full wipe. Timestamped backup unless backup:false; " +
            "dry_run:true previews.",
            ClearAllSchema(),
            args => ClearAll(args, sandbox, ledger),
            clipPath => new JsonObject
            {
                ["path"] = clipPath,
                ["confirm"] = true,
                ["backup"] = false,
            }));

        registry.Register(new ToolDefinition(
            "library_list_backups",
            "Lists the timestamped backup copies the write tools created (named " +
            "<clip>.mp4.bak-<timestamp>), newest first, with the clip each belongs to, its size, " +
            "and when it was taken. Optional 'clip' limits the list to one clip's backups. " +
            "Read-only. Requires a configured clips library. Use this to find what " +
            "clip_restore_backup can restore, or what clip_prune_backups would remove.",
            ListBackupsSchema(),
            args => ListBackups(args, sandbox),
            // Listing is path-independent; the example just exercises the happy path.
            _ => new JsonObject()));

        registry.Register(new ToolDefinition(
            "clip_restore_backup",
            "Restores a clip from one of its backups, overwriting the clip's current contents. " +
            "Destructive (the current bytes are replaced), so it requires confirm:true, show " +
            "the user which backup (from library_list_backups) and confirm first. The backup is " +
            "validated as a complete MP4 before the swap; a corrupt backup is refused and the " +
            "current file left untouched. The backup file itself is kept.",
            RestoreBackupSchema(),
            args => RestoreBackup(args, sandbox),
            clipPath => new JsonObject
            {
                // A valid .bak-<stamp> name; the stdout-purity test creates a matching file.
                ["backup"] = clipPath + ".bak-20200101-000000",
                ["confirm"] = true,
            }));

        registry.Register(new ToolDefinition(
            "clip_prune_backups",
            "Deletes a clip's backup copies, keeping the newest 'keep' (default 0 = delete all). " +
            "Destructive and irreversible: requires confirm:true. Only files matching this clip's " +
            "<clip>.mp4.bak-<timestamp> backups are deleted, never the clip itself, never an " +
            "unrelated file. Use library_list_backups first to see what would be removed.",
            PruneBackupsSchema(),
            args => PruneBackups(args, sandbox),
            clipPath => new JsonObject
            {
                ["clip"] = clipPath,
                ["keep"] = 0,
                ["confirm"] = true,
            }));
    }

    /// <summary>Well-known field names in schema order, for tool descriptions.</summary>
    private static string KnownFieldsSentence => string.Join(", ", ClipMetaSchema.KnownFields);

    // ── JSON Schemas ─────────────────────────────────────────────────────────────────────

    /// <summary>The 'path' / 'backup' / 'dry_run' properties every write tool shares.</summary>
    private static JsonObject CommonWriteProperties() => new()
    {
        ["path"] = new JsonObject
        {
            ["type"] = "string",
            ["description"] = "Path to an .mp4 file inside the clips library. " +
                              "Absolute, or relative to the library root.",
        },
        ["backup"] = new JsonObject
        {
            ["type"] = "boolean",
            ["description"] = "Keep a timestamped backup copy next to the file (default true).",
        },
        ["dry_run"] = new JsonObject
        {
            ["type"] = "boolean",
            ["description"] = "Report what would change without modifying the file (default false).",
        },
        ["stamp_provenance"] = new JsonObject
        {
            ["type"] = "boolean",
            ["description"] = "Write a 'tagged_by: Peckworks ClipMeta' provenance marker into the " +
                              "file alongside the metadata (default true). Set false to opt out.",
        },
        ["roster"] = new JsonObject
        {
            ["type"] = "array",
            ["items"] = new JsonObject { ["type"] = "string" },
            ["description"] = "Optional: tonight's player names. A 'players' value outside this list " +
                              "and the library's known players is flagged (not blocked) so you can " +
                              "confirm it's a person and not a tag. Name players up front to reduce flags.",
        },
    };

    private static JsonObject SetFieldsSchema()
    {
        JsonObject properties = CommonWriteProperties();
        properties["fields"] = new JsonObject
        {
            ["type"] = "object",
            ["description"] = "Field name → string value. Empty string deletes the field.",
            ["additionalProperties"] = new JsonObject { ["type"] = "string" },
        };
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray("path", "fields"),
        };
    }

    private static JsonObject AppendFieldSchema()
    {
        JsonObject properties = CommonWriteProperties();
        properties["field"] = new JsonObject
        {
            ["type"] = "string",
            ["description"] = "Field to append to (e.g. tags, players, notes).",
        };
        properties["value"] = new JsonObject
        {
            ["type"] = "string",
            ["description"] = "Value to append. For multi-value fields this may itself be " +
                              "pipe-delimited to append several items.",
        };
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray("path", "field", "value"),
        };
    }

    private static JsonObject ClearFieldsSchema()
    {
        JsonObject properties = CommonWriteProperties();
        properties["fields"] = new JsonObject
        {
            ["type"] = "array",
            ["items"] = new JsonObject { ["type"] = "string" },
            ["description"] = "Names of the fields to delete.",
        };
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray("path", "fields"),
        };
    }

    private static JsonObject ClearAllSchema()
    {
        JsonObject properties = CommonWriteProperties();
        properties["confirm"] = new JsonObject
        {
            ["type"] = "boolean",
            ["description"] = "Must be literally true. Safety latch for a destructive operation.",
        };
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray("path", "confirm"),
        };
    }

    private static JsonObject ListBackupsSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["clip"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Optional .mp4 clip whose backups to list. Omit to list all backups.",
            },
        },
    };

    private static JsonObject RestoreBackupSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["backup"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Path to the .bak-<timestamp> backup file to restore from " +
                                  "(as reported by library_list_backups).",
            },
            ["confirm"] = new JsonObject
            {
                ["type"] = "boolean",
                ["description"] = "Must be literally true. Safety latch, restoring overwrites the clip.",
            },
        },
        ["required"] = new JsonArray("backup", "confirm"),
    };

    private static JsonObject PruneBackupsSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["clip"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "The .mp4 clip whose backups to prune.",
            },
            ["keep"] = new JsonObject
            {
                ["type"] = "integer",
                ["description"] = "How many of the newest backups to keep (default 0 = delete all).",
            },
            ["confirm"] = new JsonObject
            {
                ["type"] = "boolean",
                ["description"] = "Must be literally true. Safety latch, deletion is irreversible.",
            },
        },
        ["required"] = new JsonArray("clip", "confirm"),
    };

    // ── Handlers ─────────────────────────────────────────────────────────────────────────

    private static JsonObject SetFields(JsonObject? args, LibrarySandbox sandbox, SelfActionLedger? ledger = null)
    {
        if (args?["fields"] is not JsonObject fieldArgs || fieldArgs.Count == 0)
            throw new ToolException(
                "The 'fields' argument is required: an object mapping field names to string " +
                "values, e.g. { \"game\": \"Team Fortress 2\", \"tags\": \"win|comeback\" }.");

        var mutation = new MetadataMutation();
        var set = new JsonArray();
        var deleted = new JsonArray();
        foreach (var pair in fieldArgs)
        {
            if (pair.Value is not JsonValue value || !value.TryGetValue(out string? text))
                throw new ToolException(
                    $"Field '{pair.Key}' must have a string value (use \"\" to delete it).");

            mutation.SetFields[ClipMetaSchema.AtomName(pair.Key)] = text;
            // Empty string is the schema's delete idiom, report it as what it IS so the
            // model tells the user "deleted", not "set to nothing".
            if (string.IsNullOrEmpty(text)) deleted.Add(pair.Key);
            else set.Add(pair.Key);
        }

        // Pre-compute the advisory BEFORE the write so we check against the library's existing
        // vocab, not the just-written clip. sandbox.Root bypasses RequireRoot() (which throws a
        // different message than ResolveWritePath), preserving the existing no-root error text.
        string? playersValue = fieldArgs[ClipMetaSchema.Players] is JsonValue pv &&
            pv.TryGetValue(out string? pvs) ? pvs : null;
        JsonArray? roster = args?["roster"] as JsonArray;
        JsonArray? playerReview = (playersValue is not null && sandbox.Root is not null)
            ? ReadTools.UnknownPlayerReview(playersValue, sandbox.Root, roster)
            : null;

        return ExecuteWrite(args, sandbox, mutation, result =>
        {
            if (set.Count > 0) result["setFields"] = set;
            if (deleted.Count > 0) result["deletedFields"] = deleted;
            if (playerReview is not null)
                result["review"] = playerReview;
        }, ledger);
    }

    private static JsonObject AppendField(JsonObject? args, LibrarySandbox sandbox, SelfActionLedger? ledger = null)
    {
        string field = ReadTools.GetRequiredString(args, "field");
        string value = ReadTools.GetRequiredString(args, "value");

        var mutation = new MetadataMutation();
        mutation.AppendFields[ClipMetaSchema.AtomName(field)] = value;

        // Advisory for players append: pre-compute before the write (same timing rationale as SetFields).
        string? playersValue = field.Equals(ClipMetaSchema.Players, StringComparison.OrdinalIgnoreCase) ? value : null;
        JsonArray? roster = args?["roster"] as JsonArray;
        JsonArray? playerReview = (playersValue is not null && sandbox.Root is not null)
            ? ReadTools.UnknownPlayerReview(playersValue, sandbox.Root, roster)
            : null;

        return ExecuteWrite(args, sandbox, mutation, result =>
        {
            result["appendedField"] = field;
            result["appendedValue"] = value;
            if (playerReview is not null)
                result["review"] = playerReview;
        }, ledger);
    }

    private static JsonObject ClearFields(JsonObject? args, LibrarySandbox sandbox, SelfActionLedger? ledger = null)
    {
        if (args?["fields"] is not JsonArray fieldArgs || fieldArgs.Count == 0)
            throw new ToolException(
                "The 'fields' argument is required: an array of field names to delete, " +
                "e.g. [\"tags\", \"notes\"].");

        var mutation = new MetadataMutation();
        var cleared = new JsonArray();
        foreach (JsonNode? node in fieldArgs)
        {
            if (node is not JsonValue value || !value.TryGetValue(out string? name) ||
                string.IsNullOrWhiteSpace(name))
            {
                throw new ToolException("Every entry in 'fields' must be a non-empty field name.");
            }
            mutation.DeleteFields.Add(ClipMetaSchema.AtomName(name));
            cleared.Add(name);
        }

        return ExecuteWrite(args, sandbox, mutation,
            result => result["clearedFields"] = cleared, ledger);
    }

    private static JsonObject ClearAll(JsonObject? args, LibrarySandbox sandbox, SelfActionLedger? ledger = null)
    {
        // The latch is the literal boolean true, a string "true" or a missing key refuses.
        // The model must consciously supply it, which in practice means it asked the user.
        if (args?["confirm"] is not JsonValue confirmValue ||
            !confirmValue.TryGetValue(out bool confirmed) || !confirmed)
        {
            throw new ToolException(
                "clip_clear_all removes ALL metadata from the clip and requires confirm:true. " +
                "Confirm with the user, then call again with confirm:true.");
        }

        var mutation = new MetadataMutation { ClearAll = true };
        return ExecuteWrite(args, sandbox, mutation,
            result => result["clearedAll"] = true, ledger);
    }

    // ── Backup management handlers ─────────────────────────────────────────────────────────

    private static JsonObject ListBackups(JsonObject? args, LibrarySandbox sandbox)
    {
        string root = sandbox.RequireRoot();

        // Optional clip filter: contain it in the library, but don't require it to exist, a
        // user may want to see the backups of a clip they've since deleted.
        string? clipFilter = null;
        if (ReadTools.GetOptionalString(args, "clip") is string clipArg)
            clipFilter = sandbox.ResolveContainedPath(clipArg, mustExist: false);

        var backups = new JsonArray();
        foreach (BackupInfo b in ClipBackup.ListBackups(root, clipFilter))
        {
            backups.Add(new JsonObject
            {
                ["backup"] = b.BackupPath,
                ["clip"] = b.ClipPath,
                ["sizeBytes"] = b.SizeBytes,
                ["takenUtc"] = b.TakenUtc.ToString("O"),
            });
        }

        return new JsonObject
        {
            ["directory"] = root,
            ["clip"] = clipFilter,
            ["backupCount"] = backups.Count,
            ["backups"] = backups,
        };
    }

    private static JsonObject RestoreBackup(JsonObject? args, LibrarySandbox sandbox)
    {
        string backupPath = sandbox.ResolveContainedPath(
            ReadTools.GetRequiredString(args, "backup"), mustExist: true);

        if (args?["confirm"] is not JsonValue confirmValue ||
            !confirmValue.TryGetValue(out bool confirmed) || !confirmed)
        {
            throw new ToolException(
                "clip_restore_backup overwrites the clip with the backup and requires confirm:true. " +
                "Confirm with the user, then call again with confirm:true.");
        }

        if (!ClipBackup.TryGetClipForBackup(backupPath, out string? clipPath))
            throw new ToolException(
                $"'{backupPath}' is not a clipmeta backup file (expected a name ending " +
                ".mp4.bak-<timestamp>). Use library_list_backups to find valid backups.");

        // The derived clip is a sibling of the (contained) backup, so it is contained too;
        // single-flight with the write tools, restoring is a write.
        WriteGate.Enter();
        try
        {
            ClipBackup.Restore(backupPath, clipPath, NullLogger.Instance);
        }
        catch (InvalidDataException ex)
        {
            throw new ToolException(ex.Message); // corrupt backup refused; clip untouched
        }
        catch (IOException ex)
        {
            throw new ToolException($"Could not restore '{clipPath}': {ex.Message}");
        }
        finally
        {
            WriteGate.Exit();
        }

        // Read the restored clip back so the model reports its actual post-restore state.
        JsonObject result = ReadTools.GetMetadata(new JsonObject { ["path"] = clipPath }, sandbox);
        result["restoredFrom"] = backupPath;
        return result;
    }

    private static JsonObject PruneBackups(JsonObject? args, LibrarySandbox sandbox)
    {
        string root = sandbox.RequireRoot();
        // Clip need not exist, prune the backups of a deleted clip too.
        string clipPath = sandbox.ResolveContainedPath(
            ReadTools.GetRequiredString(args, "clip"), mustExist: false);
        int keep = Math.Max(0, ReadTools.GetOptionalInt(args, "keep", 0));

        if (args?["confirm"] is not JsonValue confirmValue ||
            !confirmValue.TryGetValue(out bool confirmed) || !confirmed)
        {
            throw new ToolException(
                "clip_prune_backups permanently deletes backup files and requires confirm:true. " +
                "Run library_list_backups first to see what would be removed, then call again " +
                "with confirm:true.");
        }

        // ListBackups returns newest-first and only files matching the backup convention for
        // this clip, so skipping the first `keep` and deleting the rest can never touch the
        // clip itself or any unrelated file.
        IReadOnlyList<BackupInfo> all = ClipBackup.ListBackups(root, clipPath);
        var deleted = new JsonArray();
        var kept = new JsonArray();
        WriteGate.Enter();
        try
        {
            for (int i = 0; i < all.Count; i++)
            {
                if (i < keep) { kept.Add(all[i].BackupPath); continue; }
                try
                {
                    File.Delete(all[i].BackupPath);
                    deleted.Add(all[i].BackupPath);
                }
                catch (IOException ex)
                {
                    throw new ToolException(
                        $"Deleted {deleted.Count} backup(s), then failed on " +
                        $"'{all[i].BackupPath}': {ex.Message}");
                }
            }
        }
        finally
        {
            WriteGate.Exit();
        }

        return new JsonObject
        {
            ["clip"] = clipPath,
            ["deletedCount"] = deleted.Count,
            ["keptCount"] = kept.Count,
            ["deleted"] = deleted,
            ["kept"] = kept,
        };
    }

    // ── Shared write pipeline ────────────────────────────────────────────────────────────

    /// <summary>
    /// The one path every write takes: resolve under the write sandbox, apply backup/dry-run
    /// policy, run the mutation through <see cref="Mp4Writer"/> single-flight, translate the
    /// write engine's exceptions into model-readable refusals, and read the file back so the
    /// response shows the actual post-write state (one extra parse buys the model ground truth
    /// instead of an assumption, and for dry runs, proves nothing changed). On a successful
    /// (non-dry-run) write, marks the path in <paramref name="ledger"/> so gaming-mode watching
    /// can exclude ClipMeta-tagged clips from recent-save detection. A refused or failed write
    /// must never mark.
    /// </summary>
    private static JsonObject ExecuteWrite(
        JsonObject? args,
        LibrarySandbox sandbox,
        MetadataMutation mutation,
        Action<JsonObject> describeChange,
        SelfActionLedger? ledger = null)
    {
        string fullPath = sandbox.ResolveWritePath(ReadTools.GetRequiredString(args, "path"));

        // Dry-run: report the PREDICTED post-write fields without touching the file. Computed via
        // Core's MetadataPreview (which reuses the writer's Normalizer), so the preview matches an
        // actual write's read-back. (The prior dry-run read the UNCHANGED file back and so showed
        // current, not predicted, state.)
        if (ReadTools.GetOptionalBool(args, "dry_run", defaultValue: false))
            return PreviewWrite(fullPath, mutation, describeChange);

        bool backup = ReadTools.GetOptionalBool(args, "backup", defaultValue: true);
        mutation.DryRun = false;
        mutation.StampProvenance = ReadTools.GetOptionalBool(args, "stamp_provenance", defaultValue: true);
        // Timestamped sibling (clip.mp4.bak-20260612-153000): never silently overwrites a
        // previous backup the user might still want. The naming lives in Core (ClipBackup) so
        // the backup-management tools recognize exactly what the writer produces.
        mutation.BackupPath = backup ? ClipBackup.MakeBackupPath(fullPath) : null;

        WriteGate.Enter();
        try
        {
            new Mp4Writer().WriteMetadata(fullPath, mutation, NullLogger.Instance);
        }
        // Bad user values (rating out of range, malformed timecode, Core's Normalizer)...
        catch (ArgumentException ex)
        {
            throw new ToolException($"Invalid value: {ex.Message}");
        }
        // ...operations the engine refuses by design (append to a non-text atom, etc.)...
        catch (InvalidOperationException ex)
        {
            throw new ToolException($"That operation isn't possible on this clip: {ex.Message}");
        }
        // ...formats the engine won't touch (fragmented/moof MP4s)...
        catch (UnsupportedFormatException ex)
        {
            throw new ToolException($"This clip's format can't be written safely: {ex.Message}");
        }
        // ...and the safety net: post-write verification failed, original left untouched.
        catch (InvalidDataException ex)
        {
            throw new ToolException(
                $"Write verification failed and the original file was left unchanged: {ex.Message}");
        }
        catch (IOException ex)
        {
            throw new ToolException($"Could not write '{fullPath}': {ex.Message}");
        }
        finally
        {
            WriteGate.Exit();
        }

        // Mark the path written so library_watching's gaming-mode signal can exclude it.
        // Placed here, only reached after a successful, non-dry-run write; all catch paths
        // above throw ToolException and therefore skip this line.
        ledger?.MarkWritten(fullPath);

        // Ground truth read-back (see doc comment). GetMetadata re-resolves the path through
        // the read sandbox, harmless, it just passed the stricter write check.
        JsonObject result = ReadTools.GetMetadata(
            new JsonObject { ["path"] = fullPath }, sandbox);
        result["dryRun"] = false;
        result["backupPath"] = mutation.BackupPath is not null && File.Exists(mutation.BackupPath)
            ? mutation.BackupPath
            : null;
        describeChange(result);
        return result;
    }

    /// <summary>
    /// Builds the dry-run response: the PREDICTED post-write user fields, computed without touching
    /// the file via Core's <see cref="MetadataPreview"/> (which reuses the writer's
    /// <see cref="Normalizer"/>), so the preview matches an actual write's read-back. Same result
    /// shape as a real write, with <c>dryRun:true</c> and no backup.
    /// </summary>
    private static JsonObject PreviewWrite(
        string fullPath, MetadataMutation mutation, Action<JsonObject> describeChange)
    {
        BoxNode root = ReadTools.ParseClip(fullPath);
        IReadOnlyList<(string Field, string Value)> predicted =
            MetadataPreview.Predict(ClipMetaReader.GetUserFields(root), mutation);
        ClipMetaFieldStats stats = ClipMetaStats.Categorize(predicted);

        var fields = new JsonObject();
        foreach (var (field, value) in predicted)
            fields[field] = value;

        var knownUnset = new JsonArray();
        foreach (string f in stats.KnownUnset) knownUnset.Add(f);
        var customFields = new JsonArray();
        foreach (string f in stats.CustomFields) customFields.Add(f);

        var result = new JsonObject
        {
            ["path"] = fullPath,
            ["sizeBytes"] = new FileInfo(fullPath).Length,
            ["fields"] = fields,
            ["knownUnset"] = knownUnset,
            ["customFields"] = customFields,
            ["dryRun"] = true,
            ["backupPath"] = null,
        };
        describeChange(result);
        return result;
    }
}
