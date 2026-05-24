# clipmetaview

Part of the **peckworks-clipmeta** suite.

## What it does

Displays the internal box/atom structure of an MP4 file as a human-readable tree.
Editable metadata fields are clearly marked so you know exactly what `clipmetaedit` (coming soon) can change.

## Usage

```
clipmetaview "path/to/video.mp4"
```

## Output

```
tf2testclip1.mp4  (69.5 MB)
├── ftyp  File Type  [24 bytes @ 0x0]
├── mdat  Media Data  [72,873,660 bytes @ 0x18]  (raw media, not expanded)
└── moov  Movie  [15,209 bytes @ 0x457F6D4]
    ├── mvhd  Movie Header  [108 bytes @ 0x457F6DC]
    ├── trak  Track  [5,748 bytes @ 0x457F748]
    │   ├── tkhd  Track Header  [92 bytes @ 0x457F750]
    │   └── mdia  Media  [5,648 bytes @ 0x457F7AC]
    │       ├── mdhd  Media Header  [32 bytes @ 0x457F7B4]
    │       ├── hdlr  Handler  [44 bytes @ 0x457F7D4]
    │       └── minf  Media Info  [5,564 bytes @ 0x457F800]
    │           ├── vmhd  Video Media Header  [20 bytes @ 0x457F808]
    │           ├── dinf  Data Info  [36 bytes @ 0x457F81C]
    │           │   └── dref  Data Reference  [28 bytes @ 0x457F824]
    │           └── stbl  Sample Table  [5,500 bytes @ 0x457F840]
    │               └── ...
    └── udta  User Data  [...]
        └── meta  Metadata  [...]
            ├── hdlr  Handler  [...]
            └── ilst  Metadata Items  [...]
                ├── ©nam  Title  "My Video"  ← [EDITABLE]
                ├── ©ART  Artist  "Scott Finley"  ← [EDITABLE]
                ├── ©alb  Album  "Clips 2024"  ← [EDITABLE]
                └── covr  Cover Art  "[JPEG image, 14532 bytes]"  ← [EDITABLE]

Legend:
  ← [EDITABLE]  This field can be added, updated, or deleted with clipmetaedit (coming soon)
```

## Exit Codes

| Code | Meaning |
|------|---------|
| 0    | Success |
| 1    | Invalid arguments or file not found |
| 2    | File parse error |

## Technical Notes

- Pure native C# — zero external dependencies beyond the .NET 10 BCL
- Reads only box headers; never loads media data (`mdat`) into memory
- Big-endian byte order handled throughout via `BigEndianReader`
- All file I/O through `FileStream` with seeking — constant memory use regardless of file size
- Designed as the foundation for `clipmetaedit` (add/update/delete metadata)
- `BoxNode.FileOffset` and `BoxNode.Size` are accurate so the editor can seek directly to any box

## MP4 Metadata You Can Edit (coming in clipmetaedit)

| Key    | Friendly Name | Type    |
|--------|--------------|---------|
| `©nam` | Title        | Text    |
| `©ART` | Artist       | Text    |
| `©alb` | Album        | Text    |
| `©day` | Year         | Text    |
| `©cmt` | Comment      | Text    |
| `©gen` | Genre        | Text    |
| `desc` | Description  | Text    |
| `trkn` | Track Number | Integer |
| `covr` | Cover Art    | Binary  |

## Project Structure

```
clipmetaview/
├── Program.cs              Entry point (delegates to AppRunner)
├── AppRunner.cs            Testable app logic, returns exit codes
├── Mp4/
│   ├── BigEndianReader.cs  All big-endian binary reads
│   ├── BoxHeader.cs        BoxHeader record struct
│   ├── BoxNode.cs          In-memory tree node
│   ├── FullBoxHeader.cs    FullBoxHeader record struct
│   ├── MetadataKeys.cs     FourCC → friendly name dictionary
│   └── Mp4Parser.cs        Core parser (ParseFile entry point)
└── Rendering/
    └── TreeRenderer.cs     Console tree output with color support
```
