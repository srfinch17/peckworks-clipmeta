# Pristine Test-Corpus Baseline — Design Spec

**Date:** 2026-06-15
**Status:** Approved (brainstorming) → ready for implementation plan
**Author:** pairing session (srfinch17 + Claude)

---

## Problem

A pile of "random" `.mp4` clips from assorted sources was dropped into
`testclips/pristine/` with no record of what they are, whether they parse, or
whether the write engine can round-trip them byte-for-byte. The goal is a
**curated, documented baseline corpus** that exercises a deliberate spread of
real-world MP4 shapes — so that "all integration tests green" is a meaningful
statement about diverse inputs, not an accident of having only same-source
clips on hand.

This is a **test-infrastructure / workflow** change, not a product feature. No
production code in `clipmeta.core` or the CLIs changes as a result of curating
the corpus itself. (One small, optional production-adjacent item — a synthetic
`co64` fixture — is a *test* fixture, not shipping code.)

### Why size is (mostly) a red herring

The original instinct was "make sure it can edit a *huge* file." Investigation
showed write-engine correctness is **structural, not size-driven**: the writer
stream-copies `mdat` to a temp file and patches offset tables; that identical
code runs whether `mdat` is 3 MB or 700 MB. The **only** size-dependent path
triggers at **4 GB**, where 32-bit offsets/box-sizes no longer fit and the file
must use `co64` (64-bit chunk offsets) and `largesize` (64-bit box headers).
None of the clips — not even the 700 MB one originally present — reached 4 GB,
so storing giant clips never actually tested the thing that makes giant files
special.

**Crucial discovery (2026-06-15):** the NVIDIA-style DVR muxer emits
`co64` + `largesize` **unconditionally**, even on a 45 MB clip
(`2022-02-01 21.50.02.mp4`). So the 64-bit-table path is *already* exercised by
small/moderate real clips. The takeaway: **keep clips small.** Coverage comes
from structural variety, not byte count.

---

## Scope

### In scope
1. **Curate** the pristine corpus to a deliberate, mostly-small set spanning the
   structural shapes we care about (layouts, brands, container quirks, 32- vs
   64-bit tables).
2. **Verify** every retained clip survives a representative metadata write
   **byte-identical** in the media (or is explicitly documented as a known
   refusal fixture).
3. **Document** the corpus: a checked-in manifest recording each clip's
   provenance, structure, and what code path it uniquely covers.
4. **Add a synthetic `moov-first + co64` fixture** to cover the one path no real
   clip hits (64-bit offset *patching*) and to give CI — which runs clip-less —
   coverage of the 64-bit path.
5. **Update workflow docs** (CLAUDE.md, PITFALLS.md as warranted) so adding a
   clip to the baseline is a known, repeatable step.

### Out of scope
- **`.mov` / QuickTime support.** The lone `.mov` was withdrawn by the user. Any
  future QuickTime write support is a separate feature with its own spec.
- **`.mp4` write support for >4 GB files via stored giant clips.** Covered
  synthetically instead (a `co64`/`largesize` fixture with tiny payload).
- **Two-tier "fast core + opt-in full" test split.** Considered and *deferred*
  (YAGNI). With a trimmed, modest corpus we run all clips every build. Revisit
  only if suite wall-time becomes a real problem (see Risks).
- Any change to the metadata model, schema, or CLI surface.

---

## The curated corpus

Two redundant ~215 MB DVR clips (same `mp42 [ftyp,mdat,moov]` shape that
`tf2testclip1/2` already cover) were moved out of the repo to
`../_clipmeta-excluded-clips/` (not deleted — clips are effectively
irreplaceable). The retained 10 clips:

| Clip | Size | Brand | Layout | Table | Uniquely covers |
|------|------|-------|--------|-------|-----------------|
| `Stargaze.mp4` | 3.6 MB | isom | `ftyp,moov,free,free,mdat` | stco | **moov-first** + free padding; smallest, used for full-lifecycle test |
| `VIDEO0011_01.mp4` | 34 MB | mp42 | `ftyp,moov,mdat` | stco | **moov-first**, no free boxes (2nd independent moov-first) |
| `Scott.mp4` | 31 MB | isom | `ftyp,mdat,moov` | stco | isom brand, mdat-first |
| `Ben.mp4` | 36 MB | isom | `ftyp,mdat,moov` | stco | isom brand, mdat-first (sibling source to Scott) |
| `2022-02-01 21.50.02.mp4` | 46 MB | mp42 | `ftyp,mdat,moov` | **co64+largesize** | 64-bit tables on a *small* file |
| `tf2testclip1.mp4` | 69 MB | mp42 | `ftyp,mdat,moov` | **co64+largesize** | existing fixture; Xtra-box read tests |
| `Untitled…Clipchamp.mp4` | 105 MB | isom | `ftyp,uuid,free,mdat,moov` | stco | top-level **`uuid`** (editor signature) box |
| `tf2testclip2.mp4` | 217 MB | mp42 | `ftyp,mdat,moov` | **co64+largesize** | existing fixture |
| `Team Fortress 2 …21.25…DVR.mp4` | 198 MB | mp42 | `ftyp,mdat,moov` | **co64+largesize** | one real NVIDIA DVR w/ original filename (provenance) |
| `ZC112.mp4` | 290 MB | mp42 | `ftyp,free,mdat,moov,free` | stco | free boxes both sides of mdat; PITFALLS fixture (chunk flush at mdat-end) |

