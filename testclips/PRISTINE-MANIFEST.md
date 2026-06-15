# Pristine test-corpus manifest

The clips themselves live in `testclips/pristine/` and are **git-ignored** (binary, large,
some effectively irreplaceable). This manifest **is** checked in — it's the authoritative record
of what each clip is and which code path it earns its place by covering. Integration tests
enumerate `testclips/pristine/*.mp4` automatically (`TestClipsLocator`); CI runs clip-less and
graceful-skips.

**The corpus is curated, not a dumping ground.** Each clip should add a *structurally distinct*
shape (layout, brand, container quirk, 32- vs 64-bit tables, foreign metadata format). Keep clips
**small** — write-engine correctness is structural, not size-driven (see design spec
`docs/superpowers/specs/2026-06-15-pristine-test-corpus-baseline-design.md`). Redundant same-shape
clips just slow the suite.

## Current corpus (10 clips, as of 2026-06-15)

| Clip | Size | Brand | Top-level layout | Offset table | Provenance / source | Uniquely covers |
|------|------|-------|------------------|--------------|---------------------|-----------------|
| `Stargaze.mp4` | 3.6 MB | isom | `ftyp,moov,free,free,mdat` | stco | unknown editor/recorder | **moov-first** + `free` padding (moov-growth cushion); smallest clip → full-lifecycle test |
| `VIDEO0011_01.mp4` | 34 MB | mp42 | `ftyp,moov,mdat` | stco | phone camera (`VIDEO####`) | **moov-first, no free cushion** → forces stco patching with mdat shift |
| `Scott.mp4` | 31 MB | isom | `ftyp,mdat,moov` | stco | unknown (2014) | isom brand, mdat-first |
| `Ben.mp4` | 36 MB | isom | `ftyp,mdat,moov` | stco | unknown (2014, sibling of Scott) | isom brand, mdat-first |
| `2022-02-01 21.50.02.mp4` | 46 MB | mp42 | `ftyp,mdat,moov` | **co64 + largesize** | phone (has GPS `©xyz`) | **64-bit tables on a small file**; **two `meta` boxes** — a moov-level `mdta`/`keys` foreign metadata container preserved across writes (see PITFALLS 2026-06-15) |
| `tf2testclip1.mp4` | 69 MB | mp42 | `ftyp,mdat,moov` | **co64 + largesize** | NVIDIA-style DVR | has an `Xtra` box (`WM/Category = "Snipe Tag"`) → Xtra-box read tests |
| `Untitled video - Made with Clipchamp.mp4` | 105 MB | isom | `ftyp,uuid,free,mdat,moov` | stco | MS Clipchamp editor | top-level **`uuid`** box (editor signature) must be preserved |
| `tf2testclip2.mp4` | 217 MB | mp42 | `ftyp,mdat,moov` | **co64 + largesize** | NVIDIA-style DVR | existing larger fixture |
| `Team Fortress 2 2026.01.08 - 21.25.12.55.DVR.mp4` | 198 MB | mp42 | `ftyp,mdat,moov` | **co64 + largesize** | NVIDIA ShadowPlay DVR (original filename) | one real DVR with untouched provenance filename |
| `ZC112.mp4` | 290 MB | mp42 | `ftyp,free,mdat,moov,free` | stco | unknown | `free` boxes both sides of mdat; last chunk flush against mdat-end (PITFALLS 2026-06-12 scanner clamp) |

### Coverage achieved
- **Layouts:** mdat-first, moov-first (with and without `free` cushion), `uuid`-prefixed, `free`-padded both sides.
- **Brands:** `mp42`, `isom`.
- **Offset tables / box sizes:** `stco`; `co64` + `largesize` (64-bit) — emitted even on the 46 MB `2022-02-01` clip.
- **Foreign metadata formats preserved across writes:** Apple `mdta`/`keys` (moov-level `meta`), Windows-Media `Xtra`, `udta/©xyz` GPS.

### 64-bit paths → covered synthetically (not by a stored clip)
No real clip is **moov-first AND 64-bit**, so the two 64-bit code paths are exercised by synthetic
fixtures in `MinimalMp4Builder` / `Mp4WriteIntegrityTests` (not a giant >4 GB clip), which also
gives clip-less CI coverage:
- **64-bit offset *table* (`co64`)** — `BuildMoovFirstCo64WithPatternedMdat`, tested by
  `MoovFirstCo64_CreateScenario_Grow_…` and `MoovFirstCo64_UpdateScenario_GrowAndShrink_…`.
- **64-bit box *header* (`largesize` mdat)** — `BuildMoovFirstLargesizeMdatWithPatternedMdat`,
  tested by `MoovFirstLargesizeMdat_Grow_…`.

### Manifest ↔ folder drift guard
`PristineCorpusManifestTests.Manifest_ListsExactlyTheClipsOnDisk` fails if a clip in
`pristine/` has no row here, or a row here names a file not on disk — so this table can't drift
from the folder. (Graceful-skips clip-less.)

### Excluded (moved, not deleted)
Two redundant ~215 MB NVIDIA DVR clips (same `mp42 [ftyp,mdat,moov]` co64 shape `tf2testclip1/2`
already cover) were moved to `../_clipmeta-excluded-clips/` to keep the suite fast. Restore from
there if ever needed.

## Adding a clip to the baseline

1. Drop the `.mp4` into `testclips/pristine/`.
2. Run `dotnet test clipmetascribe.Tests` — the new clip rides every `[DynamicData]` integration
   test automatically (media-byte-identity, foreign-atom preservation, dry-run, temp-cleanup).
3. If it **fails**: investigate root cause (writer bug → fix + PITFALLS; intentionally
   unsupported shape → document as a refusal fixture, don't blanket-skip).
4. Add a row here describing its source, structure, and what it *uniquely* covers. If it only
   duplicates an existing shape, prefer not to keep it (suite time) — or swap it for a smaller
   example of the same shape.
5. Record any genuinely new structural quirk in `docs/PITFALLS.md`.
