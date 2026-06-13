using System.Text.Json.Nodes;
using ClipMetaCore;
using ClipMetaCore.Logging;
using ClipMetaCore.Schema;
using ClipMetaCore.Write;

namespace ClipMetaMcp.Tools;

/// <summary>
/// Registers the write tools (phase 3). Every mutation goes through Core's
/// <see cref="Mp4Writer"/> — the engine with the temp-file/verify/<c>File.Replace</c> golden
/// rule, parser-coverage gate, and media-integrity guards — so these tools contain parameter
/// mapping and safety policy, never write logic.
///
/// Safety policy (spec §3):
/// - <c>backup</c> defaults ON — a timestamped sibling copy survives every write unless the
///   caller explicitly opts out. An LLM is the caller; the user's clips get belt and braces.
/// - <c>dry_run</c> reports what would change without touching the file.
/// - <c>clip_clear_all</c> additionally requires the literal argument <c>confirm: true</c>.
/// - Writes hard-require the configured library (<see cref="LibrarySandbox.ResolveWritePath"/>).
/// - Single-flight: one write at a time process-wide (risk R8) — two concurrent rewrites of
///   the same file would race at <c>File.Replace</c>.
/// </summary>
public static class WriteTools
{
    /// <summary>
    /// Serializes all write-tool executions. The session loop is single-threaded today, so this
    /// is insurance for tomorrow (a host that pipelines requests, a future parallel dispatcher)
    /// rather than a fix for an observed race — but R8 is cheap to retire permanently now.
    /// </summary>
    private static readonly SemaphoreSlim WriteGate = new(1, 1);

    /// <summary>Registers all write tools against the given sandbox.</summary>
    public static void RegisterAll(ToolRegistry registry, LibrarySandbox sandbox)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(sandbox);

        registry.Register(new ToolDefinition(
            "clip_set_fields",
            "Writes metadata into one MP4 game clip (stored inside the file, so tags travel " +
            "with it). 'fields' maps field names to string values — well-known fields: " +
            KnownFieldsSentence + "; any other name becomes a custom field. Setting a field " +
            "to an empty string DELETES it. Multi-value fields use pipe-delimited strings " +
            "(e.g. tags: \"win|comeback\"). 'rating' must be 1-5. 'timecode' is normalized to " +
            "HH:MM:SS: a bare number is seconds and a two-part value is MM:SS, so \"90\" and " +
            "\"1:30\" both become 00:01:30. A timestamped backup copy is kept next to the file " +
            "unless backup:false; dry_run:true previews without writing.",
            SetFieldsSchema(),
            args => SetFields(args, sandbox),
            clipPath => new JsonObject
            {
                ["path"] = clipPath,
                ["fields"] = new JsonObject { ["game"] = "TF2" },
                ["backup"] = false,
            }));

        registry.Register(new ToolDefinition(
            "clip_append_field",
            "Appends a value to one metadata field of an MP4 clip without disturbing what is " +
            "already there — for multi-value fields (players, tags, timecode) the new items " +
            "merge into the pipe-delimited list (duplicates removed); for text fields the " +
            "value is appended. Use clip_set_fields to replace a value outright. Timestamped " +
            "backup unless backup:false; dry_run:true previews.",
            AppendFieldSchema(),
            args => AppendField(args, sandbox),
            clipPath => new JsonObject
            {
                ["path"] = clipPath,
                ["field"] = "tags",
                ["value"] = "purity-probe",
                ["backup"] = false,
            }));

        registry.Register(new ToolDefinition(
            "clip_clear_fields",
            "Deletes the named metadata fields from one MP4 clip (the listed fields only — " +
            "other metadata is untouched). Clearing a field that is not set is not an error. " +
            "Timestamped backup unless backup:false; dry_run:true previews.",
            ClearFieldsSchema(),
            args => ClearFields(args, sandbox),
            clipPath => new JsonObject
            {
                ["path"] = clipPath,
                ["fields"] = new JsonArray("game"),
                ["backup"] = false,
            }));

