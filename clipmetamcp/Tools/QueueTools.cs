// clipmetamcp/Tools/QueueTools.cs
using System.Text.Json.Nodes;
using ClipMetaCore.Logging;
using ClipMetaCore.Schema;
using ClipMetaCore.Watching;
using ClipMetaCore.Write;

namespace ClipMetaMcp.Tools;

/// <summary>
/// Registers the deferred-tag queue tools. A clip that is playing is locked against our write, so
/// these persist a CONFIRMED tag and drain the queue as locks clear. The queue never resolves or
/// guesses — the caller passes an already-resolved path (from library_watching, confirmed with the
/// user when confidence was low). Every drain runs under the shared <see cref="WriteGate"/> so it
/// can never race a direct write at <c>File.Replace</c>.
/// </summary>
public static class QueueTools
{
    /// <summary>
    /// Registers the queue tools against the given sandbox. When <paramref name="pump"/> is supplied,
    /// each enqueue wakes it so the background drain lands the tag the moment the player's lock clears
    /// (zero-touch flush for the last clip); null disables that — the queue still drains
    /// opportunistically on the next watched-clip call and via library_flush_queue. When
    /// <paramref name="journal"/> is supplied, <c>library_flush_queue</c> and <c>library_queue_status</c>
    /// surface any tags the background pump auto-flushed since the last call as <c>autoFlushed</c>
    /// (report-once: <see cref="DrainJournal.TakePending"/> clears the buffer).
    /// </summary>
    public static void RegisterAll(
        ToolRegistry registry, LibrarySandbox sandbox,
        QueueDrainPump? pump = null, DrainJournal? journal = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(sandbox);

        registry.Register(new ToolDefinition(
            "library_queue_tag",
            "Queues a metadata tag for a clip that is currently being played (and therefore locked " +
            "against writing). Pass the clip 'path' you already resolved with library_watching and " +
            "confirmed — this tool does NOT resolve or guess. 'fields' maps field names to string " +
            "values (empty string deletes), exactly like clip_set_fields. For searchability, put " +
            "people in 'players' and searchable nouns/moments (objects, places, events) in 'tags' " +
            "rather than burying them in free-text 'notes' — those three fields ACCUMULATE across " +
            "re-tags of the same clip (notes join as prose; tags/players merge), while game/rating " +
            "replace. The tag is written " +
            "automatically the next time you call a watched-clip tool after the player advances " +
            "(the lock clears), or immediately via library_flush_queue. Requires a configured library.",
            QueueTagSchema(),
            args => QueueTag(args, sandbox, pump),
            clipPath => new JsonObject
            {
                ["path"] = clipPath,
                ["fields"] = new JsonObject { ["tags"] = "headshot" },
            }));

        registry.Register(new ToolDefinition(
            "library_flush_queue",
            "Writes every queued deferred tag whose clip is no longer locked — use after you stop " +
            "and close the player on the LAST clip, when there is no next watched-clip call to drain " +
            "the queue. Returns what was written, what is still locked (will retry), and what was " +
            "dropped because the clip is gone. Requires a configured library.",
            NoArgsSchema(),
            args => FlushQueue(args, sandbox, journal),
            _ => new JsonObject()));

        registry.Register(new ToolDefinition(
            "library_queue_status",
            "Lists the deferred tags waiting to be written: the clip, which fields will change, how " +
            "long it has waited, and whether it is still locked. Read-only. Requires a configured library.",
            NoArgsSchema(),
            args => QueueStatus(args, sandbox, journal),
            _ => new JsonObject()));
    }

