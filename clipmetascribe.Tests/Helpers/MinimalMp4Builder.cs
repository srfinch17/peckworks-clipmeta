using System.Text;

namespace ClipMetaScribe.Tests.Helpers;

/// <summary>
/// Builds minimal but structurally valid MP4 byte arrays for write engine unit tests.
/// All sizes are calculated and all integers are big-endian.
/// </summary>
internal static class MinimalMp4Builder
{
    // ── Low-level primitives ──────────────────────────────────────────────────

    private static void WriteBE32(BinaryWriter bw, uint v)
    {
        bw.Write((byte)(v >> 24));
        bw.Write((byte)(v >> 16));
        bw.Write((byte)(v >> 8));
        bw.Write((byte)v);
    }

    private static void WriteBE64(BinaryWriter bw, ulong v)
    {
        WriteBE32(bw, (uint)(v >> 32));
        WriteBE32(bw, (uint)v);
    }

    private static byte[] Box(string type, byte[] payload)
    {
        uint size = (uint)(8 + payload.Length);
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        WriteBE32(bw, size);
        bw.Write(Encoding.Latin1.GetBytes(type.PadRight(4)[..4]));
        bw.Write(payload);
        return ms.ToArray();
    }

    /// <summary>
    /// Builds a box with a 64-bit <c>largesize</c> header: the 32-bit size field is set to 1 and
    /// the real size follows as an 8-byte big-endian value, making the header 16 bytes instead of
    /// 8. Real muxers use this for boxes that may exceed 4 GB (notably mdat); here it lets a tiny
    /// fixture exercise the writer's 64-bit-header relocation path.
    /// </summary>
    private static byte[] LargesizeBox(string type, byte[] payload)
    {
        ulong size = (ulong)(16 + payload.Length);   // 4 (size==1) + 4 (type) + 8 (largesize) + payload
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        WriteBE32(bw, 1);                             // size field == 1 → 64-bit largesize follows
        bw.Write(Encoding.Latin1.GetBytes(type.PadRight(4)[..4]));
        WriteBE64(bw, size);
        bw.Write(payload);
        return ms.ToArray();
    }

    private static byte[] FullBox(string type, byte version, uint flags, byte[] payload)
    {
        byte[] header = new byte[4];
        header[0] = version;
        header[1] = (byte)(flags >> 16);
        header[2] = (byte)(flags >> 8);
        header[3] = (byte)flags;
        return Box(type, header.Concat(payload).ToArray());
    }

    // ── Atom builders ─────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a ---- freeform atom with mean (domain), name (field), and data children.
    /// Both mean and name are FullBoxes, version+flags are 4 bytes each.
    /// </summary>
    /// <param name="dataTypeIndicator">
    /// The data box's 24-bit type indicator: 1 = UTF-8 text (the default, and what clipmeta
    /// itself always writes). Pass another value (13 = JPEG, 21 = signed int, …) to fabricate
    /// a NON-text atom, used to prove the writer refuses to append to one rather than
    /// splicing its display placeholder into the file.
    /// </param>
    public static byte[] FreeformAtom(string domain, string fieldName, string value,
                                      int dataTypeIndicator = 1)
    {
        byte[] mean = FullBox("mean", 0, 0, Encoding.UTF8.GetBytes(domain));
        byte[] name = FullBox("name", 0, 0, Encoding.UTF8.GetBytes(fieldName));

        // data: version=0, 3-byte type indicator, locale=0000 (4 bytes), then value bytes
        byte[] dataPayload = new byte[]
            {
                0,
                (byte)(dataTypeIndicator >> 16),
                (byte)(dataTypeIndicator >> 8),
                (byte)dataTypeIndicator,
                0, 0, 0, 0,
            }
            .Concat(Encoding.UTF8.GetBytes(value))
            .ToArray();
        byte[] data = Box("data", dataPayload);

        return Box("----", mean.Concat(name).Concat(data).ToArray());
    }

    /// <summary>
    /// Builds a minimal ilst box containing zero or more freeform atoms.
    /// </summary>
    public static byte[] IlstBox(params byte[][] atoms)
        => Box("ilst", atoms.SelectMany(a => a).ToArray());

