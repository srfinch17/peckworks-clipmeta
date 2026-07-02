using System.Text;
using ClipMetaCore.Abstractions;
using ClipMetaCore.Mp4;
using ClipMetaCore.Schema;

namespace ClipMetaCore.Write;

/// <summary>
/// Writes clipmeta metadata mutations into MP4 files using a safe temp-file strategy.
/// The source file is NEVER opened for writing. If any step fails, the original is untouched.
/// </summary>
/// <remarks>
/// <para><b>How a write works (the full pipeline):</b></para>
/// <list type="number">
///   <item>Parse the source file into a <see cref="BoxNode"/> tree (read-only).</item>
///   <item>Refuse to proceed if the file is fragmented (moof boxes) or if the parser could not
///       account for every byte of the file, see <see cref="VerifyParseAccountsForWholeFile"/>.
///       Writing from an incomplete parse tree would silently drop the unparsed bytes,
///       which can include the entire mdat (the actual video/audio data).</item>
///   <item>Predict the new moov size and derive <c>delta</c> = how many bytes everything
///       after moov will shift. This is the single most dangerous number in the codebase:
///       it is used to patch every chunk offset (stco/co64), so if the prediction is wrong
///       the file plays garbage. A hard assert in <see cref="WriteMoov"/> guarantees the
///       prediction matches the bytes actually produced, or the write aborts.</item>
///   <item>Stream the file to a sibling temp file (<c>file.mp4.tmp</c>), copying every box
///       verbatim except the moov subtree, which is rebuilt with the mutated metadata and
///       offset-corrected chunk tables. mdat is never loaded into memory, only stream-copied.</item>
///   <item>Verify the temp file: its length must equal original + delta, it must re-parse
///       cleanly end-to-end, it must contain the same number of mdat boxes as the original,
///       and every field we set must read back. See <see cref="VerifyWrite"/>.</item>
///   <item>Atomically swap the temp file into place with <see cref="File.Replace(string, string, string?)"/>.
///       Until this single OS call, the original file has not been touched in any way.</item>
/// </list>
/// <para>On ANY failure at ANY step, the temp file is deleted and the original is intact.</para>
/// </remarks>
public sealed class Mp4Writer : IMediaWriter
{
    /// <summary>How many times the final atomic swap is attempted before giving up.</summary>
    private const int MaxReplaceAttempts = 5;

    /// <summary>Base backoff between swap attempts; the wait scales by attempt number.</summary>
    private const int ReplaceBackoffMs = 100;

