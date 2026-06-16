# PITFALLS — peckworks-clipmeta

A living log of mistakes, gotchas, and hard-won knowledge. **Append a new entry whenever we hit and fix a real bug or discover something non-obvious.** Consult this before touching the MP4 parser or writer.

Format: newest entries at the top of "Field-discovered." The "MP4 format hazards" section below is seeded from the write-engine design spec and is foundational reference.

---

## Field-discovered (append here as we go)

### 2026-06-15 — Index write truncated the existing index on open (fixed: temp-then-atomic-swap)
- **Symptom (latent):** `ClipMetaIndex.WriteToFile` opened the destination with
  `new StreamWriter(filePath, append: false, …)`, which truncates the target the instant it
  opens. A write interrupted between that open and the final flush — crash, power loss,
  disk-full, or an exception while serializing — left the user's previously-built
  `.clipmeta-index` truncated or empty. Provable in a test: feed an `IndexData` whose entry
  list throws mid-enumeration and the on-disk index is left corrupt at the failure point.
- **Cause:** writing in place. Every *other* mutation in the codebase already goes temp →
  verify → atomic swap; the index writer was the one place that didn't.
- **Fix:** serialize to `"{filePath}.{guid}.tmp"`, then `File.Move(temp, filePath, overwrite: true)`
  (same-volume atomic `MoveFileEx(REPLACE_EXISTING)`), wrapped in the write engine's
  `Mp4Writer.RetryOnTransientLock` for the AV/indexer race. On any failure the temp is deleted
  and the original index is untouched (it is never opened for writing until the swap). Encoding
  unchanged (UTF-8) so the format round-trips exactly.
- **Lesson:** any "overwrite a file the user cares about" path must be temp-then-atomic-swap, not
  write-in-place — truncation happens at *open*, long before the bytes you meant to write.

### 2026-06-15 — Foreign-atom test assumed a single `ilst`; a real clip has two `meta` boxes (fixed test)
- **Symptom:** Expanding the pristine corpus made `Write_ForeignAtoms_Preserved` fail on exactly
  one clip (`2022-02-01 21.50.02.mp4`): "Foreign atom count changed. Before: 2, After: 0" — as if
  a clipmeta write had *deleted* two pre-existing metadata atoms.
- **Investigation (NOT a writer bug):** dumping the post-write tree proved the writer was correct
  and safe. This clip carries **two metadata containers**: a **moov-level `meta`** (a *sibling* of
  `udta`, `hdlr` type **`mdta`**, with a **`keys`** box and a key-indexed `ilst` — the Apple/
  QuickTime metadata-keys format used for make/model/GPS-style data), plus `udta/©xyz`. On write,
  clipmeta correctly leaves that foreign `meta` **byte-for-byte untouched** and creates its OWN
  iTunes-style `udta→meta→hdlr(mdir)→ilst` for its `----` atoms. The file now has **two `ilst`
  boxes**. mdat + all chunk offsets were also proven identical (media-integrity test passed).
- **Cause (in the test):** `Write_ForeignAtoms_Preserved` did `FindNode(root, type=="ilst")`,
  which returns the **first** `ilst`. Before the write that's the foreign one (2 atoms); after,
  the writer's brand-new (foreign-free) `ilst` sorts first → 0 foreign → false "lost atoms".
- **Fix (test only):** count foreign atoms across **every** `ilst` (`FindAllNodes`), not just the
  first. No production change — the writer's "don't touch a metadata format you don't own" behavior
  is exactly right.
- **Lesson:** an MP4 may hold more than one `meta`/`ilst` container, in different formats
  (iTunes `mdir` vs Apple `mdta`/keys), at different levels (movie `udta` vs moov-level `meta`).
  Test helpers that assume "the one ilst" are wrong on real-world clips. (Same class of harness
  assumption as the ZC112 chunk-window overrun below — diverse real clips surface what same-source
  fixtures never do. **`mdta`/keys metadata is a documented format the writer preserves but does
  not edit.**)

### 2026-06-12 — File.Replace races antivirus on freshly-written files (fixed: bounded retry)
- **Symptom:** The write suite intermittently failed (~1 test, ~30% of runs) with
  `IOException: The process cannot access the file because it is being used by another process`
  out of `Mp4Writer`'s final `File.Replace`. Diagnosed by looping the suite under a TRX logger
  until it caught a red — it was always a *sharing violation*, never an assertion.
- **Cause:** on Windows, antivirus / the Search indexer grabs a just-written file for a second
  or two. The writer creates the temp, releases its own deny-writers handle, then calls
  `File.Replace` — and if AV is mid-scan on the temp or the destination, ReplaceFile fails. The
  writer correctly *failed safe* (refused, original untouched), but a clean write shouldn't lose
  to a transient lock — and a real user tagging a clip seconds after a recorder made it could
  hit the same thing.
- **Fix:** `Mp4Writer.RetryOnTransientLock` wraps ONLY the final swap, retrying up to 5× with a
  100 ms × attempt backoff on `IOException`/`UnauthorizedAccessException`. Safe by construction:
  the temp is already fully written and verified before the swap, so retrying the atomic
  operation weakens no guarantee; if every attempt fails the last exception still propagates
  (fail safe, unchanged). Non-transient exceptions are not retried. Deterministic unit tests
  drive the helper with zero delay (no real locks, no new timing-dependent flake).
