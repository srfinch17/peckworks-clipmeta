using System.Text;
using ClipMetaCore.Abstractions;
using ClipMetaCore.Mp4;
using ClipMetaCore.Schema;

namespace ClipMetaCore.Write;

/// <summary>
/// Writes clipmeta metadata mutations into MP4 files using a safe temp-file strategy.
/// The source file is NEVER opened for writing. If any step fails, the original is untouched.
/// </summary>
public sealed class Mp4Writer : IMediaWriter
{
    /// <inheritdoc/>
    public bool CanWrite(string filePath) =>
        Path.GetExtension(filePath).Equals(".mp4", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public void WriteMetadata(string filePath, MetadataMutation mutation, IClipMetaLogger logger)
    {
        if (mutation.DryRun)
        {
            logger.Log($"DRY RUN — no files will be modified: {filePath}");
            return;
        }

        Normalizer.ApplyToMutation(mutation);

        // Stamp schema version on every write
        mutation.SetFields.TryAdd(ClipMetaSchema.AtomName(ClipMetaSchema.Schema), ClipMetaSchema.SchemaVersion);

        string tempPath = filePath + ".tmp";
        // Verify the file can be opened for reading (basic accessibility check).
        // We do NOT acquire an exclusive lock because our write strategy always goes via a temp file,
        // and File.Replace is atomic; the source is only opened for reading during the copy.
        try
        {
            using (var accessCheck = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) { }
        }
        catch (IOException ex)
        {
            throw new IOException(
                $"'{Path.GetFileName(filePath)}' cannot be read. " +
                $"Verify the file exists and is accessible.", ex);
        }

        logger.Log($"WRITE {Path.GetFileName(filePath)} begin");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var root = Mp4Parser.ParseFile(filePath);
            DetectFragmented(root, filePath);
            logger.LogVerbose($"PARSE {CountBoxes(root)} boxes");

            foreach (var (key, appendValue) in mutation.AppendFields.ToList())
            {
                var existingNode = FindEditableNode(root, key);
                string current = existingNode?.DisplayValue is { } dv ? dv[1..^1] : string.Empty;
                string combined = string.IsNullOrEmpty(current)
                    ? appendValue
                    : Normalizer.AppendToPipeList(current, appendValue);
                mutation.SetFields[key] = combined;
            }
            mutation.AppendFields.Clear();

            var (scenario, ilstChildren, newFields) = DetermineScenario(root, mutation);
            logger.LogVerbose($"WRITE scenario={scenario}");

            long originalMoovSize = GetMoovSize(root);
            long newMoovSize = CalculateNewMoovSize(root, scenario, ilstChildren, newFields, mutation);
            long delta = newMoovSize - originalMoovSize;
            logger.LogVerbose($"WRITE delta={delta:+#;-#;0} bytes");

            long moovEndOffset = GetMoovEndOffset(root);

            WriteToTemp(filePath, tempPath, root, mutation, scenario, ilstChildren, newFields,
                        delta, moovEndOffset, logger);

            var verifyRoot = Mp4Parser.ParseFile(tempPath);
            VerifyWrite(verifyRoot, mutation, filePath);
            logger.LogVerbose($"VERIFY temp file re-parsed OK {CountBoxes(verifyRoot)} boxes intact");

            File.Replace(tempPath, filePath, destinationBackupFileName: mutation.BackupPath);
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

    private static void WriteToTemp(
        string sourcePath, string tempPath, BoxNode root, MetadataMutation mutation,
        WriteScenario scenario, List<BoxNode> existingIlstChildren, Dictionary<string, string> newFields,
        long delta, long moovEndOffset, IClipMetaLogger logger)
    {
        using var src = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var dst = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var srcReader = new BinaryReader(src, Encoding.Latin1, leaveOpen: true);
        using var dstWriter = new BinaryWriter(dst, Encoding.Latin1, leaveOpen: true);

        foreach (var topBox in root.Children)
        {
            if (topBox.Type == "moov")
                WriteMoov(srcReader, dstWriter, topBox, mutation, scenario,
                          existingIlstChildren, newFields, delta, moovEndOffset, logger);
            else
                CopyBoxVerbatim(srcReader, dstWriter, topBox);
        }
    }

    private static void WriteMoov(
        BinaryReader src, BinaryWriter dst, BoxNode moov, MetadataMutation mutation,
        WriteScenario scenario, List<BoxNode> existingIlstChildren, Dictionary<string, string> newFields,
        long delta, long moovEndOffset, IClipMetaLogger logger)
    {
        using var moovBuf = new MemoryStream();
        using var moovWriter = new BinaryWriter(moovBuf, Encoding.Latin1, leaveOpen: true);

        foreach (var child in moov.Children)
        {
            if (child.Type == "trak")
                WriteTrak(src, moovWriter, child, delta, moovEndOffset, logger);
            else if (child.Type == "udta")
                WriteUdta(src, moovWriter, child, mutation, scenario,
                          existingIlstChildren, newFields);
            else
                CopyBoxVerbatim(src, moovWriter, child);
        }

        if (scenario == WriteScenario.Create && !moov.Children.Any(c => c.Type == "udta"))
            WriteNewUdtaChain(moovWriter, newFields);

        uint newMoovSize = (uint)(8 + moovBuf.Length);
        BigEndianWriter.WriteBoxHeader(dst, newMoovSize, "moov");
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
                // Chunk is before moov end — mdat did not move, no adjustment needed.
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
                // Chunk is before moov end — mdat did not move, no adjustment needed.
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
        WriteScenario scenario, List<BoxNode> existingIlstChildren, Dictionary<string, string> newFields)
    {
        using var udtaBuf = new MemoryStream();
        using var udtaWriter = new BinaryWriter(udtaBuf, Encoding.Latin1, leaveOpen: true);

        bool hasMeta = udta.Children.Any(c => c.Type == "meta");
        foreach (var child in udta.Children)
        {
            if (child.Type == "meta")
                WriteMeta(src, udtaWriter, child, mutation, scenario, existingIlstChildren, newFields);
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
        WriteScenario scenario, List<BoxNode> existingIlstChildren, Dictionary<string, string> newFields)
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
        MetadataMutation mutation)
    {
        long oldIlstSize = FindIlst(root)?.Size is ulong s ? (long)s : 0;
        long newIlstSize = CalculateNewIlstSize(existingIlstChildren, newFields, mutation);
        long oldMoovSize = GetMoovSize(root);
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
            // else: udta+meta exist, no ilst — WriteMeta synthesizes a new ilst via WriteNewIlst; delta = newIlstSize covers it.
        }

        // The new moov always uses a standard 8-byte header. If the original used extended-size
        // encoding (16-byte header), account for the 8-byte reduction in the on-disk footprint.
        var moovNode = root.Children.FirstOrDefault(c => c.Type == "moov");
        int originalMoovHeaderSize = moovNode?.HeaderSize ?? 8;
        long headerSizeDelta = 8 - originalMoovHeaderSize; // 0 normally, -8 for extended-size moov

        return oldMoovSize + delta + headerSizeDelta;
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
            // in the first loop above. A cleared (ClearAll) or deleted atom was skipped there —
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

    // ── mdat position detection ────────────────────────────────────────────────

    private static long GetMoovEndOffset(BoxNode root)
    {
        var moov = root.Children.FirstOrDefault(c => c.Type == "moov");
        return moov != null ? moov.FileOffset + (long)moov.Size : 0;
    }

    // ── Verification ──────────────────────────────────────────────────────────

    private static void VerifyWrite(BoxNode root, MetadataMutation mutation, string originalPath)
    {
        if (!root.Children.Any(c => c.Type == "moov"))
            throw new InvalidDataException(
                $"Verification failed: moov box missing in written file for '{originalPath}'.");

        foreach (var (key, value) in mutation.SetFields)
        {
            if (string.IsNullOrEmpty(value)) continue;
            var node = FindEditableNode(root, key);
            if (node == null)
                throw new InvalidDataException(
                    $"Verification failed: atom '{key}' not found after write of '{originalPath}'.");
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
