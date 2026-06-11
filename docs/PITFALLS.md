# PITFALLS — peckworks-clipmeta

A living log of mistakes, gotchas, and hard-won knowledge. **Append a new entry whenever we hit and fix a real bug or discover something non-obvious.** Consult this before touching the MP4 parser or writer.

Format: newest entries at the top of "Field-discovered." The "MP4 format hazards" section below is seeded from the write-engine design spec and is foundational reference.

---

## Field-discovered (append here as we go)

### 2026-06-10 — Parse and copy used separate file opens (fixed)
- **Symptom (potential):** The writer parsed the source, closed it, then re-opened it to copy
  bytes. A process writing to the file in between (Game Bar still recording the clip being
  tagged) would make the copied bytes disagree with the parsed chunk offsets — torn output.
- **Fix:** One `FileShare.Read` (deny-writers) handle held across parse + copy via the new
  `Mp4Parser.Parse(FileStream)` overload. A live recorder now causes a clean up-front refusal
  ("another program has it open for writing") instead. The handle must be released *before*
  `File.Replace` — ReplaceFile needs write/delete access the held handle would block.
- **Lesson:** Read-then-act on a file path is a TOCTOU race; hold one handle across both steps.

### 2026-06-10 — CLI swallowed flags as values; some errors were stack traces (fixed)
- **Symptoms:** `--set notes --backup` stored the literal text "--backup" as notes (while also
  enabling backup mode — flag detection scans the arg list independently). `--set tags` at the
  end of the line was silently ignored. `--set rating "five stars"` crashed with a raw .NET
  stack trace (`ArgumentException` from Normalizer was uncaught in Program). `--SET` (wrong
  case) was silently ignored while other flags matched case-insensitively. The fixed-name
  `clip.mp4.tmp` temp file would overwrite a real user file of that name. Appending to a
  non-text atom spliced its display placeholder ("[JPEG image, …]") into the file as data.
- **Fixes:** `BuildMutation` validates positional args (missing → error; an exact known-flag
  match where a value belongs → error; merely dashy values still accepted). `ArgumentException`
  / `InvalidOperationException` are caught → friendly message, exit 1. Temp files are
  `<name>.<guid>.tmp`. Appends verify the existing value is quoted text before merging.
- **Lesson:** Every "take the next arg" parse needs a missing/looks-like-a-flag check, and
  every exception type a CLI's core can throw needs a catch that turns it into an error line.

### 2026-06-10 — Lenient parser + trusting writer = silent mdat deletion (CRITICAL, fixed)
- **Symptom:** Writing metadata into a moov-first file that had one unparseable box between
  moov and mdat produced a file with **no mdat at all** — the entire video silently deleted,
  exit code 0, verification passed.
- **Cause:** `Mp4Parser.ParseBoxes` deliberately `break`s (not throws) at a box it can't read,
  so everything after the damage was missing from the tree. `Mp4Writer` emits only the boxes
  in the tree, and the old `VerifyWrite` only checked "moov exists + set fields read back" —
  all true even with the video gone.
- **Fix:** The parser stays lenient (a damaged file should still be *viewable*), but the writer
  is now strict: `VerifyParseAccountsForWholeFile` refuses any write where the parsed top-level
  boxes don't tile the file byte-for-byte, or where any box was size-clamped (truncated file).
  Post-write checks now also assert temp length == original + delta and mdat count unchanged.
- **Lesson:** A writer that rebuilds a file from a parse tree inherits every silent omission of
  the parser. Read-lenient / write-strict must be an explicit, tested boundary.

### 2026-06-10 — Delta-based offset patching had no cross-check (fixed preemptively)
- **Symptom (potential):** stco/co64 entries are shifted by a *predicted* moov-size delta
  computed independently of the bytes actually written. Any divergence (e.g. an exotic box
  layout the size calculation mis-accounts) would corrupt every chunk offset silently.
- **Fix:** `WriteMoov` now hard-fails if the rebuilt moov's actual size differs from the
  prediction, before anything reaches the original file.
- **Lesson:** When value A (offset delta) is derived from a prediction of value B (moov size),
  assert prediction == reality at the moment B becomes known. One `if` turns silent corruption
  into a safe abort.

### 2026-06-10 — `--clear-all` re-added the schema atom it had just removed (fixed)
- **Symptom:** After `--clear-all`, `--list` still showed `schema 1`.
- **Cause:** `WriteMetadata` stamped `schema=1` into `SetFields` unconditionally — including on
  clear-all and delete-only mutations.