        registry.Register(new ToolDefinition(
            "clip_clear_all",
            "Removes ALL clipmeta metadata from one MP4 clip. Destructive: requires the " +
            "explicit argument confirm:true — ask the user before calling this unless they " +
            "already clearly asked for a full wipe. Timestamped backup unless backup:false; " +
            "dry_run:true previews.",
            ClearAllSchema(),
            args => ClearAll(args, sandbox),
            clipPath => new JsonObject
            {
                ["path"] = clipPath,
                ["confirm"] = true,
                ["backup"] = false,
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

    // ── Handlers ─────────────────────────────────────────────────────────────────────────

    private static JsonObject SetFields(JsonObject? args, LibrarySandbox sandbox)
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
            // Empty string is the schema's delete idiom — report it as what it IS so the
            // model tells the user "deleted", not "set to nothing".
            if (string.IsNullOrEmpty(text)) deleted.Add(pair.Key);
            else set.Add(pair.Key);
        }

        return ExecuteWrite(args, sandbox, mutation, result =>
        {
            if (set.Count > 0) result["setFields"] = set;
            if (deleted.Count > 0) result["deletedFields"] = deleted;
        });
    }

    private static JsonObject AppendField(JsonObject? args, LibrarySandbox sandbox)
    {
        string field = ReadTools.GetRequiredString(args, "field");
        string value = ReadTools.GetRequiredString(args, "value");

        var mutation = new MetadataMutation();
        mutation.AppendFields[ClipMetaSchema.AtomName(field)] = value;

        return ExecuteWrite(args, sandbox, mutation, result =>
        {
            result["appendedField"] = field;
            result["appendedValue"] = value;
        });
    }

    private static JsonObject ClearFields(JsonObject? args, LibrarySandbox sandbox)
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
            result => result["clearedFields"] = cleared);
    }

    private static JsonObject ClearAll(JsonObject? args, LibrarySandbox sandbox)
    {
        // The latch is the literal boolean true — a string "true" or a missing key refuses.
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
            result => result["clearedAll"] = true);
    }

    // ── Shared write pipeline ────────────────────────────────────────────────────────────

    /// <summary>
    /// The one path every write takes: resolve under the write sandbox, apply backup/dry-run
    /// policy, run the mutation through <see cref="Mp4Writer"/> single-flight, translate the
    /// write engine's exceptions into model-readable refusals, and read the file back so the
    /// response shows the actual post-write state (one extra parse buys the model ground truth
    /// instead of an assumption — and for dry runs, proves nothing changed).
    /// </summary>
    private static JsonObject ExecuteWrite(
        JsonObject? args,
        LibrarySandbox sandbox,
        MetadataMutation mutation,
        Action<JsonObject> describeChange)
    {
        string fullPath = sandbox.ResolveWritePath(ReadTools.GetRequiredString(args, "path"));

        bool backup = ReadTools.GetOptionalBool(args, "backup", defaultValue: true);
        bool dryRun = ReadTools.GetOptionalBool(args, "dry_run", defaultValue: false);
        mutation.DryRun = dryRun;
        // Timestamped sibling (clip.mp4.bak-20260612-153000): never silently overwrites a
        // previous backup from an earlier session the user might still want.
        mutation.BackupPath = backup && !dryRun
            ? $"{fullPath}.bak-{DateTime.Now:yyyyMMdd-HHmmss}"
            : null;

        WriteGate.Wait();
        try
        {
            new Mp4Writer().WriteMetadata(fullPath, mutation, NullLogger.Instance);
        }
        // Bad user values (rating out of range, malformed timecode — Core's Normalizer)...
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
            WriteGate.Release();
        }

        // Ground truth read-back (see doc comment). GetMetadata re-resolves the path through
        // the read sandbox — harmless, it just passed the stricter write check.
        JsonObject result = ReadTools.GetMetadata(
            new JsonObject { ["path"] = fullPath }, sandbox);
        result["dryRun"] = dryRun;
        result["backupPath"] = mutation.BackupPath is not null && File.Exists(mutation.BackupPath)
            ? mutation.BackupPath
            : null;
        describeChange(result);
        return result;
    }
}
