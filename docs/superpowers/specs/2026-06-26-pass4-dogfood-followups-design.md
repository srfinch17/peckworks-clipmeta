# Pass-4 Dogfood Follow-ups, Design Spec

**Date:** 2026-06-26
**Round:** Secondary fixes from the 2026-06-26 watch-and-tag dogfood (subset)
**Author:** Peckworks Lab
**Predecessors:** pass-4 review-mode watcher (PR #35, merged). Source evidence: `clipmeta_testrun_log.md` §7.

---

## Problem Statement

The 2026-06-26 dogfood triage surfaced a cluster of secondary findings (testrun §7). This round takes
the **three unambiguous correctness/robustness fixes**; the debatable preference/shape changes (notes
prose separator, always-echo of `drainedFromQueue`/`queuePending`) are deferred for owner sign-off.

1. **`dry_run` previews the WRONG result (real bug).** `WriteTools.ExecuteWrite` sets `mutation.DryRun`,
   the writer returns immediately without touching the file, and the handler then reads the **unchanged
   current file** back (`WriteTools.cs:553,587`). So `dry_run:true` reports the file's *current* metadata,
   not the *predicted* post-write state. In the log this read as "dry_run previewed a merge that the real
   replace didn't perform", actually it was echoing the still-present current data. A caller relying on
   `dry_run` is misled.

2. **No bounded retry on the initial source-open lock.** `Mp4Writer` retries only the final `File.Replace`
   (`Mp4Writer.cs:253`); the initial read-open (`Mp4Writer.cs:124`) throws immediately on a sharing
   violation. The log hit a clip that a "closed" player still held (lingering handle / indexer / AV), and
   the write failed twice where a brief backoff would have ridden it out, the same transient-lock class
   `File.Replace` already retries for.

3. **The queue-drain → provenance path is correct but untested.** The drain reconstructs the mutation
   with the default `StampProvenance = true` (`QueuedMutation.ToMutation` → `Mp4Writer`), so a drained
   tag *is* stamped, but no test pins it, which is exactly the gap that let the log's false "provenance
   missing on the queue path" claim stand unchallenged.

---

## Scope

**In:** the three items above. **Out (deferred, owner sign-off):** notes prose separator `" "` → `". "`;
always-echoing `drainedFromQueue`/`queuePending` on `library_watching`; MPC title-retry before access-time
fallback; the structured-field tool-description nudge. **Also out:** timestamp/fire-N-ahead (AC2), gaming
mode, their own specs.

---

## 1. Dry-run preview correctness

### Decision
Compute the **predicted curated field set** from the current fields + the mutation, reusing the *same*
`Normalizer` the writer uses, so the preview cannot drift from the actual write. No temp-file write, no
`IMediaWriter` interface change (SOLID, additive).

### Design
New pure Core helper `clipmeta.core/Write/MetadataPreview.cs`:

```csharp
public static class MetadataPreview
{
    /// Predicts the curated (user-facing) fields a write of `mutation` would leave on a clip whose
    /// current user fields are `current`. Reuses Normalizer so preview == actual post-write read-back.
    public static IReadOnlyList<(string Field, string Value)> Predict(
        IReadOnlyList<(string Field, string Value)> current, MetadataMutation mutation);
}
```

Algorithm (bare field names throughout; mutation atom keys are domain-qualified, strip the
`com.peckworkslab.clipmeta:` prefix):
- Seed an ordered map from `current` (values are already display-decoded by `GetUserFields`).
- `SetFields`: empty/null value → remove the field (the delete idiom); else → `Normalizer.NormalizeValue`-equivalent replace (trim / pipe-normalize / rating-clamp, reuse via `Normalizer`).
- `AppendFields`: `Normalizer.AppendValue(field, currentOrEmpty, normalizedIncoming)`, identical to the writer's fold (prose space-join / pipe-merge+dedup).
- `DeleteFields`: remove the field.
- Internal stamps (`schema`, `tagged_by`) are never seeded (excluded by `GetUserFields`) and never added, matching what a real post-write `clip_get_metadata` shows (it hides internal fields). Preserve insertion order; new fields append.

> `Normalizer.NormalizeValue` is currently private; expose the per-field normalization the preview needs
> (either make it `internal` + `InternalsVisibleTo`, or add a small public `Normalizer.NormalizeFieldValue`).
> Prefer a public `NormalizeFieldValue(field, value)` so the preview and writer share one definition.

### MCP layer (`WriteTools.ExecuteWrite`)
Branch on `dryRun` **before** invoking the writer:
- Parse the clip (`ReadTools.ParseClip`), get current user fields (`ClipMetaReader.GetUserFields`),
  `MetadataPreview.Predict`, categorize with `ClipMetaStats.Categorize`, and assemble the same result
  shape `GetMetadata` returns (`path`, `sizeBytes`, `fields`, `knownUnset`, `customFields`), plus
  `dryRun:true`, `backupPath:null`, and the `describeChange` set/deleted names.
- Non-dry-run path is unchanged.

### Gold test (pins the bug shut)
For set, append, and delete cases on a scratch clip: capture the `dry_run:true` `fields`; then perform
the **real** write and read back; assert the dry-run `fields` **equal** the post-write `fields`. This
makes preview-vs-actual divergence impossible to reintroduce silently.

---

## 2. Bounded retry on the initial source-open

Wrap the read-open (`Mp4Writer.cs:121-132`) in the existing `RetryOnTransientLock` (the same helper the
final swap uses: `maxAttempts: MaxReplaceAttempts (5)`, `baseDelayMs: ReplaceBackoffMs (100)` → ≤ ~1.5 s).
On exhaustion, throw the existing friendly "open for writing elsewhere" `IOException` unchanged.

- A genuinely-playing clip still fails (correctly), just after a brief backoff, not instantly. Acceptable:
  direct writes add ≤ ~1.5 s on a truly-locked clip; queue drains are background. This rides out the
  lingering-handle / indexer / AV transient the log hit.
- The retry predicate already matches `IOException`/`UnauthorizedAccessException`, the sharing-violation
  surface, so no new exception handling.

### Test
A fake/sequenced open that throws `IOException` once then succeeds → the write completes (drive
`RetryOnTransientLock` with zero delay, as the existing retry tests do). And: a persistent failure still
surfaces the friendly message.

---

## 3. Queue-drain → provenance test

Pure test addition (no production change, behavior is already correct):
- Enqueue a tag for an unlocked scratch clip via `TagQueue.Enqueue`, `TagQueue.Drain` it with a real
  `Mp4Writer`, re-parse with `ClipMetaReader.GetFields` (the **raw** reader that includes internal
  fields), and assert `tagged_by == "Peckworks ClipMeta"` is present. This is the test whose absence let
  the log's false claim stand.

---

## 4. Affected types

| Type | Change |
|---|---|
| `MetadataPreview` (new, Core) | `Predict(current, mutation)`, pure predicted-field computation. |
| `Normalizer` | Expose `NormalizeFieldValue(field, value)` (was private) so preview and writer share one rule. |
| `Mp4Writer` | Wrap the initial source-open in `RetryOnTransientLock`. |
| `clipmetamcp` `WriteTools.ExecuteWrite` | Dry-run branch builds the result from `MetadataPreview` instead of reading the unchanged file. |

No interface changes; no new MCP tool; no NuGet. The `library_watching` response shape is untouched.

---

## 5. Test strategy

Per CLAUDE.md, a CLI/MCP write-surface behavior change ⇒ run the **full** `clipmetamcp.Tests` and
`clipmetascribe.Tests` (not a filter).
- `MetadataPreviewTests` (Core/scribe): set/append/delete/prose-join/pipe-dedup predictions; empty-deletes.
- `WriteToolsTests` (mcp): dry-run `fields` == real-write read-back (the gold test); dry-run leaves the
  file byte-unchanged; non-dry-run unchanged.
- `Mp4Writer` open-retry: transient-then-success completes; persistent failure surfaces the friendly error.
- Queue-drain provenance: drained clip carries `tagged_by`.

---

## 6. Definition of Done

1. `dotnet build`, 0 warnings, 0 errors.
2. Full `clipmetascribe.Tests` + `clipmetamcp.Tests` green, including the dry-run gold test and the
   queue-drain provenance test.
3. `dry_run:true` reports predicted post-write fields, byte-identical to a real write's read-back.
4. A transient source-open lock is retried; a persistent one still fails with the friendly message.
5. Zero NuGet; new public types/methods documented; PITFALLS updated with the dry-run-preview gap.

---

## 7. Deferred (recorded)

Notes prose separator (`" "`→`". "`, DRY into one shared constant used by both `Normalizer.AppendValue`
and `TagQueue.MergeAppend`); always-echo `drainedFromQueue`/`queuePending`; MPC title-retry; structured-
field nudge; AC2 timestamp; gaming mode. Each pending owner sign-off or its own spec.