- **Fix:** Stamp only when the mutation actually sets or appends values.
- **Lesson:** "Do X on every write" rules need a definition of *write*; removal-only operations
  usually shouldn't create data.

### 2026-06-10 — Tests verified metadata, never media (fixed)
- **Symptom:** All 286 tests passed while the mdat-deletion bug above existed. The test named
  for "stco/co64 adjustment" only asserted the file was still *parseable* — true even with
  every chunk offset corrupted. No test ever hashed mdat or checked a single offset value.
  (This file previously claimed a both-tracks stco integration test existed; it did not.)
- **Fix:** `Mp4WriteIntegrityTests` + `MediaIntegrityScanner` (an independent scanner sharing
  no code with clipmeta.core): SHA-256 of every mdat payload, plus a byte-compare at every
  stco/co64 entry (old offset in old file vs new offset in new file), on synthetic moov-first
  fixtures with two tracks and patterned chunks AND on every real pristine clip.
- **Lesson:** For a file *rewriter*, round-trip tests must assert the payload bytes, not just
  the structure. Also: all our real clips are mdat-first (Game Bar layout), so without a
  moov-first fixture the offset-adjustment code path was never exercised at all.

### 2026-06-09 — Build fails NU1100 on a machine with no NuGet source
- **Symptom:** `dotnet build`/`restore` fails: `NU1100: Unable to resolve 'MSTest'`.
- **Cause:** Not the project. `dotnet nuget list source` returned "No sources found" — the machine had no nuget.org source registered.
- **Fix:** `dotnet nuget add source https://api.nuget.org/v3/index.json -n nuget.org`, then restore.
- **Lesson:** When restore fails for a package on a fresh machine, check `dotnet nuget list source` before editing csproj files. Production projects have zero NuGet deps; only the test projects pull MSTest.

---

## MP4 format hazards (foundational — from the write-engine design spec)

These are the things that **corrupt files silently** if missed. Each should have a corresponding test. Full detail in `docs/superpowers/specs/2026-05-21-clipmeta-core-write-engine-design.md` §4 and §10.

| # | Risk | Mitigation |
|---|------|------------|
| 1 | Fragmented MP4 (`moof` boxes — common with Xbox Game Bar) | Detect on parse; refuse write with a clear `UnsupportedFormatException`. |
| 2 | Only one `stco`/`co64` adjusted, others missed | Walk **ALL** `trak → stbl → stco/co64`. A stereo clip has video + audio tables; missing one desyncs that track. `Mp4WriteIntegrityTests` proves both tables on a two-track moov-first fixture byte-for-byte. |
| 3 | `mean`/`name` FullBox 4-byte version+flags prefix omitted | Both are FullBoxes. Omitting the prefix shifts all following bytes → atom reads back as garbage. Unit test verifies byte structure. |
| 4 | `hdlr` missing when creating `meta` from scratch | QuickTime/Final Cut **reject** a `meta` box with no `hdlr` (handler_type `mdir`). Scenario-3 test uses a file with no existing udta/meta/ilst. |
| 5 | Foreign `ilst` atoms corrupted on rewrite | Copy all non-`com.peckworkslab.clipmeta` atoms (iTunes `©nam` etc., third-party `----`) byte-for-byte, in order, before appending ours. Test verifies `©nam` unchanged. |
| 6 | `stco` adjusted when `mdat` precedes `moov` | Only adjust offsets when `mdat` starts **after** the end of `moov`. mdat-first files must be left unchanged. |
| 7 | `co64` / `stco` value exceeds 32-bit boundary undetected | For 32-bit `stco`, fail if `offset + delta > UInt32.MaxValue`; warn under 10% headroom (file approaching 4 GB should already use `co64`). |
| 8 | Temp file left behind on exception | On any failure, delete the temp file and rethrow with context; the source is never opened for writing. Test asserts no temp file remains after a forced exception. |

### Other write-engine invariants
- **The Golden Rule:** the source file is never opened for writing. All mutations → temp file → re-parse to verify → `File.Replace` (atomic same-filesystem swap).
- **Big-endian everywhere.** Every multi-byte MP4 integer is big-endian; always use `BigEndianReader`/`BigEndianWriter`.
- **The `©` prefix is byte `0xA9`.** Read FourCCs with `Encoding.Latin1`, not ASCII, or it mangles. Compare against `"©nam"` etc.
- **`free` padding:** on first clipmeta write, append a 512-byte `free` box after `ilst` so future re-tags don't shift `mdat` (avoids stco/co64 churn). Exceeding the padding triggers a full rewrite.
- **`--set field ""` deletes** the atom — empty and absent are not distinguished.
