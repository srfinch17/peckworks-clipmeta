# peckworks-clipmeta

A suite of C# command-line tools for reading and writing metadata in MP4 files. Zero external dependencies — pure .NET BCL only.

## Tools

### clipmetaview
Displays the internal box/atom structure of an MP4 file as a human-readable tree. Editable metadata fields are marked.

```
clipmetaview "video.mp4"
```

### clipmetascribe
Reads and writes MP4 metadata. Supports custom fields in addition to standard ones (game, players, tags, timecode, rating, notes).

```
clipmetascribe "video.mp4" --list
clipmetascribe "video.mp4" --set game "Team Fortress 2"
clipmetascribe "video.mp4" --append tags "competitive|payload"
clipmetascribe "dir/" --find game "TF2"
clipmetascribe "dir/" --index
clipmetascribe "dir/" --index-search tags "competitive"
```

## Structure

| Project | Purpose |
|---------|---------|
| `clipmeta.core` | Shared library: MP4 parser, reader, writer |
| `clipmetaview` | Tree viewer CLI |
| `clipmetascribe` | Read/write CLI |
| `clipmetaview.Tests` | Tests for clipmetaview (MSTest) |
| `clipmetascribe.Tests` | Tests for clipmetascribe (MSTest, 206 tests) |

## Requirements

- .NET 10 SDK
- Real `.mp4` files in `testclips/pristine/` for integration tests (not included in repo)
