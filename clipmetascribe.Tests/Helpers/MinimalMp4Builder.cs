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
    /// Builds a ---- freeform atom with mean (domain), name (field), and data (UTF-8 value).
    /// Both mean and name are FullBoxes — version+flags are 4 bytes each.
    /// </summary>
    public static byte[] FreeformAtom(string domain, string fieldName, string value)
    {
        byte[] mean = FullBox("mean", 0, 0, Encoding.UTF8.GetBytes(domain));
        byte[] name = FullBox("name", 0, 0, Encoding.UTF8.GetBytes(fieldName));

        // data: version=0, type=1 (UTF-8), locale=0000 (4 bytes), then value bytes
        byte[] dataPayload = new byte[] { 0, 0, 0, 1, 0, 0, 0, 0 }
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
    /// Builds a minimal stbl box wrapping a stco box.
    /// (stts, stsc, stsz are omitted since the write engine only touches stco/co64.)
    /// </summary>
    public static byte[] StblBox(byte[] stcoBox) => Box("stbl", stcoBox);

    /// <summary>Wraps stbl in minf, minf in mdia, mdia in trak — minimal valid track chain.</summary>
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
    /// nothing — fine for structural tests, useless for proving offsets were patched correctly.
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
    /// missed" — a single-track fixture cannot catch that bug.
    /// Building is two-pass: stco entry width is fixed, so a moov built with dummy offsets has
    /// the same length as the final one; measure it, compute the real offsets, rebuild.
    /// </remarks>
    /// <param name="seedDomain">When non-null, an ilst with one seed atom is included (so a test
    /// can start from the Update/Append scenario, or delete the atom to shrink moov).</param>
    public static MemoryStream BuildMoovFirstWithPatternedMdat(
        string? seedDomain = null, string? seedField = null, string? seedValue = null,
        int traks = 2, int chunksPerTrak = 3, int chunkSize = 64)
    {
        byte[]? udta = seedDomain != null
            ? UdtaBox(MetaBox(IlstBox(FreeformAtom(seedDomain, seedField!, seedValue!))))
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
