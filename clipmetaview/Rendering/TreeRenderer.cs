using ClipMetaCore.Mp4;

namespace ClipMetaView.Rendering;

/// <summary>
/// Renders a <see cref="BoxNode"/> tree using Unicode box-drawing characters with color coding.
/// Accepts an optional <see cref="TextWriter"/> so callers can capture output without
/// redirecting <see cref="Console.Out"/> (which is not thread-safe in parallel tests).
/// Console foreground colors are applied only when writing to the actual console.
/// </summary>
public static class TreeRenderer
{
    private const string BranchMid      = "├── ";
    private const string BranchLast     = "└── ";
    private const string PrefixContinue = "│   ";
    private const string PrefixEmpty    = "    ";

    // Maximum characters allowed for a DisplayValue before truncation.
    private const int MaxValueWidth = 72;

    /// <summary>
    /// Writes the full box tree for <paramref name="filePath"/> to <paramref name="writer"/>
    /// (defaults to <see cref="Console.Out"/>), followed by a legend.
    /// Console colors are reset in a <c>finally</c> block before returning.
    /// </summary>
    public static void Render(BoxNode root, string filePath, TextWriter? writer = null)
    {
        writer ??= Console.Out;
        bool useColor = IsConsole(writer);

        try
        {
            string fileName = Path.GetFileName(filePath);
            string sizeDisplay = GetFileSizeDisplay(filePath);

            SetColor(ConsoleColor.White, useColor);
            writer.WriteLine($"{fileName}  ({sizeDisplay})");
            ResetColor(useColor);

            var children = root.Children;
            for (int i = 0; i < children.Count; i++)
                RenderNode(children[i], prefix: string.Empty, isLast: i == children.Count - 1, writer, useColor);

            writer.WriteLine();
            RenderLegend(writer, useColor);
        }
        finally
        {
            Console.ResetColor();
        }
    }

    private static void RenderNode(BoxNode node, string prefix, bool isLast, TextWriter writer, bool useColor)
    {
        string connector = isLast ? BranchLast : BranchMid;

        // Tree branch in dark gray.
        SetColor(ConsoleColor.DarkGray, useColor);
        writer.Write(prefix + connector);

        // Node content in category color.
        ConsoleColor nodeColor = GetNodeColor(node);
        SetColor(nodeColor, useColor);
        writer.Write(BuildNodeLine(node));

        if (node.IsEditable)
        {
            SetColor(ConsoleColor.DarkGray, useColor);
            writer.Write("  ");
            SetColor(ConsoleColor.Magenta, useColor);
            writer.Write("← [EDITABLE]");
        }

        ResetColor(useColor);
        writer.WriteLine();

        // mdat is never recursed, raw media bytes, not boxes.
        if (node.Type == "mdat" || node.Children.Count == 0)
            return;

        string childPrefix = prefix + (isLast ? PrefixEmpty : PrefixContinue);
        for (int i = 0; i < node.Children.Count; i++)
            RenderNode(node.Children[i], childPrefix, isLast: i == node.Children.Count - 1, writer, useColor);
    }

    private static string BuildNodeLine(BoxNode node)
    {
        string friendlyName = MetadataKeys.GetName(node.Type);
        bool nameIsDistinct = !string.Equals(friendlyName, node.Type, StringComparison.Ordinal);

        string typeAndName = nameIsDistinct
            ? $"{node.Type}  {friendlyName}"
            : node.Type;

        string sizeAndOffset = $"[{node.Size:N0} bytes @ 0x{node.FileOffset:X}]";

        string extra = node.Type == "mdat" ? "  (raw media, not expanded)" : string.Empty;

        if (!string.IsNullOrEmpty(node.DisplayValue))
        {
            string val = node.DisplayValue.Length > MaxValueWidth
                ? node.DisplayValue[..MaxValueWidth] + "…"
                : node.DisplayValue;
            return $"{typeAndName}  {val}  {sizeAndOffset}{extra}";
        }

        return $"{typeAndName}  {sizeAndOffset}{extra}";
    }

