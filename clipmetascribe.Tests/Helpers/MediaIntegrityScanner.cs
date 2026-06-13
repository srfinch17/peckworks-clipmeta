using System.Security.Cryptography;

namespace ClipMetaScribe.Tests.Helpers;

/// <summary>
/// A deliberately independent MP4 scanner used to prove, byte-for-byte, that a metadata write
/// did not damage the media data. It shares NO code with clipmeta.core — it re-implements the
/// minimal box walking it needs — so that a bug in the production parser cannot mask an
/// identical bug in the writer (if both used the same code, a shared mistake would round-trip
/// "cleanly" and the tests would pass while real players choke).
/// </summary>
/// <remarks>
/// What "media integrity" means here, and why each piece matters:
/// <list type="bullet">
///   <item><b>mdat payloads</b> — the actual compressed video/audio bytes. After a metadata
///       write these must be IDENTICAL (verified by SHA-256). The writer only ever
///       stream-copies mdat, so any difference means catastrophic corruption.</item>
///   <item><b>Chunk-offset tables (stco/co64)</b> — every entry is an ABSOLUTE file offset
///       telling the player where a chunk of samples starts. When the moov box grows or
///       shrinks, everything after it slides, so the writer must patch each entry by exactly
///       that delta. The proof that it did: the bytes found at the OLD offset in the ORIGINAL
///       file must equal the bytes at the NEW offset in the REWRITTEN file. Checking the
///       bytes (not the arithmetic) means we don't have to trust anyone's delta math.</item>
///   <item><b>Top-level box inventory</b> — the sequence of top-level box types must be
///       unchanged; a missing mdat or a dropped trailing box is instantly visible.</item>
/// </list>
/// </remarks>
internal static class MediaIntegrityScanner
{
    /// <summary>Everything the scanner extracts from one file in a single pass.</summary>
    internal sealed class Snapshot
    {
        /// <summary>Top-level box types in file order, e.g. ["ftyp", "mdat", "moov"].</summary>
        public List<string> TopLevelTypes { get; } = new();

        /// <summary>Payload range (start offset, length) of every mdat box, in file order.</summary>
        public List<(long Start, long Length)> MdatPayloads { get; } = new();

        /// <summary>
        /// Every stco/co64 table found under moov→trak→mdia→minf→stbl, in document order.
        /// One table per track is typical (a video+audio clip has two).
        /// </summary>
        public List<(string Type, List<long> Offsets)> ChunkTables { get; } = new();
    }

    /// <summary>
    /// Walks the file's box structure and returns a <see cref="Snapshot"/>.
    /// Handles all three MP4 size encodings: normal 32-bit, extended 64-bit (size field == 1),
    /// and to-end-of-file (size field == 0).
    /// </summary>
    public static Snapshot Scan(string path)
    {
        using var f = File.OpenRead(path);
        var snapshot = new Snapshot();
        Walk(f, 0, f.Length, snapshot, topLevel: true);
        return snapshot;
    }

    private static void Walk(FileStream f, long start, long end, Snapshot snap, bool topLevel)
    {
        byte[] hdr = new byte[16];
        long pos = start;

        // A box needs at least 8 bytes (4 size + 4 type); stop when fewer remain.
        while (pos + 8 <= end)
        {
            f.Position = pos;
            if (!ReadExactly(f, hdr, 8)) break;

            ulong size = ReadBE32(hdr, 0);
            string type = System.Text.Encoding.Latin1.GetString(hdr, 4, 4);
            int headerSize = 8;

            if (size == 1)
            {
                // Extended size: the real 64-bit length follows the type field.
                if (!ReadExactly(f, hdr, 8)) break;
                size = ReadBE64(hdr, 0);
                headerSize = 16;
            }
            else if (size == 0)
            {
                // To-EOF: the box runs from here to the end of the enclosing range.
                size = (ulong)(end - pos);
            }

            if (size < (ulong)headerSize) break;     // corrupt size field — stop scanning
            long boxEnd = pos + (long)size;
            if (boxEnd > end) boxEnd = end;          // clamp boxes that overrun their container

            if (topLevel) snap.TopLevelTypes.Add(type);

            if (type == "mdat")
            {
                snap.MdatPayloads.Add((pos + headerSize, boxEnd - pos - headerSize));
            }
            else if (type is "moov" or "trak" or "mdia" or "minf" or "stbl")
            {
                // Pure container boxes on the path to the chunk tables — recurse.
                Walk(f, pos + headerSize, boxEnd, snap, topLevel: false);
            }
            else if (type is "stco" or "co64")
            {
                snap.ChunkTables.Add((type, ReadChunkTable(f, pos + headerSize, type)));
            }

            pos = boxEnd;
        }
    }