Coverage matrix achieved: **layouts** {mdat-first, moov-first, moov-first+free};
**brands** {mp42, isom}; **container quirks** {top-level uuid, free padding
leading/trailing}; **offset tables** {stco, co64}; **box headers** {32-bit,
largesize}.

### The one gap → synthetic fixture
No real clip is **moov-first AND co64**, so the 64-bit offset *patching* path
(rewrite a `co64` table when `mdat` shifts) is untested by real clips, and CI
has no clips at all. Add `MinimalMp4Builder.BuildMoovFirstCo64WithPatternedMdat`
(a moov-first fixture whose `stbl` uses `co64`/`largesize` over a tiny patterned
`mdat`) and an integrity test asserting every 64-bit offset still points at the
same marker bytes after a write that grows/shrinks the moov.

---

## Architecture / how it fits the existing harness

No new test infrastructure is introduced; the existing machinery already does
the right thing and just needs the corpus and one fixture:

- **Enumeration** — `TestClipsLocator.AllPristine()` / `PristineClipRows()`
  already glob `testclips/pristine/*.mp4`, name-sorted, with graceful-skip on
  clip-less machines. Retained clips are picked up automatically; no code change.
- **Per-clip integrity** — `RealClip_MultiFieldWrite_MediaByteIdentical`
  (`[DynamicData]`, one row per clip) already SHA-256s every `mdat` payload and
  byte-compares at every chunk offset via `MediaIntegrityScanner`. New clips ride
  this for free.
- **Synthetic fixtures** — live in `MinimalMp4Builder`; the new `co64` builder
  and its test sit beside the existing moov-first integrity tests in
  `Mp4WriteIntegrityTests`.

### New artifacts
1. `testclips/PRISTINE-MANIFEST.md` — **checked in** (only `testclips/pristine/`
   and `testclips/scratch/*` are git-ignored; the `testclips/` root and a
   manifest in it are tracked). One row per clip: filename, source/provenance,
   size, brand, layout, table, what it uniquely covers. Mirrors the table above
   and is the authoritative "what is this clip" record.
2. `MinimalMp4Builder.BuildMoovFirstCo64WithPatternedMdat(...)` + a
   `MoovFirstCo64_…_AllChunkOffsetsPointAtSameData` test.
3. CLAUDE.md note under "Build & test": how to add a clip to the baseline
   (drop in `pristine/`, add a manifest row, run the suite, record any new
   structural quirk).

---

## Data flow (verify pass)

```
for each clip in testclips/pristine/*.mp4:
    scratch = copy(clip)                     # ScratchClips.Prepare
    write(scratch, {game, tags, rating})     # Mp4Writer
    AssertMediaUnchanged(clip, scratch)      # SHA-256 mdat + offset byte-compare
        └─ pass  → clip is a valid pristine baseline
        └─ throw → either a writer bug (fix) OR an intentional refusal fixture
                   (document in manifest + a dedicated refusal test, remove from
                    the byte-identical DynamicData set)
```

The verify pass is just *running the existing suite* against the curated corpus.
A green run is the definition of "the corpus is in order."

---

## Error handling / edge cases

- **A clip the writer legitimately can't round-trip.** Today none are known
  (all 10 parse; the 4 previously-present fixtures pass). If one fails byte-
  identity, the decision tree is: (a) genuine writer bug → fix + PITFALLS entry;
  (b) inherently un-rewritable shape we choose not to support → document as a
  refusal fixture and assert the writer *refuses cleanly* (original untouched,
  temp cleaned) rather than asserting byte-identity.
- **Clip-less machines / CI.** Unchanged: `Assert.Inconclusive` graceful-skip
  covers every real-clip test. The synthetic `co64` fixture ensures the 64-bit
  path still runs where there are no clips.