    private static ConsoleColor GetNodeColor(BoxNode node)
    {
        if (node.IsEditable) return ConsoleColor.Yellow;

        return MetadataKeys.GetCategory(node.Type) switch
        {
            BoxCategory.Structural   => ConsoleColor.Cyan,
            BoxCategory.Technical    => ConsoleColor.DarkGray,
            BoxCategory.Header       => ConsoleColor.Green,
            BoxCategory.Media        => ConsoleColor.DarkCyan,
            BoxCategory.WindowsMedia => ConsoleColor.DarkYellow,
            _                        => ConsoleColor.Gray,
        };
    }

    // ─── Legend ─────────────────────────────────────────────────────────────

    private static void RenderLegend(TextWriter writer, bool useColor)
    {
        const string Rule = "══════════════════════════════════════════════════════════════════";

        SetColor(ConsoleColor.DarkGray, useColor);
        writer.WriteLine(Rule);

        SetColor(ConsoleColor.White, useColor);
        writer.WriteLine(" LEGEND, clipmetaview MP4 structure viewer");

        SetColor(ConsoleColor.DarkGray, useColor);
        writer.WriteLine(Rule);
        ResetColor(useColor);

        writer.WriteLine();
        writer.WriteLine(" Output format:  TYPE  Friendly Name  value  [size @ file-offset]");
        writer.WriteLine();

        // Color key
        SetColor(ConsoleColor.White, useColor);
        writer.WriteLine(" COLOR KEY");
        ResetColor(useColor);

        WriteLegendColorSwatch(writer, useColor, ConsoleColor.Cyan,      "  Structural container  ", "organizes child boxes (moov, trak, mdia…)");
        WriteLegendColorSwatch(writer, useColor, ConsoleColor.Green,     "  Header / info box     ", "parseable technical value shown inline");
        WriteLegendColorSwatch(writer, useColor, ConsoleColor.DarkGray,  "  Technical / codec     ", "internal sample-table or codec structure");
        WriteLegendColorSwatch(writer, useColor, ConsoleColor.DarkCyan,  "  Media data (mdat)     ", "raw encoded audio/video bytes, not expanded");
        WriteLegendColorSwatch(writer, useColor, ConsoleColor.Yellow,    "  iTunes metadata       ", "iTunes tag field (©nam, ©ART, covr…), editable with clipmetaedit");
        WriteLegendColorSwatch(writer, useColor, ConsoleColor.DarkYellow,"  Windows Media (WM/)   ", "attribute written by Windows File Explorer (Tags, Director…)");
        WriteLegendColorSwatch(writer, useColor, ConsoleColor.Magenta,   "  ← [EDITABLE] marker  ", "appears on every field that can be modified");
        WriteLegendColorSwatch(writer, useColor, ConsoleColor.Gray,      "  Unknown / vendor      ", "unrecognized or vendor-specific extension box");

        writer.WriteLine();
        SetColor(ConsoleColor.DarkGray, useColor);
        writer.WriteLine(Rule);
        SetColor(ConsoleColor.White, useColor);
        writer.WriteLine(" COMMON BOX TYPES");
        SetColor(ConsoleColor.DarkGray, useColor);
        writer.WriteLine(Rule);
        ResetColor(useColor);
        writer.WriteLine();

        // Structural containers
        WriteLegendBoxRow(writer, useColor, BoxCategory.Structural, "moov", "Movie Container",  "root container for all structure and metadata");
        WriteLegendBoxRow(writer, useColor, BoxCategory.Structural, "trak", "Track",             "one media stream: video, audio, timecode, or subtitle");
        WriteLegendBoxRow(writer, useColor, BoxCategory.Structural, "mdia", "Media",             "media-type container for a track");
        WriteLegendBoxRow(writer, useColor, BoxCategory.Structural, "minf", "Media Info",        "links the sample table to the track's media type");
        WriteLegendBoxRow(writer, useColor, BoxCategory.Structural, "udta", "User Data",         "optional container for custom or vendor metadata");
        WriteLegendBoxRow(writer, useColor, BoxCategory.Structural, "meta", "Metadata",          "iTunes-style metadata header (also a FullBox)");
        WriteLegendBoxRow(writer, useColor, BoxCategory.Structural, "ilst", "Item List",         "holds all the editable iTunes tag fields");
        WriteLegendBoxRow(writer, useColor, BoxCategory.Structural, "edts", "Edit List Cont.",   "contains the edit list for a track");
        WriteLegendBoxRow(writer, useColor, BoxCategory.Structural, "dinf", "Data Info",         "describes where the media data is located");

        writer.WriteLine();

        // Header/info boxes
        WriteLegendBoxRow(writer, useColor, BoxCategory.Header, "ftyp", "File Type",        "MP4 brand/variant: isom, mp42, M4V, etc.");
        WriteLegendBoxRow(writer, useColor, BoxCategory.Header, "mvhd", "Movie Header",     "total duration, creation date, and playback speed");
        WriteLegendBoxRow(writer, useColor, BoxCategory.Header, "tkhd", "Track Header",     "track ID, flags, duration, and pixel dimensions (video)");
        WriteLegendBoxRow(writer, useColor, BoxCategory.Header, "mdhd", "Media Header",     "per-track timescale, language code, and duration");
        WriteLegendBoxRow(writer, useColor, BoxCategory.Header, "hdlr", "Handler Ref",      "declares media type: Video / Sound / Timecode / Text");
        WriteLegendBoxRow(writer, useColor, BoxCategory.Header, "vmhd", "Video Media Hdr",  "marks track as video; holds compositing mode");
        WriteLegendBoxRow(writer, useColor, BoxCategory.Header, "smhd", "Sound Media Hdr",  "marks track as audio; holds balance value");
        WriteLegendBoxRow(writer, useColor, BoxCategory.Header, "name", "Name",             "track or handler name string, user-editable label");
        WriteLegendBoxRow(writer, useColor, BoxCategory.Header, "elst", "Edit List",        "maps presentation timeline to media timeline");

        writer.WriteLine();

        // Technical / codec
        WriteLegendBoxRow(writer, useColor, BoxCategory.Technical, "stbl", "Sample Table",   "master index mapping playback time to file offsets");
        WriteLegendBoxRow(writer, useColor, BoxCategory.Technical, "stsd", "Sample Desc",    "codec parameters: avc1=H.264, hvc1=H.265, mp4a=AAC");
        WriteLegendBoxRow(writer, useColor, BoxCategory.Technical, "stts", "Time-to-Sample", "duration of each sample in decoding order");
        WriteLegendBoxRow(writer, useColor, BoxCategory.Technical, "stss", "Sync Sample",    "table of keyframes (I-frames); audio tracks omit this");
        WriteLegendBoxRow(writer, useColor, BoxCategory.Technical, "stsz", "Sample Size",    "byte size of every individual media sample");
        WriteLegendBoxRow(writer, useColor, BoxCategory.Technical, "stsc", "Sample-to-Chunk","groups samples into storage chunks for efficiency");
        WriteLegendBoxRow(writer, useColor, BoxCategory.Technical, "stco", "Chunk Offset",   "file offset of each chunk (32-bit; use co64 for large files)");
        WriteLegendBoxRow(writer, useColor, BoxCategory.Technical, "co64", "Chunk Offset 64","file offset of each chunk (64-bit version)");
        WriteLegendBoxRow(writer, useColor, BoxCategory.Technical, "dref", "Data Reference", "URL or URN pointing to the media data (usually internal)");

        writer.WriteLine();

        // Media
        WriteLegendBoxRow(writer, useColor, BoxCategory.Media, "mdat", "Media Data",
            "raw encoded audio and video samples, not expanded by this tool");

        writer.WriteLine();
        SetColor(ConsoleColor.DarkGray, useColor);
        writer.WriteLine(Rule);
        SetColor(ConsoleColor.White, useColor);
        writer.WriteLine(" EDITABLE METADATA FIELDS  (add/update/delete with clipmetaedit, coming soon)");
        SetColor(ConsoleColor.DarkGray, useColor);
        writer.WriteLine(Rule);
        ResetColor(useColor);
        writer.WriteLine();

        WriteEditableFieldTable(writer, useColor);

        writer.WriteLine();
        SetColor(ConsoleColor.DarkGray, useColor);
        writer.WriteLine(Rule);
        ResetColor(useColor);
    }