- **Lesson:** an atomic file swap on Windows must tolerate transient AV/indexer locks; retry the
  *post-verification* swap, never the verification itself.

### 2026-06-12 — Media-integrity scanner's fixed window overran the final chunk (fixed test)
- **Symptom:** Adding a new real clip (`ZC112.mp4`, mdat-first, 290 MB) made
  `RealClip_MultiFieldWrite_MediaByteIdentical` fail: "chunk table[1] entry 8931 points at
  different data after rewrite ... the track would play garbage" — even though the mdat SHA-256
  check (which runs first) PASSED, i.e. the media was provably byte-identical.
- **Cause:** the WRITER was correct. `MediaIntegrityScanner` compares a fixed 64-byte window at
  each chunk offset; ZC112's last chunk sits 38 bytes before mdat-end, so the window spilled 26
  bytes into the following `moov` box — which legitimately changed when the test wrote metadata.
  The mismatch at "index 40" was 2 bytes past the mdat boundary, i.e. non-media.
- **Fix (test helper only):** `ClampToMdatEnd` bounds each comparison window to the end of the
  mdat containing the offset, so only real sample bytes are compared. No production code
  changed; ZC112 and the moov-first `Stargaze.mp4` both pass.
- **Lesson:** chunk *sample* data is bounded by its mdat; a verifier reading a fixed span past a
  boundary-hugging final chunk measures the next box, not the media. Diverse real clips (here a
  chunk flush against mdat-end) surface harness assumptions that synthetic fixtures and
  same-source clips never hit — exactly why the pristine set should span multiple creators.

### 2026-06-12 — Clearing metadata left an ~80-byte schema/container husk (fixed)
- **Symptom:** An agent hammering the MCP write tools noticed a clip that was tagged then fully
  cleared came back ~80 bytes LARGER than pristine, twice (Stargaze 3,746,496 → 3,746,576;
  ZC112 +80). The file *looked* bare (reads filter the internal field) but still carried a
  `com.peckworkslab.clipmeta:schema=1` atom inside a live `udta→meta→hdlr→ilst` chain.
- **Cause:** two gaps. (1) The schema stamp is only ADDED on value-storing writes, but nothing
  ever REMOVED it when the last user field was deleted — `clip_clear_fields` (a delete-only
  mutation, not clear-all) left it orphaned. (2) Even clear-all, which did sweep the schema
  atom, left the now-empty `ilst`/`meta`/`udta` boxes behind.
- **Fix (`Mp4Writer`):** `RemoveOrphanedSchemaStamp` adds the schema key to `DeleteFields` when
  a mutation removes the last user field; `DetermineEmptyChainRemoval` then drops the emptied
  container chain (innermost-out: ilst → meta-if-only-hdlr-left → udta-if-only-meta-left). A
  write→clear round-trip is now byte-identical to pristine (new `WriteThenClearAll_ReturnsFile
  ToBytePristine` test proves it on a moov-first fixture, offsets and all).
