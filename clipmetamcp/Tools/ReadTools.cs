using System.Globalization;
using System.Text.Json.Nodes;
using ClipMetaCore.Logging;
using ClipMetaCore.Mp4;
using ClipMetaCore.Read;
using ClipMetaCore.Schema;
using ClipMetaCore.Watching;
using ClipMetaCore.Write;

namespace ClipMetaMcp.Tools;

/// <summary>
/// Registers the read-only tools. Every handler delegates to an already-tested Core operation, 
/// the thin-shell rule applies to this MCP server exactly as it does to the CLIs.
/// Two kinds of tool live here:
/// single-clip reads (<c>clip_get_metadata</c>, <c>clip_get_stats</c>) that take a path, and
/// library-wide reads (<c>library_*</c>) that operate on the configured clips folder and
/// refuse when none is configured (see <see cref="LibrarySandbox.RequireRoot"/>).
/// </summary>
public static class ReadTools
{
    /// <summary>
    /// Library listings could return thousands of entries on a big clips folder; the result
    /// travels through the model's context, so cap it and tell the model it was truncated
    /// (it can narrow with 'pattern' or 'subfolder'). Default when the caller doesn't ask.
    /// </summary>
    private const int DefaultListLimit = 200;

    /// <summary>Hard ceiling for the caller-supplied list limit.</summary>
    private const int MaxListLimit = 1000;

    /// <summary>Default number of watched-clip candidates returned by library_watching.</summary>
    private const int DefaultWatchingLimit = 5;

    /// <summary>Hard ceiling for the caller-supplied watched-clip limit.</summary>
    private const int MaxWatchingLimit = 50;