    /// <summary>
    /// Reads one stco/co64 table body. Both are FullBoxes:
    /// [1 byte version][3 bytes flags][4 bytes entry_count][entries...]
    /// where each entry is a 4-byte (stco) or 8-byte (co64) big-endian absolute file offset.
    /// </summary>
    private static List<long> ReadChunkTable(FileStream f, long bodyStart, string type)
    {
        f.Position = bodyStart;
        byte[] buf = new byte[8];
        ReadExactly(f, buf, 8);                       // version+flags (4) + entry_count (4)
        uint count = (uint)ReadBE32(buf, 4);

        int entrySize = type == "stco" ? 4 : 8;
        var offsets = new List<long>((int)count);
        for (uint i = 0; i < count; i++)
        {
            if (!ReadExactly(f, buf, entrySize)) break;
            offsets.Add(entrySize == 4 ? (long)ReadBE32(buf, 0) : (long)ReadBE64(buf, 0));
        }
        return offsets;
    }

    // ── Assertions ────────────────────────────────────────────────────────────

    /// <summary>
    /// The core integrity assertion: proves the rewritten file still contains exactly the same
    /// media as the original, regardless of how its metadata changed. Fails the test with a
    /// specific message at the first discrepancy.
    /// </summary>
    /// <param name="originalPath">The file as it was BEFORE the write under test.</param>
    /// <param name="rewrittenPath">The file AFTER the write under test.</param>
    public static void AssertMediaUnchanged(string originalPath, string rewrittenPath)
    {
        var before = Scan(originalPath);
        var after = Scan(rewrittenPath);

        // 1. Same top-level boxes in the same order — instantly catches a dropped mdat.
        CollectionAssert.AreEqual(before.TopLevelTypes, after.TopLevelTypes,
            $"Top-level box inventory changed: [{string.Join(",", before.TopLevelTypes)}] → " +
            $"[{string.Join(",", after.TopLevelTypes)}]");

        // 2. Every mdat payload byte-identical (length + SHA-256).
        Assert.AreEqual(before.MdatPayloads.Count, after.MdatPayloads.Count, "mdat box count changed");
        for (int i = 0; i < before.MdatPayloads.Count; i++)
        {
            Assert.AreEqual(before.MdatPayloads[i].Length, after.MdatPayloads[i].Length,
                $"mdat[{i}] payload length changed");
            Assert.AreEqual(
                HashRange(originalPath, before.MdatPayloads[i]),
                HashRange(rewrittenPath, after.MdatPayloads[i]),
                $"mdat[{i}] payload bytes changed (SHA-256 mismatch) — media data corrupted");
        }

        // 3. Chunk tables: identical shape, and every entry must point at the same data.
        Assert.AreEqual(before.ChunkTables.Count, after.ChunkTables.Count, "chunk table count changed");
        using var fOrig = File.OpenRead(originalPath);
        using var fNew = File.OpenRead(rewrittenPath);
        byte[] bytesAtOldOffset = new byte[64];
        byte[] bytesAtNewOffset = new byte[64];

        for (int t = 0; t < before.ChunkTables.Count; t++)
        {
            var (typeBefore, offsetsBefore) = before.ChunkTables[t];
            var (typeAfter, offsetsAfter) = after.ChunkTables[t];
            Assert.AreEqual(typeBefore, typeAfter, $"chunk table[{t}] changed type");
            Assert.AreEqual(offsetsBefore.Count, offsetsAfter.Count, $"chunk table[{t}] entry count changed");

            for (int i = 0; i < offsetsBefore.Count; i++)
            {
                // The decisive check: whatever bytes the old offset addressed in the original,
                // the new offset must address in the rewritten file. If the writer shifted an
                // entry by the wrong delta (or forgot a table), this fails immediately.
                //
                // The comparison window is clamped to the END of the mdat that contains the
                // offset: a chunk near the mdat boundary (ZC112's last chunk sits 38 bytes from
                // it) would otherwise have a fixed 64-byte read spill into the NEXT box — moov,
                // which legitimately changed when metadata was written — and report a false
                // "plays garbage". The chunk's actual sample bytes live inside mdat; comparing
                // past mdat-end compares non-media. (mdat itself is already proven identical
                // byte-for-byte by the SHA-256 check above, so this remains a strict offset
                // check, just bounded to real media.)
                int oldLimit = ClampToMdatEnd(offsetsBefore[i], before.MdatPayloads, bytesAtOldOffset.Length);
                int newLimit = ClampToMdatEnd(offsetsAfter[i], after.MdatPayloads, bytesAtNewOffset.Length);
                int gotOld = ReadAt(fOrig, offsetsBefore[i], bytesAtOldOffset, oldLimit);
                int gotNew = ReadAt(fNew, offsetsAfter[i], bytesAtNewOffset, newLimit);
                Assert.AreEqual(gotOld, gotNew,
                    $"chunk table[{t}] entry {i}: readable byte count differs " +
                    $"(old offset {offsetsBefore[i]}, new offset {offsetsAfter[i]})");
                CollectionAssert.AreEqual(
                    bytesAtOldOffset.Take(gotOld).ToArray(),
                    bytesAtNewOffset.Take(gotNew).ToArray(),
                    $"chunk table[{t}] entry {i} points at different data after rewrite " +
                    $"(old offset {offsetsBefore[i]}, new offset {offsetsAfter[i]}) — " +
                    $"the track would play garbage");
            }
        }
    }