    /// <summary>
    /// Builds a minimal meta FullBox (handler_type="mdir") containing an ilst.
    /// </summary>
    public static byte[] MetaBox(byte[] ilstBox)
    {
        byte[] hdlrPayload = new byte[20];  // pre_defined(4) + handler_type(4) + reserved(12)
        Encoding.Latin1.GetBytes("mdir").CopyTo(hdlrPayload, 4);
        byte[] hdlr = FullBox("hdlr", 0, 0, hdlrPayload);
        return FullBox("meta", 0, 0, hdlr.Concat(ilstBox).ToArray());
    }

    /// <summary>Builds a minimal udta box wrapping a meta box.</summary>
    public static byte[] UdtaBox(byte[] metaBox) => Box("udta", metaBox);

    /// <summary>
    /// Builds a minimal stco FullBox with the given chunk offsets (big-endian uint32 each).
    /// </summary>
    public static byte[] StcoBox(params uint[] offsets)
    {
        byte[] entryCount = new byte[4];
        entryCount[0] = (byte)(offsets.Length >> 24);
        entryCount[1] = (byte)(offsets.Length >> 16);
        entryCount[2] = (byte)(offsets.Length >> 8);
        entryCount[3] = (byte)offsets.Length;

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(entryCount);
        foreach (uint o in offsets) WriteBE32(bw, o);
        return FullBox("stco", 0, 0, ms.ToArray());
    }