    private static JsonObject QueueTagSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Path to the .mp4 you already resolved and confirmed. " +
                                  "Absolute, or relative to the library root.",
            },
            ["fields"] = new JsonObject
            {
                ["type"] = "object",
                ["description"] = "Field name → string value. Empty string deletes the field.",
                ["additionalProperties"] = new JsonObject { ["type"] = "string" },
            },
        },
        ["required"] = new JsonArray("path", "fields"),
    };

    private static JsonObject NoArgsSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject(),
    };

    // ── Handlers ─────────────────────────────────────────────────────────────────────────

    private static JsonObject QueueTag(JsonObject? args, LibrarySandbox sandbox, QueueDrainPump? pump = null)
    {
        // Library-sandbox check IS the "dumb queue" guard: the path must be a real .mp4 in-library.
        string fullPath = sandbox.ResolveWritePath(ReadTools.GetRequiredString(args, "path"));

        if (args?["fields"] is not JsonObject fieldArgs || fieldArgs.Count == 0)
            throw new ToolException(
                "The 'fields' argument is required: an object mapping field names to string values, " +
                "e.g. { \"tags\": \"headshot\" }.");

        var mutation = new MetadataMutation();
        foreach (var pair in fieldArgs)
        {
            if (pair.Value is not JsonValue value || !value.TryGetValue(out string? text))
                throw new ToolException($"Field '{pair.Key}' must have a string value (use \"\" to delete it).");

            string atom = ClipMetaSchema.AtomName(pair.Key);
            // Per-field semantics so re-tagging a clip ACCUMULATES instead of overwriting: notes
            // (prose), tags and players (lists) append; game/rating/timecode/custom replace. An
            // empty value is always the delete idiom (a set that Normalizer turns into a delete).
            if (text.Length > 0 && ClipMetaSchema.QueueAppendFields.Contains(pair.Key))
                mutation.AppendFields[atom] = text;
            else
                mutation.SetFields[atom] = text;
        }

        string root = sandbox.RequireRoot();
        DrainReport drain = DrainUnderGate(root);   // opportunistic: land anything already freed
        TagQueue.Enqueue(root, fullPath, mutation, confidence: "high");

        // Wake the background pump so it lands THIS tag the instant the player's lock clears — the
        // zero-touch flush for the last clip, where no further watched-clip call will drain it.
        pump?.Wake();

        return new JsonObject
        {
            ["queued"] = fullPath,
            ["pending"] = TagQueue.Status(root, LockProbe.IsInUse).Count,
            ["drained"] = DrainJson(drain),
        };
    }

    private static JsonObject FlushQueue(JsonObject? args, LibrarySandbox sandbox, DrainJournal? journal = null)
    {
        string root = sandbox.RequireRoot();
        DrainReport drain = DrainUnderGate(root);
        var result = DrainJson(drain);
        result["autoFlushed"] = AutoFlushedJson(journal);
        return result;
    }

    private static JsonObject QueueStatus(JsonObject? args, LibrarySandbox sandbox, DrainJournal? journal = null)
    {
        string root = sandbox.RequireRoot();
        var entries = new JsonArray();
        foreach (QueueStatusEntry e in TagQueue.Status(root, LockProbe.IsInUse))
        {
            var fields = new JsonArray();
            foreach (string f in e.ChangedFields) fields.Add(f);
            entries.Add(new JsonObject
            {
                ["path"] = e.ClipPath,
                ["changedFields"] = fields,
                ["ageSeconds"] = Math.Round(e.AgeSeconds, 1),
                ["locked"] = e.Locked,
            });
        }
        return new JsonObject
        {
            ["pending"] = entries.Count,
            ["entries"] = entries,
            ["autoFlushed"] = AutoFlushedJson(journal),
        };
    }

    /// <summary>
    /// Builds the <c>autoFlushed</c> array from the journal: tags the background pump wrote
    /// since the last foreground call. Report-once — <see cref="DrainJournal.TakePending"/> clears.
    /// Only the pump feeds the journal; synchronous drains pass <see langword="null"/> and are
    /// already reflected in the caller's own response, so they are never double-reported here.
    /// </summary>
    private static JsonArray AutoFlushedJson(DrainJournal? journal)
    {
        var arr = new JsonArray();
        foreach (DrainedTag t in journal?.TakePending() ?? Array.Empty<DrainedTag>())
        {
            var fields = new JsonArray();
            foreach (string f in t.Fields) fields.Add(f);
            arr.Add(new JsonObject
            {
                ["path"] = t.Path,
                ["fields"] = fields,
                ["agoSeconds"] = Math.Round((DateTimeOffset.UtcNow - t.WhenUtc).TotalSeconds, 1),
            });
        }
        return arr;
    }

    /// <summary>Drains the queue under the shared write single-flight, with the real probe/engine.</summary>
    private static DrainReport DrainUnderGate(string root)
    {
        WriteGate.Enter();
        try
        {
            return TagQueue.Drain(root, new Mp4Writer(), NullLogger.Instance, LockProbe.IsInUse);
        }
        finally
        {
            WriteGate.Exit();
        }
    }

    private static JsonObject DrainJson(DrainReport drain) => new()
    {
        ["written"] = ToArray(drain.Written),
        ["stillQueued"] = ToArray(drain.StillQueued),
        ["dropped"] = ToArray(drain.Dropped),
    };

    private static JsonArray ToArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (string v in values) array.Add(v);
        return array;
    }
}