    // ── Low-level helpers ─────────────────────────────────────────────────────

    /// <summary>Reads up to <paramref name="maxCount"/> bytes at an absolute offset; returns how many were available.</summary>
    private static int ReadAt(FileStream f, long offset, byte[] buffer, int maxCount)
    {
        if (offset < 0 || offset >= f.Length) return -1;   // offset points outside the file
        f.Position = offset;
        return f.Read(buffer, 0, Math.Min(maxCount, buffer.Length));
    }

    /// <summary>
    /// How many bytes from <paramref name="offset"/> may be compared without reading past the
    /// end of the mdat that contains it — capped at <paramref name="cap"/>. A chunk offset
    /// addresses sample data inside some mdat; comparing beyond that mdat's last byte compares
    /// non-media (the next box), which can change for legitimate reasons. If no mdat contains
    /// the offset (unexpected), the full cap is allowed so the check still runs.
    /// </summary>
    private static int ClampToMdatEnd(long offset, List<(long Start, long Length)> mdats, int cap)
    {
        foreach (var (start, length) in mdats)
        {
            long end = start + length;
            if (offset >= start && offset < end)
                return (int)Math.Min(cap, end - offset);
        }
        return cap;
    }

    /// <summary>SHA-256 of a byte range, streamed in 1 MB blocks so huge mdats never load whole.</summary>
    private static string HashRange(string path, (long Start, long Length) range)
    {
        using var f = File.OpenRead(path);
        f.Position = range.Start;
        using var sha = SHA256.Create();
        byte[] buf = new byte[1 << 20];
        long remaining = range.Length;
        while (remaining > 0)
        {
            int n = f.Read(buf, 0, (int)Math.Min(remaining, buf.Length));
            if (n == 0) break;
            sha.TransformBlock(buf, 0, n, null, 0);
            remaining -= n;
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!);
    }

    /// <summary>Fills exactly <paramref name="count"/> bytes or reports failure (short file).</summary>
    private static bool ReadExactly(FileStream f, byte[] buffer, int count)
    {
        int total = 0;
        while (total < count)
        {
            int n = f.Read(buffer, total, count - total);
            if (n == 0) return false;
            total += n;
        }
        return true;
    }

    private static ulong ReadBE32(byte[] b, int at)
        => ((ulong)b[at] << 24) | ((ulong)b[at + 1] << 16) | ((ulong)b[at + 2] << 8) | b[at + 3];

    private static ulong ReadBE64(byte[] b, int at)
        => (ReadBE32(b, at) << 32) | ReadBE32(b, at + 4);
}