- **Manifest drift.** The manifest is documentation, not enforced by a test in
  this iteration (YAGNI). The CLAUDE.md workflow note makes updating it part of
  adding a clip. A future enhancement could assert manifest ⊇ pristine filenames.

---

## Verify-pass findings (2026-06-15)

Running the existing suite against the curated corpus surfaced exactly one issue — and it was a
**test-harness bug, not a writer bug** (the kind the diverse corpus exists to catch):

- `2022-02-01 21.50.02.mp4` carries a **moov-level `meta`** in the Apple/QuickTime
  **`mdta`/`keys`** format (a sibling of `udta`, holding 2 key-indexed foreign atoms), plus
  `udta/©xyz` GPS. On write, the writer correctly leaves that foreign container **byte-for-byte
  untouched** and creates its own iTunes `udta→meta→ilst` — the file ends with **two `ilst`
  boxes**. Media integrity (mdat + every chunk offset) passed.
- `Write_ForeignAtoms_Preserved` used `FindNode(root, "ilst")` (the *first* `ilst`) and so
  compared the writer's new foreign-free `ilst` against the original — a false "atoms lost".
- **Fix:** count foreign atoms across **all** `ilst` boxes (`FindAllNodes`). Test-only; no
  production change. Documented in `docs/PITFALLS.md` (2026-06-15).

Result: full solution green (clipmetaview 101/3-skip, clipmetamcp 103, clipmetascribe 285/0),
scribe suite ~53 s — confirming "all clips every run" is affordable (no tiering needed).

Also confirmed during the pass: the NVIDIA-style DVR muxer emits `co64`+`largesize`
unconditionally (even the 46 MB `2022-02-01`), so the 64-bit *read/round-trip* path is already
covered by real clips; the synthetic fixture targets specifically the moov-first `co64`
**offset-patching** path.

**Synthetic co64 fixture — delivered (item 4 / DoD #3).**
`MinimalMp4Builder.BuildMoovFirstCo64WithPatternedMdat` (co64 twin of the stco patterned-mdat
builder) plus two tests in `Mp4WriteIntegrityTests`:
`MoovFirstCo64_CreateScenario_Grow_…` and `MoovFirstCo64_UpdateScenario_GrowAndShrink_…`. Each
asserts the fixture's tables are actually `co64` (a teeth guard — verified by watching it RED
against an stco fixture) before asserting `AssertMediaUnchanged` across the offset-patching write.
Result: **the writer already patches 64-bit co64 tables correctly** on a moov-first file through
both grow and shrink — no production change needed; the path is now explicitly covered, including
on clip-less CI.

## Risks

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Suite wall-time grows past tolerance (corpus went from 4 → 10 real clips, ~1.4 GB) | Med | Med | Measure during verify pass. If painful, introduce the deferred fast-core/opt-in-full tier. Cost is bounded — disk-bound temp writes, not CPU. |
| A new clip silently fails byte-identity and masks a real writer bug as "expected" | Low | High | Never blanket-`Inconclusive` a failing real clip; every refusal must be explicit + documented per the decision tree above. |
| Manifest rots out of sync with the folder | Med | Low | CLAUDE.md workflow note; optional future drift-check test. |
| Synthetic `co64` fixture encodes the format wrong and tests a strawman | Low | Med | Cross-check the fixture parses identically to a real `co64` clip's `stbl` shape; reuse `BigEndianWriter` paths the production writer uses. |
| Excluded DVR clips later wanted, assumed deleted | Low | Low | Moved (not deleted) to `../_clipmeta-excluded-clips/`; noted here and to the user. |

---

## Definition of Done

1. `testclips/pristine/` contains exactly the 10 curated clips (table above);
   the 2 trimmed DVRs live in `../_clipmeta-excluded-clips/`.
2. `testclips/PRISTINE-MANIFEST.md` checked in, one accurate row per clip.
3. `MinimalMp4Builder.BuildMoovFirstCo64WithPatternedMdat` + integrity test added;
   the moov-first 64-bit offset-patching path is proven byte-identical.
4. `dotnet build` — 0 warnings / 0 errors.
5. `dotnet test` — all pass (existing + new co64 test), every pristine clip green
   through `RealClip_MultiFieldWrite_MediaByteIdentical`, on a clip-present
   machine; graceful-skip intact clip-less.
6. CLAUDE.md "Build & test" updated with the add-a-clip workflow; PITFALLS.md
   gets an entry if the verify pass surfaces anything non-obvious.
7. Zero NuGet packages added to production projects (unchanged).
