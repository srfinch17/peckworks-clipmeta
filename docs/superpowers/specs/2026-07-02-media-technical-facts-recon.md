# Reconnaissance spec: media-technical facts (duration, codec, resolution, etc.)

Date: 2026-07-02
Status: **RECONNAISSANCE ONLY, NOT APPROVED, NOT SCHEDULED.** This is a forward-looking
pre-spec produced while the parser was already open for the `clip_get_boxtree` work
(`2026-07-02-boxtree-tool-and-cli-json-design.md`). It captures the expensive-to-rediscover
facts (where each datum lives, what is already decoded, the real difficulty) so that when the
owner greenlights this feature it starts from a short confirmation brainstorm, not a fresh
exploration. **It deliberately does NOT settle product decisions.** Run a proper
`superpowers:brainstorming` pass before building.

## Why this exists

During the boxtree design I told the owner media-technical facts were "hard, a different
project requiring new decoding of mvhd/stsd/etc." **Reconnaissance corrected that:** the
parser already decodes almost all of them into human display strings; the real work is
re-exposing already-computed values as structured typed fields and correlating them per
track, plus two genuinely-new facts (bitrate, codec profile/level). This is much cheaper than
implied, which is exactly why capturing the map now is worthwhile.

## Goal (subject to the future brainstorm)

Expose per-file and per-track technical facts as structured data for the same consumers the
boxtree serves (Claude via MCP, scripts/web via the CLI): duration, timescale, track list
with type, codec, resolution, frame rate, audio channels/sample rate, creation date, and
(new) bitrate. Likely surfaces: a new read-only MCP tool (working name `clip_get_mediainfo`)
plus a `clipmetaview`/`clipmetascribe` flag, OR structured fields layered onto the existing
`clip_get_metadata`. **Which surface is a product decision for the brainstorm.**

## Fact inventory (verified against `clipmeta.core/Mp4/Mp4Parser.cs`, 2026-07-02)

### Tier 1, already decoded, work is structured re-exposure + per-track correlation
| Fact | Source box | Status in code |
|------|-----------|----------------|
| File brand/variant | `ftyp` | decoded to `DisplayValue` (`Mp4Parser.cs:368-374`) |
| Movie duration + timescale | `mvhd` | decoded (`:460-488`, version 0 and 1) |
| Creation / modification date | `mvhd`/`tkhd`/`mdhd` | `FormatMacTimestamp` (`:634-643`) |
| Per-track media duration + timescale + language | `mdhd` | decoded; `RawTimescale` stored on the node (`:492-516`) |
| Track type (Video/Sound/Timecode/Text) | `hdlr` | decoded (`:425-456`) |
| Video resolution (display) | `tkhd` | width/height from 16.16 fixed (`:520-541`) |
| Video resolution (coded) | video sample entry in `stsd` | width/height at +24/+26 (`:578-593`) |
| Audio channels + sample rate | `mp4a` sample entry | decoded (`:596-621`) |
| Codec identity | `stsd` child sample-entry **box type** | already present as `node.Type` (`avc1`, `hvc1`, `mp4a`, ...); just needs a FourCC->friendly-name map (some already in `MetadataKeys`) |
| Frame rate (fps) | `mdhd` timescale + `stts` delta | already COMPUTED post-parse: `EnrichFrameRate`, `fps = RawTimescale / RawSampleDelta` (`:655-677`) |

The main real work in Tier 1 is **correlation**: walk each `trak` -> `mdia` -> (`mdhd`,
`hdlr`) -> `minf` -> `stbl` -> `stsd` -> sample entry, and assemble a per-track record. The
raw values already exist; today they live as separate `DisplayValue` strings on scattered
nodes rather than as a structured per-track object. Note several values are stored as
formatted strings (e.g. `"1920x1080"`, `"2 ch, 48.0 kHz"`); a structured feature likely wants
the raw numbers, which may mean lifting the raw decode into typed fields on `BoxNode` (like
the existing internal `RawTimescale`/`RawSampleDelta`) rather than re-parsing the string.

### Tier 2, genuinely new decoding
| Fact | Source | Why it is new |
|------|--------|---------------|
| Bitrate (per track / overall) | `stsz` sample sizes summed / duration, or a `btrt`/`esds` box | not decoded today; `stsz` is walked for chunk-offset work but sizes are not summed for bitrate |
| Codec profile/level (e.g. H.264 High@4.1) | `avcC`/`hvcC` config box inside the sample entry | requires parsing codec config records; a real rabbit hole with a natural stopping point (FourCC-level codec identity is Tier 1 and cheap; profile/level is deep and optional) |

## Difficulty summary
- Tier 1 is mostly assembly of already-decoded values + a per-track walk + a codec-name map.
  Estimate: modest. The one design cost is deciding whether to add typed raw fields to
  `BoxNode` vs re-parsing display strings (prefer typed fields).
- Tier 2 is optional and should probably ship later or never: bitrate is a small addition;
  codec profile/level is deep and low-ROI for this project's use case (gameplay clips).

## Open product questions (for the future brainstorm, NOT decided here)
1. Surface: a dedicated `clip_get_mediainfo` tool + CLI flag, or structured fields on an
   existing tool? (The boxtree precedent argues for a dedicated read-only tool + `--json`.)
2. Shape: a per-track array plus a file-level summary? Which fields per track?
3. Codec depth: FourCC-level identity only (cheap), or profile/level (Tier 2)?
4. Include bitrate in v1 of this feature, or defer?
5. Raw-value exposure: add typed fields to `BoxNode` (clean) vs parse the existing display
   strings (hacky). Recommend typed fields.
6. Does this reuse the boxtree DTO/serialization contract (shared `JsonSerializerOptions`,
   camelCase, string enums)? Almost certainly yes, for consistency.

## What this recon deliberately does NOT do
- No DTO, no tool contract, no test plan, no commitment to Tier 2. Those are the brainstorm's
  job. Guessing them now would bake in unexamined assumptions, the opposite of the point.

## Pointer / discoverability
This spec is referenced from `CLAUDE.md` (Future work) and from the boxtree spec, and a
project memory entry indexes it, so a future session surfaces it without the owner having to
remember it exists.