    /// <summary>
    /// Registers all read tools against the given sandbox. When <paramref name="watcher"/> is supplied,
    /// library_watching resolves the watched clip from the watcher's title-segment history (the
    /// previous-stable heuristic) instead of a one-shot poll; null keeps today's live-poll behavior.
    /// When <paramref name="ledger"/> is supplied, <c>clip_get_metadata</c> and <c>library_export</c>
    /// mark each content-read path in the ledger (so access-time signals can subtract self-reads), and
    /// <c>library_watching</c> threads it into <see cref="WatchingResolver.CreateDefault"/> so
    /// gaming-mode detection excludes clips ClipMeta itself tagged. When <paramref name="journal"/> is
    /// supplied, <c>library_watching</c> surfaces any tags the background <see cref="QueueDrainPump"/>
    /// auto-flushed since the last call as <c>autoFlushed</c> (report-once: <see cref="DrainJournal.TakePending"/>
    /// clears the buffer).
    /// </summary>
    public static void RegisterAll(
        ToolRegistry registry, LibrarySandbox sandbox,
        ReviewWatcher? watcher = null, SelfActionLedger? ledger = null,
        DrainJournal? journal = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(sandbox);

        registry.Register(new ToolDefinition(
            "clip_get_metadata",
            "Reads everything about one MP4 game clip in a single call: all clipmeta metadata " +
            "values, file size, which well-known fields are unset, and which set fields are " +
            "custom names. 'path' must be an existing .mp4 file inside the configured clips " +
            "library; relative paths resolve against the library root. " + PipeFieldsSentence +
            " For MANY clips, do NOT call this per file, library_export returns every clip's " +
            "metadata in one call, and library_search_index answers field queries in one call.",
            SinglePathSchema(),
            args => GetMetadata(args, sandbox, ledger),
            clipPath => new JsonObject { ["path"] = clipPath }));

        registry.Register(new ToolDefinition(
            "library_list",
            "Lists MP4 clip files in the clips library by file name (no metadata is read, " +
            "to see every clip's metadata in one call use library_export; to query by field " +
            "use library_find or library_search_index). Newest first. Optional 'pattern' " +
            "is a wildcard on the file name (e.g. '*2026.01*'), optional 'subfolder' restricts " +
            "to one folder inside the library, 'recursive' defaults to true, 'limit' caps the " +
            $"result (default {DefaultListLimit}). Requires a configured clips library.",
            ListSchema(),
            args => ListLibrary(args, sandbox),
            _ => new JsonObject()));

        registry.Register(new ToolDefinition(
            "library_find",
            "Searches every clip in the library for a metadata field whose value contains the " +
            "given text (case-insensitive substring) and returns the matching file paths. " +
            "This parses each MP4, so it can take a while on large libraries, prefer " +
            "library_search_index for repeated queries. Requires a configured clips library. " +
            PipeFieldsSentence,
            FieldValueSchema(
                "Metadata field to search (e.g. game, tags, players, or a custom name).",
                "Text the field value must contain (case-insensitive)."),
            args => FindInLibrary(args, sandbox),
            _ => new JsonObject { ["field"] = "game", ["value"] = "TF2" }));

        registry.Register(new ToolDefinition(
            "library_vocab",
            "Lists every distinct value used for one metadata field across the whole library, " +
            "with the number of clips using each value, e.g. all tags ever used, or all game " +
            "names. Multi-value fields are split into individual items first. Requires a " +
            "configured clips library.",
            FieldOnlySchema(),
            args => VocabForLibrary(args, sandbox),
            _ => new JsonObject { ["field"] = "tags" }));

        registry.Register(new ToolDefinition(
            "library_export",
            "Exports the metadata of every clip in the library (or one subfolder) as " +
            "structured records ('json', the default) or CSV text ('csv', same columns as " +
            "the clipmetascribe --export command; custom fields become extra columns after the " +
            "known ones). Ordered alphabetically by path (note: library_list orders newest " +
            "first). Requires a configured clips library.",
            ExportSchema(),
            args => ExportLibrary(args, sandbox, ledger),
            _ => new JsonObject()));

        registry.Register(new ToolDefinition(
            "library_search_index",
            "Fast metadata search backed by an index file stored in the library root. " +
            "Results reflect the index as of 'indexBuilt'; the response's 'staleClipCount' " +
            "says how many files changed since, pass rebuild:true when it is above zero. " +
            "With 'field' (and optional 'value' substring) returns matching clips; without, " +
            "returns an index summary. Requires a configured clips library.",
            SearchIndexSchema(),
            args => SearchIndex(args, sandbox),
            _ => new JsonObject { ["rebuild"] = true, ["field"] = "game", ["value"] = "TF2" }));

        registry.Register(new ToolDefinition(
            "library_watching",
            "Resolves 'the clip I'm watching / just watched' by inspecting open media players. " +
            "Returns ranked candidates, best first. A 'player_title' candidate resolved to a library " +
            "path with confidence 'high' is the file an open player is showing, prefer it and you " +
            "may tag it. If only 'access_time' candidates exist, or confidence is 'low' (multiple " +
            "players open, or an ambiguous file name), confirm with the user before tagging. " +
            "A 'recent_write' candidate with confidence 'high' is a clip just SAVED to the library while no " +
            "player was open (gaming mode, the user clipped a moment from a game); it is a live target you " +
            "may tag. 'recent_write' 'low' means several clips were saved at once, so confirm which one. " +
            "IMPORTANT: when 'anyLiveTarget' is false, NOTHING is actually open or locked, every " +
            "candidate is just an unverified most-recently-touched guess (and 'access_time' is only " +
            "an advisory recency hint, easily skewed by other apps), so do NOT tag without the user " +
            "confirming the exact path. To tag, " +
            "call the write tool with the chosen 'path'. Note: a clip cannot be written while a " +
            "player still holds it open ('inUse' true), it frees when the player advances or closes. " +
            "Optional 'limit' (default " + DefaultWatchingLimit + ") and 'include_access_fallback' " +
            "(default true). " +
            "If the response includes a 'warning' (type 'player_outside_library'), a player is showing a file " +
            "that is not in the configured library, tell the user they may be playing from the wrong folder " +
            "(name the player and, if 'foreignDirectory' is given, the folder) and do NOT tag. If a candidate " +
            "has a 'note', mention it and confirm with the user before tagging. " +
            "Requires a configured clips library. " +
            "In review mode the recommended top candidate reflects the clip you were watching when you " +
            "spoke, even if the player has since advanced (it may be unlocked and directly writable). " +
            "For an EXACT bind, and to clear a backlog of several dictations, pass 'spoken_at' (the time " +
            "the user dictated); see that argument. A " +
            "'review' array may list non-blocking advisories (autoCorrected, sameClipTwice, sequenceSkip, " +
            "multiplePlayersActive, timestampUnmatched) to mention to the user and reconcile later, never block the run to ask. " +
            "Calling this also writes any previously queued tags whose clips have since been freed (see library_queue_tag).",
            WatchingSchema(),
            args => Watching(args, sandbox, watcher, ledger, journal),
            _ => new JsonObject { ["limit"] = DefaultWatchingLimit }));
    }