    private static void WriteLegendColorSwatch(
        TextWriter writer, bool useColor, ConsoleColor color, string label, string description)
    {
        writer.Write("   ");
        SetColor(color, useColor);
        writer.Write($"■ {label}");
        ResetColor(useColor);
        writer.WriteLine($"  {description}");
    }

    private static void WriteLegendBoxRow(
        TextWriter writer, bool useColor, BoxCategory category, string fourCC, string name, string description)
    {
        ConsoleColor color = category switch
        {
            BoxCategory.Structural => ConsoleColor.Cyan,
            BoxCategory.Technical  => ConsoleColor.DarkGray,
            BoxCategory.Header     => ConsoleColor.Green,
            BoxCategory.Media      => ConsoleColor.DarkCyan,
            _                      => ConsoleColor.Gray,
        };

        writer.Write("   ");
        SetColor(color, useColor);
        writer.Write($"{fourCC,-6}  {name,-18}");
        ResetColor(useColor);
        writer.WriteLine($"  {description}");
    }

    private static void WriteEditableFieldTable(TextWriter writer, bool useColor)
    {
        (string Key, string Name)[] fields =
        [
            ("©nam", "Title"),
            ("©ART", "Artist"),
            ("©alb", "Album"),
            ("©day", "Year"),
            ("©cmt", "Comment"),
            ("©gen", "Genre"),
            ("desc", "Description"),
            ("covr", "Cover Art"),
            ("trkn", "Track Number"),
            ("disk", "Disc Number"),
            ("©lyr", "Lyrics"),
            ("©too", "Encoder Tool"),
            ("©wrt", "Composer"),
            ("aART", "Album Artist"),
            ("tmpo", "BPM"),
            ("cpil", "Compilation"),
            ("name", "Track Name"),
        ];

        int cols = 3;
        int rows = (int)Math.Ceiling(fields.Length / (double)cols);
        for (int row = 0; row < rows; row++)
        {
            writer.Write("   ");
            for (int col = 0; col < cols; col++)
            {
                int idx = col * rows + row;
                if (idx >= fields.Length) break;
                SetColor(ConsoleColor.Yellow, useColor);
                writer.Write($"{fields[idx].Key,-6}");
                ResetColor(useColor);
                writer.Write($" {fields[idx].Name,-14}  ");
            }
            writer.WriteLine();
        }
    }