- **Conservatism that matters:** the chain is dropped only when nothing else needs it. A
  surviving clipmeta field, OR any foreign atom (iTunes `©nam`, another tool's `----`), keeps
  the whole chain — tested by `ClearAll_WithForeignAtomPresent_KeepsChainAndForeignAtom`. The
  moov-size prediction subtracts the exact removed-box size, and the existing hard moov-size
  assert in `WriteMoov` is the backstop if that prediction is ever wrong.
- **Lesson:** "remove the data" and "remove the now-empty containers that held it" are two
  jobs; a rewriter that does only the first leaves growing cruft and makes "did we ever touch
  this file" unanswerable.

### 2026-06-12 — Packed .mcpb install silently fails on the Microsoft Store build (workaround: unpacked)
- **Symptom:** Settings → Extensions → Advanced settings → **Install Extension…** → pick
  `clipmeta.mcpb` → the file dialog closes and *nothing* happens. No toast, no error, no card.
- **Cause (from `main.log` in the app's package container):** the packed file is routed into
  `installDxtUnpacked`, which expects a *folder* containing `manifest.json` and fails with
  `No manifest.json found in extension folder` — logged but never surfaced in the UI. App bug
  in Claude Desktop **Microsoft Store/MSIX build** (observed on `Claude_1.12603.1.0`); the
  bundle itself was verified well-formed (manifest at zip root, forward-slash entries).
- **Workaround that passed the E2E gate:** extract the bundle and use **Install Unpacked
  Extension** on the folder. `pack-mcpb.ps1` now keeps that folder as `dist/clipmeta-unpacked/`.
  Once installed: binary spawn, stdio handshake (2025-11-25 echoed), `tools/list`, and a real
  `clip_get_metadata` round-trip all worked first try — **R2 retired** 2026-06-12.
- **Where the Store build hides its logs/config:** NOT `%APPDATA%\Claude` — everything lives in
  `%LOCALAPPDATA%\Packages\Claude_pzs8sxrjxfjjc\LocalCache\Roaming\Claude\` (`logs\main.log`,
  `logs\mcp-server-<Name>.log`, `claude_desktop_config.json`). Check `main.log` first for any
  silent extension failure.
- **Lesson:** a silent UI no-op almost always has a logged error somewhere — find the app's log
  directory before re-trying gestures. And ship the unpacked layout alongside the bundle: it is
  the universal fallback when a host's packed-install path is broken.

### 2026-06-12 — There is no drag-and-drop install for .mcpb bundles (fixed in docs)
- **Symptom:** The design spec, README, and pack-script output all said to install the bundle by
  "dragging onto Claude Desktop → Settings → Extensions." The user tried it on the real app:
  nothing happens — there is no drop target on the Extensions page.
- **Reality (per Anthropic's help center, verified 2026-06-12):** local bundles install via
  Settings → Extensions → **Advanced settings** → Extension Developer → **Install Extension…**
  → file picker. The folder-picker for `user_config` (our clips-library sandbox) appears during
  the install prompts.
- **Fix:** README, spec §"User install story", plan phase 5, and `pack-mcpb.ps1` messages all
  corrected to the button flow.
- **Lesson:** UI install-flow claims are field claims, not spec claims — they must be verified
  on the actual app version before they reach user-facing docs. We verified the *manifest
  schema* against live docs but never the *install gesture*. (Phase-4 `--install` exists
  precisely because the bundle flow could change under us; same reasoning applies to docs.)
- **Symptom:** The MCP library sandbox checked `resolvedPath.StartsWith(root)` after
  `Path.GetFullPath` — and an adversarial probe **escaped it**: a directory junction inside the
  library pointing outside it passes the lexical check while `FileStream` happily follows the
  reparse point to the outside target. `mklink /J` needs no admin rights.
- **Fix:** Containment is now checked on the OS-canonical path — every junction/symlink
  component resolved via `FileSystemInfo.ResolveLinkTarget(returnFinalTarget: true)`, walking
  root-to-leaf — and the configured root is canonicalized the same way (a junction *root* is
  legitimate). Cloud-placeholder files (Dropbox/OneDrive) are reparse points but **not** links:
  `ResolveLinkTarget` returns null for them, so they pass through — a blanket reparse-point ban
  would have broken online-only clips.
- **Related fixes from the same review:** NTFS alternate-data-stream syntax
  (`real.mp4:payload.mp4` — the *stream* name satisfies a `.mp4` suffix check) is now refused;
  and `Path.TrimEndingDirectorySeparator` keeps the separator on a drive root, so naive
  `root + '\'` built `"C:\\\\"` and refused **every** file on a whole-drive library — use
  `Path.EndsInDirectorySeparator` before appending.
- **Lesson:** `GetFullPath` resolves `..` but not reparse points; the filesystem resolves both.
  Any check done on the lexical path can disagree with what the OS actually opens.

### 2026-06-11 — `Compress-Archive` zip entry separators are PowerShell-version-dependent
- **Symptom (potential):** Under Windows PowerShell 5.1, `Compress-Archive` writes zip entry
  names with backslashes (`server\clipmetamcp.exe`), violating the ZIP spec; spec-strict
  extractors then can't find the `.mcpb` bundle's `server/clipmetamcp.exe` entry point —
  installed-but-never-spawns. pwsh 7 on this machine happened to emit forward slashes.
- **Fix:** `pack-mcpb.ps1` uses `[System.IO.Compression.ZipFile]::CreateFromDirectory`, which
  always writes forward slashes regardless of which PowerShell runs the script.
- **Lesson:** Artifacts that must be byte-deterministic shouldn't depend on which shell built
  them; go to the BCL API directly.

### 2026-06-11 — Garbage bytes in an .mp4 parse "successfully" to an empty tree
- **Symptom:** An MCP test assumed `Mp4Parser.ParseFile` throws on a garbage file; it doesn't —
  the parser's deliberate leniency (clamp oversized boxes, stop at damage; see the mdat entry
  below) means tiny garbage files parse to a tree with no metadata, no exception.
- **Lesson:** "Corrupt file" tests against the read path must assert *empty result + session
  survives*, not an exception. Only the **write** path treats unaccounted bytes as fatal.

### 2026-06-10 — Parse and copy used separate file opens (fixed)
- **Symptom (potential):** The writer parsed the source, closed it, then re-opened it to copy
  bytes. A process writing to the file in between (a capture tool still recording the clip
  being tagged) would make the copied bytes disagree with the parsed chunk offsets — torn output.
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
  the structure. Also: the real test clips that happen to be on hand are all mdat-first, so the
  offset-adjustment code path is exercised ONLY by the synthetic moov-first fixtures. Layout is
  a per-file fact, not a property of the source tool — never assume all clips share one layout;
  both paths must stay covered regardless of what the real-clip sample looks like.

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
| 1 | Fragmented MP4 (`moof` boxes — produced by some streaming/live-capture recorders) | Detect on parse; refuse write with a clear `UnsupportedFormatException`. |
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