    /// <summary>
    /// Runs <paramref name="action"/>, retrying on a transient file-lock failure
    /// (<see cref="IOException"/> / <see cref="UnauthorizedAccessException"/>, what a sharing
    /// violation surfaces as) up to <paramref name="maxAttempts"/> times, sleeping
    /// <paramref name="baseDelayMs"/> × attempt between tries. The final failure is rethrown.
    /// Used only for the post-verification atomic swap, where a retry is safe (see call site).
    /// <paramref name="onRetry"/> is invoked before each wait (for logging). Internal so it can
    /// be unit-tested directly with a controllable delegate and zero delay.
    /// </summary>
    internal static void RetryOnTransientLock(
        Action action, int maxAttempts, int baseDelayMs, Action<int, Exception>? onRetry = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < maxAttempts)
            {
                onRetry?.Invoke(attempt, ex);
                if (baseDelayMs > 0)
                    System.Threading.Thread.Sleep(baseDelayMs * attempt);
            }
        }
    }

    /// <summary>
    /// Opens the source for reading (deny-writers), riding out a transient sharing violation with the
    /// same bounded backoff the final swap uses. A media player that just "closed" can leave a handle
    /// lingering for a moment (or the Search indexer / AV grabs a just-finished file); without this a
    /// write of such a clip failed outright where a brief retry succeeds. A clip that is genuinely
    /// still held fails after the retries with the same friendly message as before.
    /// </summary>
    private static FileStream OpenSourceWithRetry(string filePath, IClipMetaLogger logger)
    {
        FileStream? src = null;
        try
        {
            RetryOnTransientLock(
                () => src = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read),
                MaxReplaceAttempts, ReplaceBackoffMs,
                (attempt, ex) => logger.LogVerbose(
                    $"source open attempt {attempt} hit a transient lock ({ex.GetType().Name}: {ex.Message}); retrying"));
        }
        catch (IOException ex)
        {
            throw new IOException(
                $"'{Path.GetFileName(filePath)}' cannot be opened for tagging. Another " +
                $"program has it open for writing, if it is still being recorded or " +
                $"exported, wait for that to finish and try again.", ex);
        }
        return src!;
    }

    /// <inheritdoc/>
    public bool CanWrite(string filePath) =>
        Path.GetExtension(filePath).Equals(".mp4", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public void WriteMetadata(string filePath, MetadataMutation mutation, IClipMetaLogger logger)
    {
        if (mutation.DryRun)
        {
            logger.Log($"DRY RUN, no files will be modified: {filePath}");
            return;
        }

        Normalizer.ApplyToMutation(mutation);

        // Stamp the schema version atom, but only when this write actually stores field values.
        // Delete-only mutations and --clear-all must NOT re-add atoms: "remove all clipmeta
        // metadata" has to mean exactly that. (Normalizer runs first, so a --set with an empty
        // value has already been converted into a delete and won't trigger the stamp.)
        if (mutation.SetFields.Count > 0 || mutation.AppendFields.Count > 0)
        {
            mutation.SetFields.TryAdd(ClipMetaSchema.AtomName(ClipMetaSchema.Schema), ClipMetaSchema.SchemaVersion);

            // Provenance: stamp "who tagged this" under the same gate as the schema version (only
            // when real user data is written). TryAdd so a caller-supplied tagged_by value wins.
            if (mutation.StampProvenance)
                mutation.SetFields.TryAdd(
                    ClipMetaSchema.AtomName(ClipMetaSchema.TaggedBy), ClipMetaSchema.ProvenanceValue);
        }

        // The temp file gets a unique name (clip.mp4.<guid>.tmp) so it can never collide with, 
        // and FileMode.Create-overwrite, a file the user actually owns, or with a second write
        // running against the same clip at the same time.
        string tempPath = $"{filePath}.{Guid.NewGuid():N}.tmp";

        logger.Log($"WRITE {Path.GetFileName(filePath)} begin");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            // Open the source ONCE, for the entire parse-and-copy, with FileShare.Read:
            // other processes may read alongside us but nobody may WRITE while we work.
            // This closes a real race: chunk offsets are captured during the parse and the
            // bytes are copied afterwards, if a capture tool still recording the file could
            // append between those two steps, the output would index bytes that moved.
            // Holding one deny-writers handle makes parse + copy see a single frozen snapshot;
            // if a recorder already has the file open for writing, this open fails up front
            // (sharing violation) and we refuse cleanly instead of producing a torn file.
            FileStream src = OpenSourceWithRetry(filePath, logger);

            using (src)
            {
                var root = Mp4Parser.Parse(src);
                DetectFragmented(root, filePath);

                // SAFETY GATE: the parser is deliberately lenient (the tree viewer should open
                // damaged files), but the WRITER must be strict. It rebuilds the output from the
                // parse tree, so any byte the parser skipped would simply vanish from the new
                // file. A corrupt 8-byte box sandwiched between moov and mdat would otherwise
                // cause the entire mdat, all the video, to be dropped silently. Refuse instead.
                VerifyParseAccountsForWholeFile(root, filePath);
                logger.LogVerbose($"PARSE {CountBoxes(root)} boxes");

                // SAFETY GATE: ISO 14496-12 legally permits a meta box directly under moov (or a
                // udta under a trak), but this writer only edits the canonical
                // moov.udta.meta.ilst. The reader walks EVERY ilst anywhere, so a set/clear/
                // clear-all against the canonical copy while a non-canonical duplicate survives
                // would silently diverge. Refuse rather than risk that.
                DetectNonCanonicalMetadata(root, filePath);

                // Fold appends into sets: read the current value, merge, and treat the result
                // as a plain set from here on.
                foreach (var (key, appendValue) in mutation.AppendFields.ToList())
                {
                    var existingNode = FindEditableNode(root, key);
                    string current;
                    if (existingNode?.DisplayValue is not { } dv)
                    {
                        // Atom absent (or has no readable value): appending to nothing is a set.
                        current = string.Empty;
                    }
                    else if (dv.Length >= 2 && dv[0] == '"' && dv[^1] == '"')
                    {
                        // Text values are presented quoted ("like this") by the parser; strip
                        // the quotes to recover the raw stored string.
                        current = dv[1..^1];
                    }
                    else
                    {
                        // The existing payload is not text (e.g. an image or integer-typed data
                        // atom, displayed as "[JPEG image, …]" or a bare number). Splicing its
                        // DISPLAY string into a pipe list would write that placeholder text into
                        // the file as if it were the value. Refuse instead of corrupting it.
                        throw new InvalidOperationException(
                            $"Cannot append to '{key}': its existing value is not text " +
                            $"({dv}). Use --set to replace it instead.");
                    }
                    // Field-aware merge: prose (notes) joins as text, case preserved; list fields
                    // (tags/players/timecode) pipe-merge + dedup. AppendValue handles empty current.
                    mutation.SetFields[key] = Normalizer.AppendValue(key, current, appendValue);
                }
                mutation.AppendFields.Clear();

                // A schema stamp with nothing to version is pure file bloat: if this mutation
                // removes the last user field, the stamp goes with it (field-discovered
                // 2026-06-12: delete-only clears left files +~80 bytes vs pristine forever).
                RemoveOrphanedSchemaStamp(root, mutation);

                var (scenario, ilstChildren, newFields) = DetermineScenario(root, mutation);
                logger.LogVerbose($"WRITE scenario={scenario}");

                // When the mutation empties the ilst, the now-useless container chain
                // (ilst → meta+hdlr → udta) is dropped too, so a write→clear round-trip
                // returns the file to byte-identical pristine instead of leaving husks.
                var drop = DetermineEmptyChainRemoval(root, mutation, newFields);
                if (drop.Ilst)
                    logger.LogVerbose($"WRITE dropping empty container chain " +
                        $"(ilst{(drop.Meta ? "+meta" : "")}{(drop.Udta ? "+udta" : "")})");

                // Predict, byte-exactly, how large the rebuilt moov will be. The difference vs
                // the original moov ("delta") is how far every byte after moov will shift in the
                // output. Chunk-offset tables (stco/co64) inside moov hold ABSOLUTE file offsets
                // into mdat, so each of their entries must be corrected by exactly this delta, 
                // see WriteAdjustedStco/WriteAdjustedCo64. WriteMoov later asserts that the moov
                // it actually produced matches this prediction; a mismatch aborts the write
                // rather than risk patching the offsets by the wrong amount.
                long originalMoovSize = GetMoovSize(root);
                long newMoovSize = CalculateNewMoovSize(root, scenario, ilstChildren, newFields, mutation, drop);
                long delta = newMoovSize - originalMoovSize;
                logger.LogVerbose($"WRITE delta={delta:+#;-#;0} bytes");

                // Where the original moov ends. Chunk offsets BELOW this point reference data
                // that sits before/inside moov (the mdat-before-moov layout, common output of
                // many screen recorders), that data does not move, so those entries are left
                // alone. Layout is detected here from the actual box offsets, never assumed from
                // the file's source; moov-first and mdat-first files are both handled on merit.
                long moovEndOffset = GetMoovEndOffset(root);

                WriteToTemp(src, tempPath, root, mutation, scenario, ilstChildren, newFields,
                            delta, moovEndOffset, newMoovSize, drop, logger);

                // VERIFICATION STEP 1, cheap whole-file arithmetic. moov is the only box whose
                // size changed, so the temp file must be exactly (original length + delta) bytes.
                // This single check catches every "a box was silently dropped" failure mode.
                long expectedTempLength = (long)root.Size + delta;
                long actualTempLength = new FileInfo(tempPath).Length;
                if (actualTempLength != expectedTempLength)
                    throw new InvalidDataException(
                        $"Verification failed for '{Path.GetFileName(filePath)}': temp file is " +
                        $"{actualTempLength} bytes but {expectedTempLength} were expected " +
                        $"(original {root.Size} + delta {delta}). The original file is untouched.");

                // VERIFICATION STEP 2, full re-parse of the temp file. It must parse cleanly
                // from first byte to last, contain the same media boxes as the original, and
                // every field this mutation set must read back. Only after this passes do we
                // touch the original.
                var verifyRoot = Mp4Parser.ParseFile(tempPath);
                VerifyParseAccountsForWholeFile(verifyRoot, tempPath);
                VerifyWrite(verifyRoot, root, mutation, filePath);
                logger.LogVerbose($"VERIFY temp file re-parsed OK {CountBoxes(verifyRoot)} boxes intact");
            }
            // The deny-writers handle must be released BEFORE File.Replace: ReplaceFile needs
            // write/delete access to the destination, which our own open would block. This
            // re-opens a microscopic window where another process could grab the file between
            // the close and the swap, but by now the temp file is fully written and verified,
            // so the worst case is the swap failing with an IOException (original untouched),
            // never a torn output.
            //
            // Retry the swap briefly on a transient sharing violation. By this point the temp is
            // fully written and verified, so retrying the atomic swap weakens no guarantee, it
            // only rides out a momentary lock, which on Windows is common: antivirus or the
            // Search indexer grabs a just-written file (the temp, or the destination right after
            // a recorder finished it) for a second or two. Without this, tagging a freshly
            // created clip intermittently failed with "being used by another process" even though
            // nothing was wrong. If every attempt still fails, the last exception propagates and
            // the original file is untouched, fail safe, exactly as before.
            RetryOnTransientLock(
                () => File.Replace(tempPath, filePath, destinationBackupFileName: mutation.BackupPath),
                MaxReplaceAttempts, ReplaceBackoffMs,
                (attempt, ex) => logger.LogVerbose(
                    $"File.Replace attempt {attempt} hit a transient lock ({ex.GetType().Name}: {ex.Message}); retrying"));
            logger.LogVerbose($"SWAP {Path.GetFileName(filePath)} ← {Path.GetFileName(tempPath)}");

            sw.Stop();
            logger.Log($"WRITE {Path.GetFileName(filePath)} OK {sw.ElapsedMilliseconds}ms");
        }
        catch
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { /* best effort */ }
            }
            throw;
        }
    }

    // ── Orphaned schema stamp + empty-chain removal ────────────────────────────

    /// <summary>Which now-empty containers this write should drop (innermost first).</summary>
    /// <param name="Ilst">The post-mutation ilst would hold zero atoms, omit it.</param>
    /// <param name="Meta">With ilst gone, meta would hold only its hdlr, omit it too.</param>
    /// <param name="Udta">With meta gone, udta would be empty, omit the whole chain.</param>
    private readonly record struct EmptyChainRemoval(bool Ilst, bool Meta, bool Udta);

    /// <summary>
    /// If this mutation removes the LAST user clipmeta field, schedules the schema-version
    /// stamp for deletion as well. The stamp exists to version real metadata; once no user
    /// field remains it is pure residue, without this, every write→clear cycle left ~80
    /// bytes behind and "has clipmeta ever touched this file" stopped being answerable.
    /// Runs after appends are folded into <see cref="MetadataMutation.SetFields"/>.
    /// </summary>
    private static void RemoveOrphanedSchemaStamp(BoxNode root, MetadataMutation mutation)
    {
        string schemaKey = ClipMetaSchema.AtomName(ClipMetaSchema.Schema);
        string taggedByKey = ClipMetaSchema.AtomName(ClipMetaSchema.TaggedBy);
        string domainPrefix = ClipMetaSchema.Domain + ":";

        // Neither bookkeeping stamp (schema, tagged_by) counts as a "user" field, both may sit in
        // SetFields, added by the conditional stamps above.
        bool IsBookkeeping(string key) =>
            key.Equals(schemaKey, StringComparison.Ordinal) ||
            key.Equals(taggedByKey, StringComparison.Ordinal);

        // Is this write storing any user field? Then the stamps are earning their keep.
        bool storesUserField = mutation.SetFields.Any(kv =>
            !string.IsNullOrEmpty(kv.Value) &&
            kv.Key.StartsWith(domainPrefix, StringComparison.Ordinal) &&
            !IsBookkeeping(kv.Key));
        if (storesUserField)
            return;

        var ilst = FindIlst(root);
        if (ilst is null)
            return; // nothing stored, nothing to orphan

        // Does any existing user clipmeta atom survive this mutation?
        bool anySurvives = ilst.Children.Any(c =>
            c.EditableKey is { } key &&
            key.StartsWith(domainPrefix, StringComparison.Ordinal) &&
            !IsBookkeeping(key) &&
            !mutation.DeleteFields.Contains(key) &&
            !mutation.ClearAll);
        if (anySurvives)
            return;

        // No user fields after this write: the bookkeeping stamps are orphaned. ClearAll already
        // sweeps them; delete-only mutations need them added explicitly. (Harmless if absent, 
        // deleting a nonexistent field is a no-op.)
        mutation.DeleteFields.Add(schemaKey);
        mutation.DeleteFields.Add(taggedByKey);
    }

    /// <summary>
    /// Decides whether the mutation leaves the ilst empty, and if so how far up the container
    /// chain can be removed without touching anything foreign. Conservative by construction:
    /// meta goes only when ilst was its sole content besides the hdlr; udta goes only when
    /// meta was its sole child. A container holding ANY box we don't recognize as part of the
    /// chain stays, foreign content untouched (spec hazard #5).
    /// </summary>
    private static EmptyChainRemoval DetermineEmptyChainRemoval(
        BoxNode root, MetadataMutation mutation, Dictionary<string, string> newFields)
    {
        // Writing new values? The ilst cannot be empty.
        if (newFields.Count > 0)
            return default;

        var ilst = FindIlst(root);
        if (ilst is null)
            return default;

        // Would any atom survive in the ilst? (free children are padding the rewriter drops
        // anyway, so they don't keep a container alive.)
        bool anySurvives = ilst.Children.Any(c =>
            c.Type != "free" &&
            !(c.EditableKey is { } key &&
              (mutation.DeleteFields.Contains(key) ||
               (mutation.ClearAll &&
                key.StartsWith(ClipMetaSchema.Domain + ":", StringComparison.Ordinal)))));
        if (anySurvives)
            return default;

        bool dropIlst = true;

        var moov = root.Children.FirstOrDefault(c => c.Type == "moov");
        var udta = moov?.Children.FirstOrDefault(c => c.Type == "udta");
        var meta = udta?.Children.FirstOrDefault(c => c.Type == "meta");

        // meta is removable when, after the ilst goes, only its handler declaration is left.
        bool dropMeta = meta is not null &&
            meta.Children.All(c => c.Type is "hdlr" or "ilst" or "free");

        // udta is removable when the doomed meta was its only child.
        bool dropUdta = dropMeta && udta is not null &&
            udta.Children.All(c => c.Type is "meta");

        return new EmptyChainRemoval(dropIlst, dropMeta, dropUdta);
    }

    // ── Scenario determination ─────────────────────────────────────────────────

    private enum WriteScenario { Update, Append, Create }

    private static (WriteScenario scenario, List<BoxNode> existingIlstChildren, Dictionary<string, string> newFields)
        DetermineScenario(BoxNode root, MetadataMutation mutation)
    {
        var ilst = FindIlst(root);
        var newFields = CollectNewFields(mutation);

        if (ilst == null)
            return (WriteScenario.Create, new(), newFields);

        var existingChildren = ilst.Children.ToList();
        bool anyUpdate = newFields.Keys.Any(k => existingChildren.Any(c => c.EditableKey == k))
                      || mutation.DeleteFields.Any(k => existingChildren.Any(c => c.EditableKey == k));

        return anyUpdate
            ? (WriteScenario.Update, existingChildren, newFields)
            : (WriteScenario.Append, existingChildren, newFields);
    }

    private static Dictionary<string, string> CollectNewFields(MetadataMutation mutation)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in mutation.SetFields)
            if (!string.IsNullOrEmpty(v)) fields[k] = v!;
        return fields;
    }

    // ── Core write ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Streams the source file to the temp file. Every top-level box is copied byte-for-byte
    /// EXCEPT moov, which is rebuilt (that is where the metadata and the chunk-offset tables
    /// live). mdat in particular is never interpreted, only stream-copied in 64 KB chunks.
    /// </summary>
    /// <param name="src">
    /// The source stream the file was PARSED from, still open. Reusing the same deny-writers
    /// handle (rather than re-opening the path) guarantees the bytes we copy are the bytes the
    /// parse described, no other process can have modified the file in between.
    /// </param>
    /// <param name="predictedMoovSize">
    /// The moov size computed by <see cref="CalculateNewMoovSize"/>, the value the stco/co64
    /// delta was derived from. <see cref="WriteMoov"/> hard-fails if the moov it builds does
    /// not match this exactly.
    /// </param>
    private static void WriteToTemp(
        FileStream src, string tempPath, BoxNode root, MetadataMutation mutation,
        WriteScenario scenario, List<BoxNode> existingIlstChildren, Dictionary<string, string> newFields,
        long delta, long moovEndOffset, long predictedMoovSize, EmptyChainRemoval drop,
        IClipMetaLogger logger)
    {
        using var dst = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var srcReader = new BinaryReader(src, Encoding.Latin1, leaveOpen: true);
        using var dstWriter = new BinaryWriter(dst, Encoding.Latin1, leaveOpen: true);

        foreach (var topBox in root.Children)
        {
            if (topBox.Type == "moov")
                WriteMoov(srcReader, dstWriter, topBox, mutation, scenario,
                          existingIlstChildren, newFields, delta, moovEndOffset,
                          predictedMoovSize, drop, logger);
            else
                CopyBoxVerbatim(srcReader, dstWriter, topBox);
        }
    }

    /// <summary>
    /// Rebuilds the moov box into an in-memory buffer, then writes it to the destination.
    /// Buffering first is what lets us (a) know the final size before emitting the 8-byte
    /// header, and (b) assert that size against the prediction BEFORE anything is committed.
    /// moov is metadata-only and small (KBs to low MBs), so buffering it is safe, unlike mdat,
    /// which is never buffered.
    /// </summary>
    private static void WriteMoov(
        BinaryReader src, BinaryWriter dst, BoxNode moov, MetadataMutation mutation,
        WriteScenario scenario, List<BoxNode> existingIlstChildren, Dictionary<string, string> newFields,
        long delta, long moovEndOffset, long predictedMoovSize, EmptyChainRemoval drop,
        IClipMetaLogger logger)
    {
        using var moovBuf = new MemoryStream();
        using var moovWriter = new BinaryWriter(moovBuf, Encoding.Latin1, leaveOpen: true);

        foreach (var child in moov.Children)
        {
            if (child.Type == "udta" && drop.Udta)
                // The whole chain emptied out, the udta is not rebuilt at all.
                continue;
            if (child.Type == "trak")
                // trak is rebuilt (not copied) because its stbl subtree holds the
                // chunk-offset tables that may need patching.
                WriteTrak(src, moovWriter, child, delta, moovEndOffset, logger);
            else if (child.Type == "udta")
                // udta is rebuilt because its meta/ilst subtree holds the metadata atoms.
                WriteUdta(src, moovWriter, child, mutation, scenario,
                          existingIlstChildren, newFields, drop);
            else
                CopyBoxVerbatim(src, moovWriter, child);
        }

        // Create scenario with no udta at all: synthesize the whole udta→meta→hdlr→ilst chain.
        if (scenario == WriteScenario.Create && !moov.Children.Any(c => c.Type == "udta"))
            WriteNewUdtaChain(moovWriter, newFields);

        // THE CRITICAL ASSERT. The stco/co64 entries inside moovBuf were just shifted by a
        // delta that assumed the new moov would be exactly predictedMoovSize bytes. If the
        // bytes we actually produced disagree, any future bug in the size calculation, an
        // exotic box layout we mis-accounted, anything, those chunk offsets are wrong and
        // the file would play garbage. Failing here costs nothing (temp file is discarded);
        // proceeding would corrupt the clip silently.
        long actualMoovSize = 8 + moovBuf.Length;
        if (actualMoovSize != predictedMoovSize)
            throw new InvalidDataException(
                $"Internal size mismatch: rebuilt moov is {actualMoovSize} bytes but " +
                $"{predictedMoovSize} were predicted; chunk offsets would be corrupted. " +
                $"Write aborted, the original file is untouched.");

        BigEndianWriter.WriteBoxHeader(dst, (uint)actualMoovSize, "moov");
        moovBuf.Position = 0;
        moovBuf.CopyTo(dst.BaseStream);
    }

    private static void WriteTrak(
        BinaryReader src, BinaryWriter dst, BoxNode trak,
        long delta, long moovEndOffset, IClipMetaLogger logger)
    {
        using var trakBuf = new MemoryStream();
        using var trakWriter = new BinaryWriter(trakBuf, Encoding.Latin1, leaveOpen: true);

        foreach (var child in trak.Children)
        {
            if (child.Type == "mdia")
                WriteMdia(src, trakWriter, child, delta, moovEndOffset, logger);
            else
                CopyBoxVerbatim(src, trakWriter, child);
        }

        BigEndianWriter.WriteBoxHeader(dst, (uint)(8 + trakBuf.Length), "trak");
        trakBuf.Position = 0;
        trakBuf.CopyTo(dst.BaseStream);
    }

    private static void WriteMdia(
        BinaryReader src, BinaryWriter dst, BoxNode mdia,
        long delta, long moovEndOffset, IClipMetaLogger logger)
    {
        using var mdiaBuf = new MemoryStream();
        using var mdiaWriter = new BinaryWriter(mdiaBuf, Encoding.Latin1, leaveOpen: true);

        foreach (var child in mdia.Children)
        {
            if (child.Type == "minf")
                WriteMinf(src, mdiaWriter, child, delta, moovEndOffset, logger);
            else
                CopyBoxVerbatim(src, mdiaWriter, child);
        }

        BigEndianWriter.WriteBoxHeader(dst, (uint)(8 + mdiaBuf.Length), "mdia");
        mdiaBuf.Position = 0;
        mdiaBuf.CopyTo(dst.BaseStream);
    }

    private static void WriteMinf(
        BinaryReader src, BinaryWriter dst, BoxNode minf,
        long delta, long moovEndOffset, IClipMetaLogger logger)
    {
        using var minfBuf = new MemoryStream();
        using var minfWriter = new BinaryWriter(minfBuf, Encoding.Latin1, leaveOpen: true);

        foreach (var child in minf.Children)
        {
            if (child.Type == "stbl")
                WriteStbl(src, minfWriter, child, delta, moovEndOffset, logger);
            else
                CopyBoxVerbatim(src, minfWriter, child);
        }

        BigEndianWriter.WriteBoxHeader(dst, (uint)(8 + minfBuf.Length), "minf");
        minfBuf.Position = 0;
        minfBuf.CopyTo(dst.BaseStream);
    }

    private static void WriteStbl(
        BinaryReader src, BinaryWriter dst, BoxNode stbl,
        long delta, long moovEndOffset, IClipMetaLogger logger)
    {
        using var stblBuf = new MemoryStream();
        using var stblWriter = new BinaryWriter(stblBuf, Encoding.Latin1, leaveOpen: true);

        foreach (var child in stbl.Children)
        {
            if (child.Type == "stco" && delta != 0)
                WriteAdjustedStco(src, stblWriter, child, delta, moovEndOffset, logger);
            else if (child.Type == "co64" && delta != 0)
                WriteAdjustedCo64(src, stblWriter, child, delta, moovEndOffset, logger);
            else
                CopyBoxVerbatim(src, stblWriter, child);
        }

        BigEndianWriter.WriteBoxHeader(dst, (uint)(8 + stblBuf.Length), "stbl");
        stblBuf.Position = 0;
        stblBuf.CopyTo(dst.BaseStream);
    }

    private static void WriteAdjustedStco(
        BinaryReader src, BinaryWriter dst, BoxNode stco, long delta, long moovEndOffset, IClipMetaLogger logger)
    {
        src.BaseStream.Position = stco.FileOffset + stco.HeaderSize;
        byte ver = src.ReadByte();
        byte f1 = src.ReadByte(), f2 = src.ReadByte(), f3 = src.ReadByte();
        uint count = BigEndianReader.ReadUInt32(src);

        using var content = new MemoryStream();
        using var cw = new BinaryWriter(content, Encoding.Latin1, leaveOpen: true);
        cw.Write(ver); cw.Write(f1); cw.Write(f2); cw.Write(f3);
        BigEndianWriter.WriteUInt32(cw, count);

        for (uint i = 0; i < count; i++)
        {
            uint original = BigEndianReader.ReadUInt32(src);
            if ((long)original < moovEndOffset)
            {
                // Chunk is before moov end, mdat did not move, no adjustment needed.
                BigEndianWriter.WriteUInt32(cw, original);
                continue;
            }
            long adjusted = (long)original + delta;
            if (adjusted > uint.MaxValue)
                throw new InvalidOperationException(
                    $"stco offset overflow at entry {i}: {adjusted} > UInt32.MaxValue.");
            if (adjusted < 0)
                throw new InvalidOperationException(
                    $"stco offset underflow at entry {i}: {adjusted} < 0. Metadata shrink produced negative offset.");
            BigEndianWriter.WriteUInt32(cw, (uint)adjusted);
        }

        logger.LogVerbose($"STCO {count} entries += {delta}");
        BigEndianWriter.WriteBoxHeader(dst, (uint)(8 + content.Length), "stco");
        content.Position = 0;
        content.CopyTo(dst.BaseStream);
    }

    private static void WriteAdjustedCo64(
        BinaryReader src, BinaryWriter dst, BoxNode co64, long delta, long moovEndOffset, IClipMetaLogger logger)
    {
        src.BaseStream.Position = co64.FileOffset + co64.HeaderSize;
        byte ver = src.ReadByte();
        byte f1 = src.ReadByte(), f2 = src.ReadByte(), f3 = src.ReadByte();
        uint count = BigEndianReader.ReadUInt32(src);

        using var content = new MemoryStream();
        using var cw = new BinaryWriter(content, Encoding.Latin1, leaveOpen: true);
        cw.Write(ver); cw.Write(f1); cw.Write(f2); cw.Write(f3);
        BigEndianWriter.WriteUInt32(cw, count);

        for (uint i = 0; i < count; i++)
        {
            ulong original = BigEndianReader.ReadUInt64(src);
            if ((long)original < moovEndOffset)
            {
                // Chunk is before moov end, mdat did not move, no adjustment needed.
                BigEndianWriter.WriteUInt64(cw, original);
                continue;
            }
            long adjusted = (long)original + delta;
            if (adjusted < 0)
                throw new InvalidOperationException(
                    $"co64 offset underflow at entry {i}: {adjusted} < 0. Metadata shrink produced negative offset.");
            BigEndianWriter.WriteUInt64(cw, (ulong)adjusted);
        }

        logger.LogVerbose($"CO64 {count} entries += {delta}");
        BigEndianWriter.WriteBoxHeader(dst, (uint)(8 + content.Length), "co64");
        content.Position = 0;
        content.CopyTo(dst.BaseStream);
    }

    // ── ilst writing (Scenarios 1, 2, 3) ─────────────────────────────────────

    private static void WriteUdta(
        BinaryReader src, BinaryWriter dst, BoxNode udta, MetadataMutation mutation,
        WriteScenario scenario, List<BoxNode> existingIlstChildren, Dictionary<string, string> newFields,
        EmptyChainRemoval drop)
    {
        using var udtaBuf = new MemoryStream();
        using var udtaWriter = new BinaryWriter(udtaBuf, Encoding.Latin1, leaveOpen: true);

        bool hasMeta = udta.Children.Any(c => c.Type == "meta");
        foreach (var child in udta.Children)
        {
            if (child.Type == "meta" && drop.Meta)
                // meta held only its hdlr + the now-empty ilst, drop it entirely.
                continue;
            if (child.Type == "meta")
                WriteMeta(src, udtaWriter, child, mutation, scenario, existingIlstChildren, newFields, drop);
            else
                CopyBoxVerbatim(src, udtaWriter, child);
        }

        if (!hasMeta && scenario == WriteScenario.Create)
            WriteNewMetaChain(udtaWriter, newFields);

        BigEndianWriter.WriteBoxHeader(dst, (uint)(8 + udtaBuf.Length), "udta");
        udtaBuf.Position = 0;
        udtaBuf.CopyTo(dst.BaseStream);
    }

    private static void WriteNewMetaChain(BinaryWriter dst, Dictionary<string, string> newFields)
    {
        using var ilstBuf = new MemoryStream();
        using var ilstWriter = new BinaryWriter(ilstBuf, Encoding.Latin1, leaveOpen: true);
        foreach (var (key, value) in newFields)
        {
            int colonIdx = key.IndexOf(':', StringComparison.Ordinal);
            if (colonIdx < 0) continue;
            FreeformAtomWriter.Write(ilstWriter, key[..colonIdx], key[(colonIdx + 1)..], value);
        }
        byte[] ilstBytes = ilstBuf.ToArray();
        uint ilstSize = (uint)(8 + ilstBytes.Length);

        byte[] hdlrBody = new byte[21]; // 20 bytes fixed fields + 1-byte null-terminated name (ISO 14496-12)
        Encoding.Latin1.GetBytes("mdir").CopyTo(hdlrBody, 4);
        byte[] hdlrBytes = BuildFullBox("hdlr", 0, 0, hdlrBody);

        using var metaBuf = new MemoryStream();
        using var metaWriter = new BinaryWriter(metaBuf, Encoding.Latin1, leaveOpen: true);
        BigEndianWriter.WriteFullBoxPrefix(metaWriter, 0, 0);
        metaWriter.Write(hdlrBytes);
        BigEndianWriter.WriteBoxHeader(metaWriter, ilstSize, "ilst");
        metaWriter.Write(ilstBytes);
        byte[] metaBytes = metaBuf.ToArray();

        BigEndianWriter.WriteBoxHeader(dst, (uint)(8 + metaBytes.Length), "meta");
        dst.Write(metaBytes);
    }

    private static void WriteNewIlst(BinaryWriter dst, Dictionary<string, string> newFields)
    {
        using var ilstBuf = new MemoryStream();
        using var ilstWriter = new BinaryWriter(ilstBuf, Encoding.Latin1, leaveOpen: true);
        foreach (var (key, value) in newFields)
        {
            int colonIdx = key.IndexOf(':', StringComparison.Ordinal);
            if (colonIdx < 0) continue;
            FreeformAtomWriter.Write(ilstWriter, key[..colonIdx], key[(colonIdx + 1)..], value);
        }
        byte[] ilstBytes = ilstBuf.ToArray();
        BigEndianWriter.WriteBoxHeader(dst, (uint)(8 + ilstBytes.Length), "ilst");
        dst.Write(ilstBytes);
    }

    private static void WriteMeta(
        BinaryReader src, BinaryWriter dst, BoxNode meta, MetadataMutation mutation,
        WriteScenario scenario, List<BoxNode> existingIlstChildren, Dictionary<string, string> newFields,
        EmptyChainRemoval drop)
    {
        using var metaBuf = new MemoryStream();
        using var metaWriter = new BinaryWriter(metaBuf, Encoding.Latin1, leaveOpen: true);

        if (meta.IsFullBox)
        {
            metaWriter.Write(meta.Version);
            metaWriter.Write((byte)(meta.Flags >> 16));
            metaWriter.Write((byte)(meta.Flags >> 8));
            metaWriter.Write((byte)meta.Flags);
        }

        bool wroteIlst = false;
        foreach (var child in meta.Children)
        {
            if (child.Type == "ilst" && drop.Ilst)
                // The ilst would be empty, omit it, keeping the meta's hdlr (so a future
                // tag still has a valid mdir-handler meta to write into).
                continue;
            if (child.Type == "ilst")
            {
                wroteIlst = true;
                WriteIlst(src, metaWriter, child, mutation, scenario,
                          existingIlstChildren, newFields);
            }
            else
                CopyBoxVerbatim(src, metaWriter, child);
        }

        // Scenario: udta+meta exist but contain no ilst child. Synthesize one here so metadata
        // is written. CalculateNewMoovSize already accounts for newIlstSize bytes of growth.
        if (!wroteIlst && scenario == WriteScenario.Create)
            WriteNewIlst(metaWriter, newFields);

        BigEndianWriter.WriteBoxHeader(dst, (uint)(8 + metaBuf.Length), "meta");
        metaBuf.Position = 0;
        metaBuf.CopyTo(dst.BaseStream);
    }

    private static void WriteIlst(
        BinaryReader src, BinaryWriter dst, BoxNode ilst, MetadataMutation mutation,
        WriteScenario scenario, List<BoxNode> existingIlstChildren, Dictionary<string, string> newFields)
    {
        using var ilstBuf = new MemoryStream();
        using var ilstWriter = new BinaryWriter(ilstBuf, Encoding.Latin1, leaveOpen: true);

        var writtenKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var child in ilst.Children)
        {
            if (child.Type == "free") continue;

            string key = child.EditableKey ?? string.Empty;

            if (mutation.DeleteFields.Contains(key))
                continue;

            if (mutation.ClearAll && key.StartsWith(ClipMetaSchema.Domain + ":", StringComparison.Ordinal))
                continue;

            if (newFields.TryGetValue(key, out string? newValue))
            {
                if (child.Type == "----")
                {
                    int colonIdx = key.IndexOf(':', StringComparison.Ordinal);
                    string domain = key[..colonIdx];
                    string field = key[(colonIdx + 1)..];
                    FreeformAtomWriter.Write(ilstWriter, domain, field, newValue);
                }
                else
                {
                    // Non-freeform atoms (©nam, ©ART, etc.) require format-specific encoders
                    // that this engine does not implement. All clipmeta keys use '----' freeform atoms.
                    // Reaching here means the mutation contains a raw FourCC key, which is unsupported.
                    throw new InvalidOperationException(
                        $"Cannot update non-freeform ilst atom '{child.Type}' (key='{key}'). " +
                        $"Only '----' freeform atoms are writable by this engine.");
                }
                writtenKeys.Add(key);
            }
            else
            {
                CopyBoxVerbatim(src, ilstWriter, child);
            }
        }

        foreach (var (key, value) in newFields)
        {
            if (writtenKeys.Contains(key)) continue;
            int colonIdx = key.IndexOf(':', StringComparison.Ordinal);
            if (colonIdx < 0) continue;
            string domain = key[..colonIdx];
            string field = key[(colonIdx + 1)..];
            FreeformAtomWriter.Write(ilstWriter, domain, field, value);
        }

        BigEndianWriter.WriteBoxHeader(dst, (uint)(8 + ilstBuf.Length), "ilst");
        ilstBuf.Position = 0;
        ilstBuf.CopyTo(dst.BaseStream);
    }

    private static void WriteNewUdtaChain(BinaryWriter dst, Dictionary<string, string> newFields)
    {
        using var ilstBuf = new MemoryStream();
        using var ilstWriter = new BinaryWriter(ilstBuf, Encoding.Latin1, leaveOpen: true);

        foreach (var (key, value) in newFields)
        {
            int colonIdx = key.IndexOf(':', StringComparison.Ordinal);
            if (colonIdx < 0) continue;
            string domain = key[..colonIdx];
            string field = key[(colonIdx + 1)..];
            FreeformAtomWriter.Write(ilstWriter, domain, field, value);
        }

        byte[] ilstBytes = ilstBuf.ToArray();
        uint ilstSize = (uint)(8 + ilstBytes.Length);

        byte[] hdlrBody = new byte[21]; // 20 bytes fixed fields + 1-byte null-terminated name (ISO 14496-12)
        Encoding.Latin1.GetBytes("mdir").CopyTo(hdlrBody, 4);
        byte[] hdlrBytes = BuildFullBox("hdlr", 0, 0, hdlrBody);

        using var metaBuf = new MemoryStream();
        using var metaWriter = new BinaryWriter(metaBuf, Encoding.Latin1, leaveOpen: true);
        BigEndianWriter.WriteFullBoxPrefix(metaWriter, 0, 0);
        metaWriter.Write(hdlrBytes);
        BigEndianWriter.WriteBoxHeader(metaWriter, ilstSize, "ilst");
        metaWriter.Write(ilstBytes);

        byte[] metaBytes = metaBuf.ToArray();
        uint udtaSize = (uint)(8 + 8 + metaBytes.Length);

        BigEndianWriter.WriteBoxHeader(dst, udtaSize, "udta");
        BigEndianWriter.WriteBoxHeader(dst, (uint)(8 + metaBytes.Length), "meta");
        dst.Write(metaBytes);
    }

    private static byte[] BuildFullBox(string type, byte version, uint flags, byte[] body)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.Latin1, leaveOpen: true);
        uint size = (uint)(8 + 4 + body.Length);
        BigEndianWriter.WriteBoxHeader(bw, size, type);
        BigEndianWriter.WriteFullBoxPrefix(bw, version, flags);
        bw.Write(body);
        return ms.ToArray();
    }

    // ── Verbatim copy ─────────────────────────────────────────────────────────

    private static void CopyBoxVerbatim(BinaryReader src, BinaryWriter dst, BoxNode box)
    {
        src.BaseStream.Position = box.FileOffset;
        long bytesToCopy = (long)box.Size;
        const int ChunkSize = 65536;
        byte[] buffer = new byte[ChunkSize];
        while (bytesToCopy > 0)
        {
            int read = src.Read(buffer, 0, (int)Math.Min(bytesToCopy, ChunkSize));
            if (read == 0) break;
            dst.Write(buffer, 0, read);
            bytesToCopy -= read;
        }
    }

    // ── Size calculation helpers ───────────────────────────────────────────────

    private static long GetMoovSize(BoxNode root)
        => (long)(root.Children.FirstOrDefault(c => c.Type == "moov")?.Size ?? 0);

    private static long CalculateNewMoovSize(
        BoxNode root, WriteScenario scenario,
        List<BoxNode> existingIlstChildren, Dictionary<string, string> newFields,
        MetadataMutation mutation, EmptyChainRemoval drop)
    {
        long oldMoovSize = GetMoovSize(root);

        // The new moov always uses a standard 8-byte header. If the original used extended-size
        // encoding (16-byte header), account for the 8-byte reduction in the on-disk footprint.
        var moovForHeader = root.Children.FirstOrDefault(c => c.Type == "moov");
        long headerSizeDelta = 8 - (moovForHeader?.HeaderSize ?? 8); // 0 normally, -8 for extended-size moov

        // Empty-chain removal: the output omits the outermost container we can safely drop, so
        // moov shrinks by exactly that whole box's on-disk size. Predicting it precisely matters
        // because WriteMoov hard-asserts actual == predicted before any offsets are committed.
        if (drop.Ilst)
            return oldMoovSize - RemovedChainSize(root, drop) + headerSizeDelta;

        long oldIlstSize = FindIlst(root)?.Size is ulong s ? (long)s : 0;
        long newIlstSize = CalculateNewIlstSize(existingIlstChildren, newFields, mutation);
        long delta = newIlstSize - oldIlstSize;

        if (scenario == WriteScenario.Create && FindIlst(root) == null)
        {
            var moov = root.Children.FirstOrDefault(c => c.Type == "moov");
            bool hasUdta = moov?.Children.Any(c => c.Type == "udta") ?? false;
            bool hasMeta = hasUdta &&
                (moov!.Children.FirstOrDefault(c => c.Type == "udta")
                    ?.Children.Any(c => c.Type == "meta") ?? false);

            if (!hasUdta)
                delta += 53; // udta(8) + meta(8) + meta FullBox prefix(4) + hdlr(8+4+21=33) = 53
            else if (!hasMeta)
                delta += 45; // meta(8) + meta FullBox prefix(4) + hdlr(8+4+21=33) = 45
            // else: udta+meta exist, no ilst, WriteMeta synthesizes a new ilst via WriteNewIlst; delta = newIlstSize covers it.
        }

        return oldMoovSize + delta + headerSizeDelta;
    }

    /// <summary>
    /// On-disk size of the outermost container the empty-chain removal drops, the exact number
    /// of bytes moov loses. udta &gt; meta &gt; ilst, matching how far up
    /// <see cref="DetermineEmptyChainRemoval"/> found it safe to remove.
    /// </summary>
    private static long RemovedChainSize(BoxNode root, EmptyChainRemoval drop)
    {
        var moov = root.Children.FirstOrDefault(c => c.Type == "moov");
        var udta = moov?.Children.FirstOrDefault(c => c.Type == "udta");
        var meta = udta?.Children.FirstOrDefault(c => c.Type == "meta");
        var ilst = meta?.Children.FirstOrDefault(c => c.Type == "ilst");

        if (drop.Udta) return (long)(udta?.Size ?? 0);
        if (drop.Meta) return (long)(meta?.Size ?? 0);
        return (long)(ilst?.Size ?? 0);
    }

    private static long CalculateNewIlstSize(
        List<BoxNode> existing, Dictionary<string, string> newFields, MetadataMutation mutation)
    {
        long size = 8; // box header
        foreach (var child in existing)
        {
            if (child.Type == "free") continue;
            string key = child.EditableKey ?? string.Empty;
            if (mutation.DeleteFields.Contains(key)) continue;
            if (mutation.ClearAll && key.StartsWith(ClipMetaSchema.Domain + ":", StringComparison.Ordinal)) continue;

            if (newFields.TryGetValue(key, out string? newVal) && child.Type == "----")
            {
                int colon = key.IndexOf(':');
                if (colon < 0) { size += (long)child.Size; continue; }
                size += FreeformAtomWriter.CalculateSize(key[..colon], key[(colon + 1)..], newVal!);
            }
            else
            {
                size += (long)child.Size;
            }
        }
        foreach (var (key, val) in newFields)
        {
            // Skip only when an existing atom for this key will actually be preserved or updated
            // in the first loop above. A cleared (ClearAll) or deleted atom was skipped there, 
            // WriteIlst will still append the new value, so it must be counted here.
            bool existingHandledInFirstLoop = existing.Any(c =>
                c.EditableKey == key &&
                !mutation.DeleteFields.Contains(key) &&
                !(mutation.ClearAll && key.StartsWith(ClipMetaSchema.Domain + ":", StringComparison.Ordinal)));
            if (existingHandledInFirstLoop) continue;
            int colon = key.IndexOf(':');
            if (colon < 0) continue;
            size += FreeformAtomWriter.CalculateSize(key[..colon], key[(colon + 1)..], val);
        }
        return size;
    }

    // ── Fragmented MP4 detection ───────────────────────────────────────────────

    private static void DetectFragmented(BoxNode root, string filePath)
    {
        if (root.Children.Any(c => c.Type == "moof"))
            throw new UnsupportedFormatException(
                $"'{Path.GetFileName(filePath)}' uses fragmented MP4 format (contains moof boxes). " +
                $"Write is not supported for fragmented files.");
    }

    // ── Non-canonical metadata detection ───────────────────────────────────────

    /// <summary>
    /// Refuses to write when a <c>com.peckworkslab.clipmeta</c> freeform atom (a node whose
    /// <see cref="BoxNode.EditableKey"/> is domain-qualified, see <see cref="ClipMetaSchema.AtomName"/>)
    /// exists anywhere outside the canonical <c>moov.udta.meta.ilst</c> location this writer
    /// edits (see <see cref="FindIlst"/>). ISO 14496-12 legally permits a <c>meta</c> box
    /// directly under <c>moov</c>, or a <c>udta</c> under a <c>trak</c>, so a clipmeta atom can
    /// legally exist there too, this writer only ever edits the canonical copy. Scoped to OUR
    /// domain: a foreign <c>meta</c>/<c>ilst</c> (e.g. camera GPS/make/model metadata in Apple's
    /// <c>mdta</c>/keys format) is untouched, legitimate, and must not trip this guard.
    /// </summary>
    /// <exception cref="UnsupportedFormatException">
    /// When a non-canonical clipmeta atom is found.
    /// </exception>
    private static void DetectNonCanonicalMetadata(BoxNode root, string filePath)
    {
        string domainPrefix = ClipMetaSchema.Domain + ":";
        BoxNode? canonical = FindIlst(root);
        var withinCanonical = new HashSet<BoxNode>();
        if (canonical != null) CollectSubtree(canonical, withinCanonical);

        BoxNode? offender = FindNode(root, n =>
            !withinCanonical.Contains(n) &&
            n.EditableKey?.StartsWith(domainPrefix, StringComparison.Ordinal) == true);
        if (offender != null)
            throw new UnsupportedFormatException(
                $"'{Path.GetFileName(filePath)}' has metadata found at a non-canonical location " +
                $"({DescribeLocation(root, offender)}); clipmeta only edits moov.udta.meta.ilst " +
                $"and will not risk a divergent write, file refused.");
    }

    private static void CollectSubtree(BoxNode node, HashSet<BoxNode> set)
    {
        set.Add(node);
        foreach (var child in node.Children) CollectSubtree(child, set);
    }

    /// <summary>
    /// Dot-joined box-type path from just below the file root down to the containing
    /// <c>ilst</c> (trimmed there, the leaf atom itself isn't useful in the message), or down
    /// to <paramref name="target"/> if it has no <c>ilst</c> ancestor.
    /// </summary>
    private static string DescribeLocation(BoxNode root, BoxNode target)
    {
        var chain = new List<string>();
        if (!BuildChain(root, target, chain)) return target.Type;
        int ilstIndex = chain.IndexOf("ilst");
        if (ilstIndex >= 0) chain.RemoveRange(ilstIndex, chain.Count - ilstIndex);
        return chain.Count > 0 ? string.Join('.', chain) : target.Type;
    }

    private static bool BuildChain(BoxNode node, BoxNode target, List<string> chain)
    {
        if (node.Type != "root") chain.Add(node.Type);
        if (node == target) return true;
        if (node.Children.Any(c => BuildChain(c, target, chain))) return true;
        if (node.Type != "root") chain.RemoveAt(chain.Count - 1);
        return false;
    }

    // ── mdat position detection ────────────────────────────────────────────────

    private static long GetMoovEndOffset(BoxNode root)
    {
        var moov = root.Children.FirstOrDefault(c => c.Type == "moov");
        return moov != null ? moov.FileOffset + (long)moov.Size : 0;
    }

    // ── Pre-write safety gate ─────────────────────────────────────────────────

    /// <summary>
    /// Refuses to write unless the parse tree accounts for every single byte of the file.
    /// </summary>
    /// <remarks>
    /// <para>The parser stops (rather than throws) when it meets a box it cannot make sense of
    ///, e.g. a size field smaller than the 8-byte header, or a 64-bit size that overflows.
    /// That leniency is correct for *viewing* (show what you can), but fatal for *writing*:
    /// the writer emits only the boxes in the tree, so unparsed trailing bytes, which may be
    /// the entire mdat if the corrupt box sits between moov and mdat, would silently
    /// disappear from the output.</para>
    /// <para>Two conditions are enforced here:</para>
    /// <list type="bullet">
    ///   <item>Top-level boxes must tile the file exactly: box N+1 starts where box N ends,
    ///       the first starts at byte 0, and the last ends at the final byte.</item>
    ///   <item>No box anywhere in the tree may have been size-clamped by the parser. A clamped
    ///       box means its size field claims more bytes than its container holds (typical of a
    ///       truncated download), its on-disk header is lying, and copying it verbatim would
    ///       reproduce the lie around content we cannot vouch for.</item>
    /// </list>
    /// </remarks>
    /// <exception cref="UnsupportedFormatException">When any byte of the file is unaccounted for.</exception>
    private static void VerifyParseAccountsForWholeFile(BoxNode root, string filePath)
        => VerifyWholeFileAccounted(root, filePath);

    /// <summary>
    /// Public entry to the whole-file-accounting gate, so callers that adopt a file as
    /// authoritative (e.g. <see cref="ClipBackup.Restore"/> validating a backup before swapping
    /// it over the live clip) apply the SAME strictness the writer does before a write, the
    /// parse must tile the whole file and contain no size-clamped (truncated) box.
    /// </summary>
    /// <param name="root">Parse tree from <see cref="Mp4Parser.ParseFile"/>.</param>
    /// <param name="filePath">Path, for error messages.</param>
    /// <exception cref="UnsupportedFormatException">When any byte of the file is unaccounted for.</exception>
    public static void VerifyWholeFileAccounted(BoxNode root, string filePath)
    {
        long covered = 0;
        foreach (var child in root.Children)
        {
            // Defensive: ParseBoxes walks sequentially, so a gap here should be impossible.
            if (child.FileOffset != covered)
                throw new UnsupportedFormatException(
                    $"'{Path.GetFileName(filePath)}' has a {child.FileOffset - covered}-byte gap " +
                    $"at offset {covered} that the parser could not interpret. " +
                    $"Refusing to rewrite the file because the gap's contents would be lost.");
            covered = child.FileOffset + (long)child.Size;
        }

        // root.Size is the actual file length (set by Mp4Parser.ParseFile). Anything between
        // `covered` and the end of the file is data the parser gave up on.
        if (covered != (long)root.Size)
            throw new UnsupportedFormatException(
                $"'{Path.GetFileName(filePath)}' contains {(long)root.Size - covered} bytes at " +
                $"offset {covered} that could not be parsed as MP4 boxes (likely a corrupt box " +
                $"header). Refusing to rewrite the file because those bytes, possibly the " +
                $"entire video data, would be lost.");

        var clamped = FindNode(root, n => n.WasClamped);
        if (clamped != null)
            throw new UnsupportedFormatException(
                $"'{Path.GetFileName(filePath)}' has a '{clamped.Type}' box at offset " +
                $"{clamped.FileOffset} whose size field extends past its container (file " +
                $"truncated or corrupt). Refusing to rewrite.");
    }

    // ── Post-write verification ───────────────────────────────────────────────

    /// <summary>
    /// Validates the fully-written temp file against the original parse tree and the mutation,
    /// throwing (and thereby aborting the swap) on any discrepancy. Runs alongside two other
    /// checks performed by the caller: the temp-length arithmetic check and a full
    /// <see cref="VerifyParseAccountsForWholeFile"/> on the temp file.
    /// </summary>
    /// <param name="root">Parse tree of the temp file.</param>
    /// <param name="originalRoot">Parse tree of the original source file, for comparison.</param>
    private static void VerifyWrite(BoxNode root, BoxNode originalRoot, MetadataMutation mutation, string originalPath)
    {
        if (!root.Children.Any(c => c.Type == "moov"))
            throw new InvalidDataException(
                $"Verification failed: moov box missing in written file for '{originalPath}'.");

        // The media data itself must have survived: same number of mdat boxes as the source.
        // (Their content is guaranteed by the verbatim stream-copy + the temp-length check.)
        int mdatBefore = originalRoot.Children.Count(c => c.Type == "mdat");
        int mdatAfter = root.Children.Count(c => c.Type == "mdat");
        if (mdatBefore != mdatAfter)
            throw new InvalidDataException(
                $"Verification failed: original has {mdatBefore} mdat box(es) but written file " +
                $"has {mdatAfter} for '{originalPath}'.");

        // Every field this mutation stored must read back from the temp file.
        foreach (var (key, value) in mutation.SetFields)
        {
            if (string.IsNullOrEmpty(value)) continue;
            var node = FindEditableNode(root, key);
            if (node == null)
                throw new InvalidDataException(
                    $"Verification failed: atom '{key}' not found after write of '{originalPath}'.");
        }

        // --clear-all must leave no clipmeta atoms behind (other than fields this same
        // mutation explicitly set, which the public API permits even alongside ClearAll).
        if (mutation.ClearAll)
        {
            var leftover = FindNode(root, n =>
                n.EditableKey is { } k &&
                k.StartsWith(ClipMetaSchema.Domain + ":", StringComparison.Ordinal) &&
                !mutation.SetFields.ContainsKey(k));
            if (leftover != null)
                throw new InvalidDataException(
                    $"Verification failed: clear-all left atom '{leftover.EditableKey}' behind " +
                    $"in '{originalPath}'.");
        }
    }

    // ── Tree search helpers ───────────────────────────────────────────────────

    private static BoxNode? FindIlst(BoxNode root)
    {
        var moov = root.Children.FirstOrDefault(c => c.Type == "moov");
        var udta = moov?.Children.FirstOrDefault(c => c.Type == "udta");
        var meta = udta?.Children.FirstOrDefault(c => c.Type == "meta");
        return meta?.Children.FirstOrDefault(c => c.Type == "ilst");
    }

    private static BoxNode? FindEditableNode(BoxNode root, string editableKey)
        => FindNode(root, n => n.EditableKey == editableKey);

    private static BoxNode? FindNode(BoxNode node, Func<BoxNode, bool> predicate)
    {
        if (predicate(node)) return node;
        foreach (var child in node.Children)
        {
            var found = FindNode(child, predicate);
            if (found != null) return found;
        }
        return null;
    }

    private static int CountBoxes(BoxNode root)
    {
        int count = 1;
        foreach (var child in root.Children) count += CountBoxes(child);
        return count;
    }
}