    /// <summary>
    /// The multi-value-field sentence shared by tool descriptions, derived from
    /// <see cref="ClipMetaSchema.PipeFields"/> so it can never drift from the schema.
    /// (Enumerated via KnownFields to keep the listing order deterministic.)
    /// </summary>
    private static string PipeFieldsSentence =>
        "Multi-value fields (" +
        string.Join(", ", ClipMetaSchema.KnownFields.Where(ClipMetaSchema.PipeFields.Contains)) +
        ") are returned as pipe-delimited strings.";

    // ── JSON Schemas ─────────────────────────────────────────────────────────────────────

    /// <summary>JSON Schema for tools whose only argument is a clip path.</summary>
    private static JsonObject SinglePathSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Path to an .mp4 file inside the clips library. " +
                                  "Absolute, or relative to the library root.",
            },
        },
        ["required"] = new JsonArray("path"),
    };

    private static JsonObject ListSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["subfolder"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Optional folder inside the library to list instead of the whole library.",
            },
            ["pattern"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Optional wildcard filter on the file name, e.g. '*2026.01*' or 'tf2*'. " +
                                  "'*' matches any text, '?' any single character.",
            },
            ["recursive"] = new JsonObject
            {
                ["type"] = "boolean",
                ["description"] = "Include subfolders (default true).",
            },
            ["limit"] = new JsonObject
            {
                ["type"] = "integer",
                ["description"] = $"Maximum number of clips to return (default {DefaultListLimit}, max {MaxListLimit}).",
            },
        },
    };

    private static JsonObject FieldValueSchema(string fieldDescription, string valueDescription) => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["field"] = new JsonObject { ["type"] = "string", ["description"] = fieldDescription },
            ["value"] = new JsonObject { ["type"] = "string", ["description"] = valueDescription },
        },
        ["required"] = new JsonArray("field", "value"),
    };

    private static JsonObject FieldOnlySchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["field"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Metadata field to enumerate values for (e.g. tags, game, players).",
            },
        },
        ["required"] = new JsonArray("field"),
    };

    private static JsonObject ExportSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["format"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray("json", "csv"),
                ["description"] = "Output format (default json).",
            },
            ["subfolder"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Optional folder inside the library to export instead of the whole library.",
            },
        },
    };

    private static JsonObject SearchIndexSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["rebuild"] = new JsonObject
            {
                ["type"] = "boolean",
                ["description"] = "Rescan the library and rewrite the index before answering (default false).",
            },
            ["field"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Metadata field to search. Omit to just get an index summary.",
            },
            ["value"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Substring the field value must contain (case-insensitive). " +
                                  "Omit or leave empty to match every clip that has the field.",
            },
        },
    };

    private static JsonObject WatchingSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["limit"] = new JsonObject
            {
                ["type"] = "integer",
                ["description"] = $"Maximum candidates to return (default {DefaultWatchingLimit}, max {MaxWatchingLimit}).",
            },
            ["include_access_fallback"] = new JsonObject
            {
                ["type"] = "boolean",
                ["description"] = "When true (default), include most-recently-accessed clips as " +
                                  "low-confidence candidates. When false, only open-player candidates " +
                                  "are returned.",
            },
            ["spoken_at"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "ISO-8601/RFC-3339 timestamp of WHEN THE USER ACTUALLY DICTATED this " +
                                  "tag (e.g. '2026-06-26T18:25:03Z'). Pass it whenever you know it, the " +
                                  "clip whose playback covered that instant is bound exactly, instead of " +
                                  "guessing from when this call happens to run. Essential for clearing a " +
                                  "backlog: issue one call per pending dictation, OLDEST FIRST, each with " +
                                  "its own spoken_at, and each resolves its own clip. A malformed or " +
                                  "absent value simply falls back to the live heuristic.",
            },
        },
    };

    // ── Handlers ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One call, the whole picture. Field-report driven (2026-06-12): the first consumer agent
    /// needed values AND set/unset/custom categorization for one clip and had to make two calls
    /// (clip_get_metadata + the since-removed clip_get_stats), each a full MP4 parse. The file
    /// is already parsed here, return everything. When <paramref name="ledger"/> is non-null,
    /// marks the path as read so access-time signals can subtract self-reads; internal utility
    /// calls (e.g. ground-truth read-back in ExecuteWrite) pass null and are not marked.
    /// </summary>
    internal static JsonObject GetMetadata(JsonObject? args, LibrarySandbox sandbox, SelfActionLedger? ledger = null)
    {
        string fullPath = sandbox.ResolveClipPath(GetRequiredString(args, "path"));
        BoxNode root = ParseClip(fullPath);
        ledger?.MarkRead(fullPath);

        // GetUserFields already excludes internal bookkeeping fields. It can legitimately return
        // the same field name more than once (a file holding duplicate clipmeta atoms, e.g.
        // written by another tagger): keep the FIRST occurrence and report the conflict, rather
        // than silently last-wins, the model must not present one value as authoritative when
        // the file disagrees with itself.
        IReadOnlyList<(string Field, string Value)> userFields = ClipMetaReader.GetUserFields(root);
        var fields = new JsonObject();
        var duplicated = new JsonArray();
        foreach ((string field, string value) in userFields)
        {
            if (fields.ContainsKey(field))
            {
                if (!duplicated.Any(d => d!.GetValue<string>() == field))
                    duplicated.Add(field);
                continue;
            }
            fields[field] = value;
        }

        // Same categorization Core gives the CLI --stats command, one definition of
        // set/unset/custom for every surface.
        ClipMetaFieldStats stats = ClipMetaStats.Categorize(userFields);

        var result = new JsonObject
        {
            ["path"] = fullPath,
            ["sizeBytes"] = new FileInfo(fullPath).Length,
            ["fields"] = fields,
            ["knownUnset"] = ToJsonArray(stats.KnownUnset),
            ["customFields"] = ToJsonArray(stats.CustomFields),
        };
        if (duplicated.Count > 0)
            result["duplicatedFields"] = duplicated; // names that appeared more than once; first value kept
        return result;
    }

    private static JsonObject ListLibrary(JsonObject? args, LibrarySandbox sandbox)
    {
        string directory = sandbox.ResolveLibraryDirectory(GetOptionalString(args, "subfolder"));
        string? pattern = GetOptionalString(args, "pattern");
        bool recursive = GetOptionalBool(args, "recursive", defaultValue: true);
        int limit = Math.Clamp(GetOptionalInt(args, "limit", DefaultListLimit), 1, MaxListLimit);

        IReadOnlyList<ClipFileInfo> all = ClipMetaLibrary.ListClips(directory, pattern, recursive);

        var clips = new JsonArray();
        foreach (ClipFileInfo clip in all.Take(limit))
        {
            clips.Add(new JsonObject
            {
                ["path"] = clip.FilePath,
                ["name"] = Path.GetFileName(clip.FilePath),
                ["sizeBytes"] = clip.SizeBytes,
                ["lastModified"] = clip.LastModified.ToString("O"),
            });
        }

        return new JsonObject
        {
            ["directory"] = directory,
            ["totalMatches"] = all.Count,
            ["returned"] = clips.Count,
            // Spelled out so the model knows more exist and narrows instead of assuming
            // this is everything.
            ["truncated"] = all.Count > clips.Count,
            ["clips"] = clips,
        };
    }

    private static JsonObject FindInLibrary(JsonObject? args, LibrarySandbox sandbox)
    {
        string root = sandbox.RequireRoot();
        string field = GetRequiredString(args, "field");
        string value = GetRequiredString(args, "value");

        var paths = new JsonArray();
        foreach (string path in ClipMetaFinder.Find(root, field, value))
            paths.Add(path);

        return new JsonObject
        {
            ["field"] = field,
            ["value"] = value,
            ["matchCount"] = paths.Count,
            ["paths"] = paths,
        };
    }

    private static JsonObject VocabForLibrary(JsonObject? args, LibrarySandbox sandbox)
    {
        string root = sandbox.RequireRoot();
        string field = GetRequiredString(args, "field");

        VocabResult vocab = ClipMetaVocab.Enumerate(root, field);

        // Most-used first, then alphabetical, the order a human (or model) summarizing
        // "what tags do I use?" actually wants. Dictionary order would be arbitrary.
        var values = new JsonObject();
        foreach (var pair in vocab.Counts
                     .OrderByDescending(p => p.Value)
                     .ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            values[pair.Key] = pair.Value;
        }

        return new JsonObject
        {
            ["field"] = field,
            ["clipsWithField"] = vocab.ClipsWithField,
            ["distinctValues"] = values.Count,
            ["values"] = values,
        };
    }

    /// <summary>
    /// Exports every clip's metadata. When <paramref name="ledger"/> is non-null, marks each
    /// exported clip path as read so access-time signals can subtract these content reads.
    /// </summary>
    private static JsonObject ExportLibrary(JsonObject? args, LibrarySandbox sandbox, SelfActionLedger? ledger = null)
    {
        string directory = sandbox.ResolveLibraryDirectory(GetOptionalString(args, "subfolder"));
        string format = GetOptionalString(args, "format")?.ToLowerInvariant() ?? "json";
        if (format is not ("json" or "csv"))
            throw new ToolException($"Unknown format '{format}'. Use 'json' or 'csv'.");

        IEnumerable<string> paths = Directory.EnumerateFiles(directory, "*.mp4", SearchOption.AllDirectories);
        IReadOnlyList<ExportRecord> records = ClipMetaExporter.GetRecords(paths);
        foreach (ExportRecord record in records)
            ledger?.MarkRead(record.FilePath);

        if (format == "csv")
        {
            // Core's writer, byte-identical to clipmetascribe --export --format csv.
            using var csv = new StringWriter();
            ClipMetaExporter.WriteCsv(records, csv);
            return new JsonObject
            {
                ["format"] = "csv",
                ["clipCount"] = records.Count,
                ["csv"] = csv.ToString(),
            };
        }

        var jsonRecords = new JsonArray();
        foreach (ExportRecord record in records)
        {
            var fields = new JsonObject();
            foreach ((string f, string v) in record.Fields)
                fields.TryAdd(f, v); // duplicate atoms: first wins, as in clip_get_metadata
            jsonRecords.Add(new JsonObject { ["file"] = record.FilePath, ["fields"] = fields });
        }
        return new JsonObject
        {
            ["format"] = "json",
            ["clipCount"] = records.Count,
            ["records"] = jsonRecords,
        };
    }

    private static JsonObject SearchIndex(JsonObject? args, LibrarySandbox sandbox)
    {
        string root = sandbox.RequireRoot();
        string indexPath = Path.Combine(root, ClipMetaIndex.IndexFileName);
        bool rebuildRequested = GetOptionalBool(args, "rebuild", defaultValue: false);

        IndexData data;
        bool rebuilt;
        if (!rebuildRequested && File.Exists(indexPath))
        {
            try
            {
                data = ClipMetaIndex.ReadFromFile(indexPath);
                rebuilt = false;
            }
            catch (Exception ex) when (ex is IOException or FormatException or ArgumentException)
            {
                // A corrupt or unreadable index self-heals with a rescan instead of wedging
                // the tool, the index is a cache, never the source of truth.
                data = RebuildIndex(root, indexPath);
                rebuilt = true;
            }
        }
        else
        {
            data = RebuildIndex(root, indexPath);
            rebuilt = true;
        }

        var result = new JsonObject
        {
            ["indexBuilt"] = data.Built.ToString("O"),
            ["rebuilt"] = rebuilt,
            ["clipCount"] = data.Entries.Count,
            // Field-report driven (2026-06-12): the agent had no way to know whether the index
            // still matched the filesystem and had to guess about rebuild:true. This costs one
            // stat call per file, no parsing, and makes the decision mechanical.
            ["staleClipCount"] = CountStaleClips(root, data),
        };

        string? field = GetOptionalString(args, "field");
        if (field is null)
            return result; // summary only, the model asked about the index, not a query

        // Empty/absent value means "every clip that has this field" (ClipMetaSearch semantics).
        string value = GetOptionalString(args, "value") ?? string.Empty;
        var matches = new JsonArray();
        foreach (IndexEntry entry in ClipMetaSearch.Find(data, field, value))
        {
            var fields = new JsonObject();
            foreach ((string f, string v) in entry.Fields)
                fields.TryAdd(f, v);
            matches.Add(new JsonObject { ["path"] = entry.FilePath, ["fields"] = fields });
        }
        result["field"] = field;
        result["value"] = value;
        result["matchCount"] = matches.Count;
        result["matches"] = matches;
        return result;
    }

    /// <summary>
    /// Resolves the watched clip and returns ranked candidates. When <paramref name="ledger"/> is
    /// non-null it is threaded into <see cref="WatchingResolver.CreateDefault"/> so
    /// gaming-mode (<c>recent_write</c>) detection excludes paths ClipMeta itself tagged. When
    /// <paramref name="journal"/> is non-null, tags the background pump auto-flushed since the
    /// last call are surfaced as <c>autoFlushed</c> (report-once via <see cref="DrainJournal.TakePending"/>).
    /// </summary>
    private static JsonObject Watching(
        JsonObject? args, LibrarySandbox sandbox,
        ReviewWatcher? watcher = null, SelfActionLedger? ledger = null,
        DrainJournal? journal = null)
    {
        string root = sandbox.RequireRoot();

        // Opportunistic drain (pass 2): land any queued tags whose locks have cleared before
        // resolving. The queue is opportunistic state, never a hard dependency, a persistence
        // failure here (e.g. the queue file is momentarily locked) must NOT fail a watched-clip
        // READ, so degrade to "nothing drained" and let resolution proceed; the next call retries.
        DrainReport drained;
        WriteGate.Enter();
        try
        {
            drained = TagQueue.Drain(root, new Mp4Writer(), NullLogger.Instance, LockProbe.IsInUse);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            drained = new DrainReport(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
        }
        finally
        {
            WriteGate.Exit();
        }

        int limit = Math.Clamp(GetOptionalInt(args, "limit", DefaultWatchingLimit), 1, MaxWatchingLimit);
        bool includeAccessFallback = GetOptionalBool(args, "include_access_fallback", defaultValue: true);
        DateTimeOffset? spokenAt = ParseSpokenAt(args);

        var resolver = WatchingResolver.CreateDefault(ProcessWindowSource.ForCurrentPlatform(), ledger);
        WatchingResult result = watcher is null
            ? resolver.Resolve(root, limit, includeAccessFallback)
            : resolver.ResolveReview(root, watcher.Snapshot(), watcher.LastBoundId,
                                     DateTimeOffset.UtcNow, limit, includeAccessFallback, spokenAt);

        // Remember the recommended bind so the next call can flag a repeat or a skipped clip.
        if (watcher is not null && result.RecommendationConfident && result.BoundSegmentId is { } boundId)
            watcher.MarkBound(boundId);

        var array = new JsonArray();
        foreach (WatchingCandidate c in result.Candidates)
        {
            var entry = new JsonObject
            {
                ["path"] = c.Path,
                ["name"] = c.Name,
                ["source"] = c.Source,
                ["player"] = c.Player,
                ["lastAccessTimeUtc"] = c.LastAccessTimeUtc.ToString("O"),
                ["secondsSinceAccess"] = Math.Round(c.SecondsSinceAccess, 1),
                ["inUse"] = c.InUse,
                ["confidence"] = c.Confidence,
            };
            if (c.Note is not null)
                entry["note"] = c.Note;
            array.Add(entry);
        }

        var response = new JsonObject
        {
            ["libraryRoot"] = root,
            ["candidateCount"] = result.Candidates.Count,
            ["anyLiveTarget"] = result.AnyLiveTarget,
            ["candidates"] = array,
        };

        // Review-mode advisories (non-blocking): the model mentions these to the user and reconciles
        // later. A multi-player flag also raises the existing inline warning channel.
        if (result.Review is { Count: > 0 })
        {
            var review = new JsonArray();
            foreach (ReviewFlag f in result.Review)
            {
                var clips = new JsonArray();
                foreach (string clipName in f.Clips) clips.Add(clipName);
                var entry = new JsonObject { ["type"] = f.Type, ["clips"] = clips };
                if (f.StableSeconds > 0) entry["stableSeconds"] = Math.Round(f.StableSeconds, 1);
                review.Add(entry);
            }
            response["review"] = review;

            if (result.Review.Any(f => f.Type == ReviewFlag.TypeMultiplePlayersActive))
                response["warning"] = new JsonObject
                {
                    ["type"] = "multiple_players_active",
                    ["message"] = "More than one media player is active, too ambiguous to bind a clip " +
                                  "safely. Confirm the exact path with the user before tagging.",
                };
        }

        if (response["warning"] is null && result.Diagnostics.UnresolvedPlayers.Count > 0)
        {
            var players = new JsonArray();
            foreach (UnresolvedPlayer up in result.Diagnostics.UnresolvedPlayers)
                players.Add(new JsonObject
                {
                    ["player"] = up.Player,
                    ["referencedName"] = up.ReferencedName,
                    ["foreignDirectory"] = up.ForeignDirectory,
                });

            if (ForeignNoticeIsBlocking(result.Candidates))
                response["warning"] = new JsonObject
                {
                    ["type"] = "player_outside_library",
                    ["message"] = "A media player is showing a file that is not in the configured clips " +
                                  "library. The user may be playing from the wrong folder. Do not tag.",
                    ["unresolvedPlayers"] = players,
                };
            else
                // #1: a fresh in-library save was detected, the gaming candidate is the live target,
                // so the foreign player is informational only (never "do not tag").
                response["advisory"] = new JsonObject
                {
                    ["type"] = "player_outside_library_ignored",
                    ["message"] = "A media player is showing a file outside the library, but a fresh " +
                                  "in-library save was detected, the gaming candidate below is the live " +
                                  "target. The foreign player was ignored.",
                    ["unresolvedPlayers"] = players,
                };
        }

        // Always echo the drain outcome + remaining queue depth so a caller can confirm a queued
        // write actually landed from the watching response alone (the dogfood's "silent flush" gap):
        // previously these were emitted only when non-zero, so "did my last tag land?" was unanswerable.
        response["drainedFromQueue"] = new JsonObject
        {
            ["written"] = drained.Written.Count,
            ["dropped"] = drained.Dropped.Count,
            ["stillQueued"] = drained.StillQueued.Count,
        };
        response["queuePending"] = TagQueue.Status(root, LockProbe.IsInUse).Count;

        // P0-1: surface tags the BACKGROUND pump auto-flushed since the last call (it writes the
        // last clip when its player closes but reports to no one). Report-once: TakePending clears.
        // Shape is built by QueueTools.AutoFlushedJson, single source so queue tools and watching
        // emit identical path/fields/agoSeconds entries (agoSeconds is clamped ≥ 0 there).
        response["autoFlushed"] = QueueTools.AutoFlushedJson(journal);

        return response;
    }

    /// <summary>Scans the library and persists the fresh index into the library root.</summary>
    private static IndexData RebuildIndex(string root, string indexPath)
    {
        IndexData data = ClipMetaIndex.Build(root);
        ClipMetaIndex.WriteToFile(data, indexPath);
        return data;
    }

    /// <summary>
    /// How many clips on disk disagree with the index: new files, deleted files, and files
    /// whose size or last-write time changed since the index was built. Stat calls only.
    /// </summary>
    private static int CountStaleClips(string root, IndexData data)
    {
        var indexed = data.Entries.ToDictionary(
            e => e.FilePath, e => e, StringComparer.OrdinalIgnoreCase);

        int stale = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in Directory.EnumerateFiles(root, "*.mp4", SearchOption.AllDirectories))
        {
            seen.Add(path);
            if (!indexed.TryGetValue(path, out IndexEntry? entry))
            {
                stale++; // new file the index has never seen
                continue;
            }
            var info = new FileInfo(path);
            if (info.Length != entry.FileSizeBytes ||
                new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero) != entry.LastModified)
            {
                stale++; // modified (retagged clips change both, but either alone counts)
            }
        }

        // Indexed entries whose files are gone are stale too, searches would return ghosts.
        stale += indexed.Keys.Count(path => !seen.Contains(path));
        return stale;
    }

    // ── Shared plumbing ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Whether an open foreign player should be reported as a BLOCKING "do not tag" warning. False
    /// when the candidate list already contains a gaming target (a <c>recent_write</c> candidate): a
    /// foreign lock is on a file you cannot tag anyway, so it must not block a valid in-library save, 
    /// it demotes to a non-blocking advisory instead (#1).
    /// </summary>
    internal static bool ForeignNoticeIsBlocking(IReadOnlyList<WatchingCandidate> candidates) =>
        !candidates.Any(c => c.Source == RecentWriteSignal.SourceName);

    /// <summary>Parses an MP4, converting Core's exceptions into model-readable refusals.</summary>
    internal static BoxNode ParseClip(string fullPath)
    {
        try
        {
            return Mp4Parser.ParseFile(fullPath);
        }
        catch (InvalidDataException ex)
        {
            throw new ToolException($"'{fullPath}' could not be parsed as an MP4 file: {ex.Message}");
        }
        catch (UnauthorizedAccessException)
        {
            throw new ToolException($"Access to '{fullPath}' was denied by the operating system.");
        }
        catch (IOException ex)
        {
            throw new ToolException($"Could not read '{fullPath}': {ex.Message}");
        }
    }

    private static JsonArray ToJsonArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (string value in values)
            array.Add(value);
        return array;
    }

    /// <summary>Extracts a required string argument or refuses with a message naming it.</summary>
    internal static string GetRequiredString(JsonObject? args, string name)
    {
        if (args?[name] is JsonValue value &&
            value.TryGetValue(out string? text) &&
            !string.IsNullOrWhiteSpace(text))
        {
            return text;
        }
        throw new ToolException($"The '{name}' argument is required and must be a non-empty string.");
    }

    /// <summary>
    /// Extracts an optional string argument. Absent, JSON null, or blank all mean "not given"
    /// (null); a present non-string value is a refusal, not a silent coercion.
    /// </summary>
    /// <summary>
    /// Parses the optional 'spoken_at' timestamp leniently: a missing, wrong-typed, or unparseable
    /// value yields null so a watched-clip READ never fails on this convenience argument, it simply
    /// falls back to the live heuristic.
    /// </summary>
    private static DateTimeOffset? ParseSpokenAt(JsonObject? args)
    {
        if (args?["spoken_at"] is not JsonValue value || !value.TryGetValue(out string? text) ||
            string.IsNullOrWhiteSpace(text))
            return null;
        return DateTimeOffset.TryParse(
            text, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal, out DateTimeOffset dto)
            ? dto
            : null;
    }

    internal static string? GetOptionalString(JsonObject? args, string name)
    {
        JsonNode? node = args?[name];
        if (node is null)
            return null;
        if (node is JsonValue value && value.TryGetValue(out string? text))
            return string.IsNullOrWhiteSpace(text) ? null : text;
        throw new ToolException($"The '{name}' argument must be a string when given.");
    }

    /// <summary>Extracts an optional boolean argument; a present non-boolean is a refusal.</summary>
    internal static bool GetOptionalBool(JsonObject? args, string name, bool defaultValue)
    {
        JsonNode? node = args?[name];
        if (node is null)
            return defaultValue;
        if (node is JsonValue value && value.TryGetValue(out bool flag))
            return flag;
        throw new ToolException($"The '{name}' argument must be true or false when given.");
    }

    /// <summary>Extracts an optional integer argument; a present non-integer is a refusal.</summary>
    internal static int GetOptionalInt(JsonObject? args, string name, int defaultValue)
    {
        JsonNode? node = args?[name];
        if (node is null)
            return defaultValue;
        if (node is JsonValue value && value.TryGetValue(out int number))
            return number;
        throw new ToolException($"The '{name}' argument must be an integer when given.");
    }

    /// <summary>
    /// Builds the soft "unknownPlayer" review array for a players value, or null when every token is
    /// known. Known = library vocab players ∪ the optional session roster arg. Never blocks the write.
    /// </summary>
    internal static JsonArray? UnknownPlayerReview(string? playersValue, string root, JsonArray? roster)
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (string name in ClipMetaVocab.Enumerate(root, ClipMetaSchema.Players).Counts.Keys)
                known.Add(name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null; // enumeration failure degrades to no advisory so the write proceeds unblocked
        }
        if (roster is not null)
            foreach (JsonNode? n in roster)
                if (n?.GetValue<string>() is { Length: > 0 } s)
                    known.Add(s.Trim());

        IReadOnlyList<string> unknown = PlayerRosterGuard.UnknownPlayers(playersValue, known);
        if (unknown.Count == 0)
            return null;

        var knownArr = new JsonArray();
        foreach (string k in known.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            knownArr.Add(k);

        var review = new JsonArray();
        foreach (string token in unknown)
            review.Add(new JsonObject
            {
                ["type"] = "unknownPlayer",
                ["token"] = token,
                ["knownPlayers"] = knownArr.DeepClone(),
            });
        return review;
    }
}
