# PITFALLS — peckworks-clipmeta

A living log of mistakes, gotchas, and hard-won knowledge. **Append a new entry whenever we hit and fix a real bug or discover something non-obvious.** Consult this before touching the MP4 parser or writer.

Format: newest entries at the top of "Field-discovered." The "MP4 format hazards" section below is seeded from the write-engine design spec and is foundational reference.

---

## Field-discovered (append here as we go)

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
| 2 | Only one `stco`/`co64` adjusted, others missed | Walk **ALL** `trak → stbl → stco/co64`. A stereo clip has video + audio tables; missing one desyncs that track. Integration test checks both. |
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