    // ─── Metadata Summary ───────────────────────────────────────────────────

    /// <summary>
    /// Writes a flat, color-coded summary of all metadata values found in the tree.
    /// Grouped into iTunes Metadata, Windows Media Metadata, and Technical/Other.
    /// Mirrors the color scheme used in the tree output.
    /// </summary>
    public static void RenderSummary(BoxNode root, TextWriter? writer = null)
    {
        writer ??= Console.Out;
        bool useColor = IsConsole(writer);

        var itunesNodes       = new List<BoxNode>();
        var windowsMediaNodes = new List<BoxNode>();
        var otherNodes        = new List<BoxNode>();

        CollectMetadataNodes(root, itunesNodes, windowsMediaNodes, otherNodes);

        if (itunesNodes.Count == 0 && windowsMediaNodes.Count == 0 && otherNodes.Count == 0)
            return;

        const string Rule = "══════════════════════════════════════════════════════════════════";

        writer.WriteLine();
        SetColor(ConsoleColor.DarkGray, useColor);
        writer.WriteLine(Rule);
        SetColor(ConsoleColor.White, useColor);
        writer.WriteLine(" METADATA SUMMARY");
        SetColor(ConsoleColor.DarkGray, useColor);
        writer.WriteLine(Rule);
        ResetColor(useColor);
        writer.WriteLine();

        if (itunesNodes.Count > 0)
        {
            SetColor(ConsoleColor.Yellow, useColor);
            writer.WriteLine(" iTunes Metadata");
            ResetColor(useColor);
            foreach (var node in itunesNodes)
                WriteSummaryLine(writer, useColor, node, ConsoleColor.Yellow);
            writer.WriteLine();
        }

        if (windowsMediaNodes.Count > 0)
        {
            SetColor(ConsoleColor.DarkYellow, useColor);
            writer.WriteLine(" Windows Media Metadata");
            ResetColor(useColor);
            foreach (var node in windowsMediaNodes)
                WriteSummaryLine(writer, useColor, node, ConsoleColor.DarkYellow);
            writer.WriteLine();
        }

        if (otherNodes.Count > 0)
        {
            SetColor(ConsoleColor.Green, useColor);
            writer.WriteLine(" Technical / Other");
            ResetColor(useColor);
            foreach (var node in otherNodes)
                WriteSummaryLine(writer, useColor, node, ConsoleColor.Green);
            writer.WriteLine();
        }

        SetColor(ConsoleColor.DarkGray, useColor);
        writer.WriteLine(Rule);
        ResetColor(useColor);
    }