    /// <summary>
    /// Builds a minimal co64 FullBox with the given chunk offsets (big-endian uint64 each).
    /// co64 is stco's 64-bit sibling: identical layout but 8-byte entries. Used to exercise the
    /// writer's 64-bit offset-patching path. Entry width is fixed (8 bytes) regardless of value,
    /// so the two-pass moov build stays length-stable just as it does for stco.
    /// </summary>
    public static byte[] Co64Box(params ulong[] offsets)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        WriteBE32(bw, (uint)offsets.Length);   // entry_count
        foreach (ulong o in offsets) WriteBE64(bw, o);
        return FullBox("co64", 0, 0, ms.ToArray());
    }

    /// <summary>
    /// Builds a minimal stbl box wrapping a stco (or co64) box.
    /// (stts, stsc, stsz are omitted since the write engine only touches stco/co64.)
    /// </summary>
    public static byte[] StblBox(byte[] stcoBox) => Box("stbl", stcoBox);

    /// <summary>Wraps stbl in minf, minf in mdia, mdia in trak, minimal valid track chain.</summary>
    public static byte[] TrakBox(byte[] stcoBox)
    {
        byte[] stbl = StblBox(stcoBox);
        byte[] minf = Box("minf", stbl);
        byte[] mdia = Box("mdia", minf);
        return Box("trak", mdia);
    }

    /// <summary>
    /// Builds a complete moov box with optional udta and one or two tracks.
    /// mvhd is minimal (all zeros except size+type), which is sufficient for the write engine.
    /// Pass null for udtaBox when no udta is needed.
    /// </summary>
    public static byte[] MoovBox(byte[]? udtaBox, params byte[][] trakBoxes)
    {
        byte[] mvhd = FullBox("mvhd", 0, 0, new byte[96]); // v0 mvhd body = 96 bytes
        var children = new List<byte[]> { mvhd };
        children.AddRange(trakBoxes);
        if (udtaBox != null) children.Add(udtaBox);
        return Box("moov", children.SelectMany(b => b).ToArray());
    }

    /// <summary>Builds a minimal mdat box with N bytes of filler.</summary>
    public static byte[] MdatBox(int fillerBytes = 64)
        => Box("mdat", new byte[fillerBytes]);

    /// <summary>
    /// Assembles a complete moov-before-mdat MP4 file useful for stco adjustment tests.
    /// Returns the raw bytes as a MemoryStream.
    /// </summary>
    /// <param name="chunkOffset">The single stco entry; must point past end of moov.</param>
    public static MemoryStream BuildMp4WithStco(uint chunkOffset, string domain, string fieldName, string value)
    {
        byte[] freeform = FreeformAtom(domain, fieldName, value);
        byte[] ilst = IlstBox(freeform);
        byte[] meta = MetaBox(ilst);
        byte[] udta = UdtaBox(meta);
        byte[] stco = StcoBox(chunkOffset);
        byte[] trak = TrakBox(stco);
        byte[] moov = MoovBox(udta, trak);
        byte[] mdat = MdatBox();

        var ms = new MemoryStream();
        ms.Write(moov);
        ms.Write(mdat);
        ms.Position = 0;
        return ms;
    }

    /// <summary>
    /// Builds a moov-FIRST MP4 whose stco entries are REAL absolute offsets pointing at
    /// recognizable patterned chunks inside mdat. This is the fixture that actually exercises
    /// the dangerous code path: because moov precedes mdat, any change to moov's size shifts
    /// mdat, and the writer must patch every stco entry by exactly that shift.
    /// </summary>
    /// <remarks>
    /// Contrast with <see cref="BuildMp4WithStco"/>, whose single offset (e.g. 9999) points at
    /// nothing, fine for structural tests, useless for proving offsets were patched correctly.
    /// Layout produced:
    /// <code>
    ///   moov
    ///     mvhd
    ///     trak ─ mdia ─ minf ─ stbl ─ stco   (chunksPerTrak entries, track 0)
    ///     trak ─ mdia ─ minf ─ stbl ─ stco   (chunksPerTrak entries, track 1)
    ///     [udta ─ meta ─ hdlr + ilst]        (only when a seed field is supplied)
    ///   mdat   (traks × chunksPerTrak chunks of chunkSize bytes, each filled with a
    ///           distinct marker byte so misdirected offsets are unmistakable)
    /// </code>
    /// Two tracks are deliberate: PITFALLS hazard #2 is "only one stco adjusted, others
    /// missed", a single-track fixture cannot catch that bug.
    /// Building is two-pass: stco entry width is fixed, so a moov built with dummy offsets has
    /// the same length as the final one; measure it, compute the real offsets, rebuild.
    /// </remarks>
    /// <param name="seedDomain">When non-null, an ilst with one seed atom is included (so a test
    /// can start from the Update/Append scenario, or delete the atom to shrink moov).</param>
    /// <param name="seedDataType">Type indicator for the seed atom's data box; see
    /// <see cref="FreeformAtom"/>. Default 1 (UTF-8 text).</param>
    public static MemoryStream BuildMoovFirstWithPatternedMdat(
        string? seedDomain = null, string? seedField = null, string? seedValue = null,
        int traks = 2, int chunksPerTrak = 3, int chunkSize = 64, int seedDataType = 1)
    {
        byte[]? udta = seedDomain != null
            ? UdtaBox(MetaBox(IlstBox(FreeformAtom(seedDomain, seedField!, seedValue!, seedDataType))))
            : null;

        // mdat payload: chunk (t, c) is chunkSize bytes of the marker value 0xA0 + t*16 + c,
        // e.g. track 0 → A0 A1 A2..., track 1 → B0 B1 B2... Distinct everywhere, so if a chunk
        // offset lands even one byte off, the integrity comparison sees different markers.
        byte[] mdatPayload = new byte[traks * chunksPerTrak * chunkSize];
        for (int t = 0; t < traks; t++)
            for (int c = 0; c < chunksPerTrak; c++)
                Array.Fill(mdatPayload, (byte)(0xA0 + t * 16 + c),
                           (t * chunksPerTrak + c) * chunkSize, chunkSize);
        byte[] mdat = Box("mdat", mdatPayload);

        // Pass 1: dummy offsets, just to learn the moov length.
        byte[] moovDummy = BuildMultiTrackMoov(udta, traks, chunksPerTrak, new uint[traks * chunksPerTrak]);

        // mdat's payload starts right after moov plus mdat's own 8-byte header.
        long mdatPayloadStart = moovDummy.Length + 8;
        uint[] offsets = new uint[traks * chunksPerTrak];
        for (int i = 0; i < offsets.Length; i++)
            offsets[i] = (uint)(mdatPayloadStart + i * chunkSize);

        // Pass 2: same structure, real offsets. Lengths must match or the offsets are garbage.
        byte[] moov = BuildMultiTrackMoov(udta, traks, chunksPerTrak, offsets);
        if (moov.Length != moovDummy.Length)
            throw new InvalidOperationException("two-pass moov build produced different lengths");

        var ms = new MemoryStream();
        ms.Write(moov);
        ms.Write(mdat);
        ms.Position = 0;
        return ms;
    }

    /// <summary>Builds a moov containing one stco-bearing trak per track, slicing the flat
    /// offset array into per-track runs of <paramref name="chunksPerTrak"/> entries.</summary>
    private static byte[] BuildMultiTrackMoov(byte[]? udta, int traks, int chunksPerTrak, uint[] allOffsets)
    {
        byte[][] trakBoxes = Enumerable.Range(0, traks)
            .Select(t => TrakBox(StcoBox(
                allOffsets.Skip(t * chunksPerTrak).Take(chunksPerTrak).ToArray())))
            .ToArray();
        return MoovBox(udta, trakBoxes);
    }

    /// <summary>
    /// The co64 twin of <see cref="BuildMoovFirstWithPatternedMdat"/>: a moov-FIRST MP4 whose
    /// chunk-offset tables are <b>co64</b> (64-bit) rather than stco. This is the ONLY coverage of
    /// the writer rewriting a 64-bit offset table when moov growth shifts mdat, no real pristine
    /// clip is both moov-first AND co64 (the co64 real clips are all mdat-first, where offsets
    /// never move), and CI runs clip-less. Identical patterned-mdat / two-track / two-pass scheme
    /// as the stco builder; see its remarks. Parameters mirror it exactly.
    /// </summary>
    public static MemoryStream BuildMoovFirstCo64WithPatternedMdat(
        string? seedDomain = null, string? seedField = null, string? seedValue = null,
        int traks = 2, int chunksPerTrak = 3, int chunkSize = 64, int seedDataType = 1)
    {
        byte[]? udta = seedDomain != null
            ? UdtaBox(MetaBox(IlstBox(FreeformAtom(seedDomain, seedField!, seedValue!, seedDataType))))
            : null;

        byte[] mdatPayload = new byte[traks * chunksPerTrak * chunkSize];
        for (int t = 0; t < traks; t++)
            for (int c = 0; c < chunksPerTrak; c++)
                Array.Fill(mdatPayload, (byte)(0xA0 + t * 16 + c),
                           (t * chunksPerTrak + c) * chunkSize, chunkSize);
        byte[] mdat = Box("mdat", mdatPayload);

        // Pass 1: dummy offsets, just to learn the moov length (co64 entry width is fixed at 8).
        byte[] moovDummy = BuildMultiTrackMoovCo64(udta, traks, chunksPerTrak, new ulong[traks * chunksPerTrak]);

        long mdatPayloadStart = moovDummy.Length + 8;
        ulong[] offsets = new ulong[traks * chunksPerTrak];
        for (int i = 0; i < offsets.Length; i++)
            offsets[i] = (ulong)(mdatPayloadStart + i * chunkSize);

        // Pass 2: same structure, real offsets. Lengths must match or the offsets are garbage.
        byte[] moov = BuildMultiTrackMoovCo64(udta, traks, chunksPerTrak, offsets);
        if (moov.Length != moovDummy.Length)
            throw new InvalidOperationException("two-pass co64 moov build produced different lengths");

        var ms = new MemoryStream();
        ms.Write(moov);
        ms.Write(mdat);
        ms.Position = 0;
        return ms;
    }

    /// <summary>co64 twin of <see cref="BuildMultiTrackMoov"/>.</summary>
    private static byte[] BuildMultiTrackMoovCo64(byte[]? udta, int traks, int chunksPerTrak, ulong[] allOffsets)
    {
        byte[][] trakBoxes = Enumerable.Range(0, traks)
            .Select(t => TrakBox(Co64Box(
                allOffsets.Skip(t * chunksPerTrak).Take(chunksPerTrak).ToArray())))
            .ToArray();
        return MoovBox(udta, trakBoxes);
    }

    /// <summary>
    /// A moov-FIRST MP4 whose mdat carries a 64-bit <b>largesize</b> header (16-byte header, not
    /// 8). Uses ordinary 32-bit stco offsets, the point is the box HEADER, not the offset table:
    /// when moov grows and shifts the mdat, the writer must correctly parse and relocate a box
    /// whose own size is encoded as a 64-bit largesize. No real pristine clip is moov-first with a
    /// largesize mdat (the real largesize clips are all mdat-first, where the box never moves), so
    /// this is the only coverage of that path. The chunk-offset math accounts for the 16-byte
    /// header (payload starts at moov-length + 16, not + 8).
    /// </summary>
    public static MemoryStream BuildMoovFirstLargesizeMdatWithPatternedMdat(
        string? seedDomain = null, string? seedField = null, string? seedValue = null,
        int traks = 2, int chunksPerTrak = 3, int chunkSize = 64, int seedDataType = 1)
    {
        byte[]? udta = seedDomain != null
            ? UdtaBox(MetaBox(IlstBox(FreeformAtom(seedDomain, seedField!, seedValue!, seedDataType))))
            : null;

        byte[] mdatPayload = new byte[traks * chunksPerTrak * chunkSize];
        for (int t = 0; t < traks; t++)
            for (int c = 0; c < chunksPerTrak; c++)
                Array.Fill(mdatPayload, (byte)(0xA0 + t * 16 + c),
                           (t * chunksPerTrak + c) * chunkSize, chunkSize);
        byte[] mdat = LargesizeBox("mdat", mdatPayload);   // 16-byte largesize header

        byte[] moovDummy = BuildMultiTrackMoov(udta, traks, chunksPerTrak, new uint[traks * chunksPerTrak]);

        // mdat payload starts after moov + mdat's 16-byte LARGESIZE header (not the usual 8).
        long mdatPayloadStart = moovDummy.Length + 16;
        uint[] offsets = new uint[traks * chunksPerTrak];
        for (int i = 0; i < offsets.Length; i++)
            offsets[i] = (uint)(mdatPayloadStart + i * chunkSize);

        byte[] moov = BuildMultiTrackMoov(udta, traks, chunksPerTrak, offsets);
        if (moov.Length != moovDummy.Length)
            throw new InvalidOperationException("two-pass moov build produced different lengths");

        var ms = new MemoryStream();
        ms.Write(moov);
        ms.Write(mdat);
        ms.Position = 0;
        return ms;
    }

    /// <summary>
    /// Assembles a minimal MP4 whose metadata lives at the ISO 14496-12 legal but
    /// non-canonical <c>moov.meta.ilst</c> location (a <c>meta</c> box directly under
    /// <c>moov</c>, with no <c>udta</c> wrapper) instead of clipmeta's canonical
    /// <c>moov.udta.meta.ilst</c>. Mirrors <see cref="BuildMp4WithStco"/>'s minimal structural
    /// style; the chunk offset value is a placeholder, this fixture only proves write refusal,
    /// it is never actually rewritten.
    /// </summary>
    public static MemoryStream BuildMp4WithNonCanonicalMoovMetaIlst(
        uint chunkOffset, string domain, string fieldName, string value)
    {
        byte[] freeform = FreeformAtom(domain, fieldName, value);
        byte[] ilst = IlstBox(freeform);
        byte[] meta = MetaBox(ilst);   // moov-level meta: deliberately NOT wrapped in udta
        byte[] stco = StcoBox(chunkOffset);
        byte[] trak = TrakBox(stco);
        byte[] mvhd = FullBox("mvhd", 0, 0, new byte[96]);
        byte[] moov = Box("moov", mvhd.Concat(trak).Concat(meta).ToArray());
        byte[] mdat = MdatBox();

        var ms = new MemoryStream();
        ms.Write(moov);
        ms.Write(mdat);
        ms.Position = 0;
        return ms;
    }

    /// <summary>
    /// Assembles a minimal MP4 whose metadata lives at the ISO 14496-12 legal but
    /// non-canonical <c>trak.udta.meta.ilst</c> location (a <c>udta</c> box directly under a
    /// <c>trak</c>, sibling of <c>mdia</c>) instead of clipmeta's canonical
    /// <c>moov.udta.meta.ilst</c>. Proves the refusal gate also catches this second ISO-legal
    /// non-canonical location, not just a moov-level <c>meta</c>.
    /// </summary>
    public static MemoryStream BuildMp4WithNonCanonicalTrakUdtaMetaIlst(
        uint chunkOffset, string domain, string fieldName, string value)
    {
        byte[] freeform = FreeformAtom(domain, fieldName, value);
        byte[] trakUdta = UdtaBox(MetaBox(IlstBox(freeform)));
        byte[] mdia = Box("mdia", Box("minf", StblBox(StcoBox(chunkOffset))));
        byte[] trak = Box("trak", mdia.Concat(trakUdta).ToArray());
        byte[] mvhd = FullBox("mvhd", 0, 0, new byte[96]);
        byte[] moov = Box("moov", mvhd.Concat(trak).ToArray());
        byte[] mdat = MdatBox();

        var ms = new MemoryStream();
        ms.Write(moov);
        ms.Write(mdat);
        ms.Position = 0;
        return ms;
    }

    /// <summary>
    /// Builds a minimal ftyp box: major_brand (4 bytes) + minor_version (4 bytes) + one
    /// compatible brand (4 bytes). The writer's moov-less guard only cares about box
    /// boundaries at the top level, not ftyp's actual content, so this is deliberately bare.
    /// </summary>
    public static byte[] FtypBox()
    {
        byte[] payload = new byte[12];
        Encoding.Latin1.GetBytes("isom").CopyTo(payload, 0);
        // bytes 4..7 (minor_version) left zero
        Encoding.Latin1.GetBytes("isom").CopyTo(payload, 8);
        return Box("ftyp", payload);
    }

    /// <summary>
    /// Assembles a well-formed but moov-less MP4: <c>ftyp</c> + <c>mdat</c>, no <c>moov</c>
    /// anywhere. Models a recording interrupted before the muxer finalized the file (moov is
    /// conventionally written last). Task B5 scenario (a), the correctness reviewer's finding:
    /// before the fix this fell through <c>DetermineScenario</c> as <c>Create</c> and died at
    /// the internal temp-length check instead of refusing cleanly.
    /// </summary>
    public static MemoryStream BuildFtypMdatNoMoov()
    {
        byte[] ftyp = FtypBox();
        byte[] mdat = MdatBox();

        var ms = new MemoryStream();
        ms.Write(ftyp);
        ms.Write(mdat);
        ms.Position = 0;
        return ms;
    }

    /// <summary>
    /// Builds a top-level box with a 32-bit size field of <c>0</c> ("to end of file", see
    /// <see cref="ClipMetaCore.Mp4.BigEndianReader.ReadBoxHeader"/>). ISO 14496-12 only permits
    /// this for the LAST box in the file; here the box is deliberately followed by more bytes,
    /// modeling a malformed muxer output.
    /// </summary>
    private static byte[] Size0Box(string type, byte[] payload)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        WriteBE32(bw, 0); // size field == 0 -> BigEndianReader resolves this to end-of-stream
        bw.Write(Encoding.Latin1.GetBytes(type.PadRight(4)[..4]));
        bw.Write(payload);
        return ms.ToArray();
    }

    /// <summary>
    /// Assembles an MP4 whose <c>mdat</c> uses a to-EOF (<c>size=0</c>) header and is followed
    /// by a real, well-formed <c>moov</c> (carrying one seed clipmeta field). Because size=0
    /// resolves unconditionally to end-of-stream, the parser treats the moov's bytes as part of
    /// mdat's opaque payload rather than a sibling box, the resulting tree has NO moov box at
    /// all, even though moov's bytes are physically present in the file. Task B5 scenario (b),
    /// the nemesis's "swallowed moov" finding: a size=0 box that is not actually last silently
    /// eats everything after it, including moov, so reads report "(no clipmeta metadata)" with
    /// false confidence and writes must refuse rather than die at the internal temp-length check.
    /// </summary>
    public static MemoryStream BuildMdatSizeZeroSwallowingMoov(string domain, string fieldName, string value)
    {
        byte[] moov = MoovBox(UdtaBox(MetaBox(IlstBox(FreeformAtom(domain, fieldName, value)))));
        byte[] mdat = Size0Box("mdat", new byte[64]);

        var ms = new MemoryStream();
        ms.Write(mdat);
        ms.Write(moov); // physically present, but swallowed into mdat's to-EOF payload on parse
        ms.Position = 0;
        return ms;
    }

    /// <summary>
    /// Saves a byte stream to a temp file, returns the file path.
    /// Caller is responsible for deleting the file.
    /// </summary>
    public static string SaveToTempFile(MemoryStream ms, string extension = ".mp4")
    {
        string path = Path.ChangeExtension(Path.GetTempFileName(), extension);
        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }
}