    private static void WriteSummaryLine(TextWriter writer, bool useColor, BoxNode node, ConsoleColor labelColor)
    {
        string label = MetadataKeys.GetName(node.Type);
        string value = node.DisplayValue ?? string.Empty;
        if (value.Length > MaxValueWidth)
            value = value[..MaxValueWidth] + "…";

        writer.Write("   ");
        SetColor(labelColor, useColor);
        writer.Write($"{label,-20}");
        ResetColor(useColor);
        writer.Write($"  {value}");

        if (node.IsEditable)
        {
            writer.Write("  ");
            SetColor(ConsoleColor.Magenta, useColor);
            writer.Write("← [EDITABLE]");
            ResetColor(useColor);
        }

        writer.WriteLine();
    }

    private static void CollectMetadataNodes(
        BoxNode node,
        List<BoxNode> itunes,
        List<BoxNode> windowsMedia,
        List<BoxNode> other)
    {
        foreach (var child in node.Children)
        {
            if (child.DisplayValue != null)
            {
                var cat = MetadataKeys.GetCategory(child.Type);
                if (cat == BoxCategory.WindowsMedia || child.Type.StartsWith("WM/", StringComparison.Ordinal))
                    windowsMedia.Add(child);
                else if (child.IsEditable || child.Type.StartsWith("©", StringComparison.Ordinal)
                    || cat == BoxCategory.EditableMeta)
                    itunes.Add(child);
                else if (cat is BoxCategory.Header or BoxCategory.Technical)
                    other.Add(child);
            }

            // Always recurse, metadata can be deeply nested.
            CollectMetadataNodes(child, itunes, windowsMedia, other);
        }
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private static bool IsConsole(TextWriter w) => ReferenceEquals(w, Console.Out);

    private static void SetColor(ConsoleColor color, bool useColor)
    {
        if (useColor) Console.ForegroundColor = color;
    }

    private static void ResetColor(bool useColor)
    {
        if (useColor) Console.ResetColor();
    }

    private static string GetFileSizeDisplay(string filePath)
    {
        try { return FormatFileSize((ulong)new FileInfo(filePath).Length); }
        catch { return "unknown size"; }
    }

    private static string FormatFileSize(ulong bytes)
    {
        const ulong MB = 1024 * 1024;
        const ulong KB = 1024;
        if (bytes >= MB) return $"{(double)bytes / MB:F1} MB";
        if (bytes >= KB) return $"{(double)bytes / KB:F1} KB";
        return $"{bytes} bytes";
    }
}
