Tag your game clips by voice, the moment they happen. clipmeta writes searchable metadata **inside** your MP4 files, so the tags travel with the clip.

## What's new in v1.0.1

This is a hardening and correctness release. No new features, safety and reliability fixes for edge cases found during real-world use of v1.0.0.

- **Safer with truncated or damaged files.** A cut-off or damaged MP4 no longer causes confusing errors or a crashed library scan: writes are cleanly refused, and scanning a folder skips the bad file and names it in the output so you know what was left out. Reading a single file stays deliberately lenient.
- **Won't create duplicate, conflicting tags.** If a file already carries clipmeta metadata in an unexpected location, writes are now refused rather than silently adding a second, divergent copy of your tags.
- **Stronger write verification.** After every write, clipmeta now reads back and checks the actual tag values (not just that something is present), and confirms fields you deleted are really gone.
- **Won't silently lose a tag when multiple tools write at once.** Writes are now serialized across the CLI, the Claude Desktop MCP server, a Claude Code-hosted MCP server, and the tag queue, so a tag written by one process can no longer be silently lost when another rewrites the same clip.
- **Clear message for unfinished recordings.** MP4s with no finalized structure (for example a recording that was still writing when a player crashed) now get a plain "can't be tagged yet" message instead of an internal error.
- **Fixed a CLI backup bug.** `--backup` now uses the same timestamped naming as the rest of the tool, so making a second backup no longer overwrites the first, and CLI backups now show up correctly in backup management tools.
- **Fixed search for multi-word field names.** Field names containing spaces now round-trip correctly through the search index, so a cached search agrees with a live lookup again.
- **Documentation cleanup.** Removed leftover references to a CLI tool that never shipped, and corrected several other inaccuracies in the docs.
- **Now credited to Peckworks Lab.**

**Changelog note:** a few small documentation and wording commits landed on `main` right after v1.0.0 shipped, before this version-bump process existed, so they went out without a version bump. v1.0.1 also formally covers those.

This is also the first release built and published automatically by CI from a tagged commit, rather than assembled by hand.

**Project site:** https://srfinch17.github.io/peckworks-clipmeta/

> Windows 10 / 11 (64-bit). The binaries are self-contained, so you do **not** need to install .NET.

## Which file do I download?

| File | What it is |
|---|---|
| **`clipmeta.mcpb`** | The Claude Desktop extension for voice tagging. **Start here.** This is the whole thing; it does not need anything else. |
| `clipmeta-unpacked.zip` | The same extension, unpacked. Use this **only** if installing the `.mcpb` does nothing (a known bug in the Microsoft Store build of Claude Desktop). |
| `clipmeta-cli-win-x64.zip` | Optional command-line tools (`clipmetascribe`, `clipmetaview`) for people who prefer a terminal. **Not required** for the Claude Desktop experience. |

## Install for Claude Desktop (the main way)

1. Download **`clipmeta.mcpb`** below.
2. Open Claude Desktop, go to **Settings** then **Extensions**.
3. Open **Advanced settings**, then **Extension Developer**, then **Install Extension...**
4. Pick the `clipmeta.mcpb` you downloaded.
5. When prompted, choose your **clips folder**. clipmeta can only ever read or write files inside this folder (a hard sandbox).

That is the entire install. No terminal. In any conversation you can now say *"tag that last clip..."* and Claude resolves which clip you mean and tags it.

**If step 4 does nothing** (the Microsoft Store build of Claude Desktop has a known install bug): download `clipmeta-unpacked.zip`, extract it, and use **Install Unpacked Extension** pointed at the extracted folder instead.

## Install the command-line tools (optional)

1. Download and extract **`clipmeta-cli-win-x64.zip`**.
2. Run from a terminal, for example: `.\clipmetascribe.exe "clip.mp4" --list`
3. Optional: add the folder to your PATH to call the tools from anywhere. See the included `README.txt`.

## Good to know

- **Windows only** for now. Reading, writing, and search are portable, but the live "which clip am I watching" resolution is Windows-only in this v1.x line.
- The binaries are **not code-signed**, so Windows SmartScreen may warn "unknown publisher" on first run. Click **More info**, then **Run anyway**. (Real code signing needs a paid certificate.)
- **Your footage is safe by design.** Every write goes to a temp file that is re-parsed and verified byte-for-byte before an all-or-nothing swap; if any check fails, your original is left untouched. Try it on copies until you trust it.

Full source, the safety engineering, and the gaming-vs-review decision trees are on the [project site](https://srfinch17.github.io/peckworks-clipmeta/).
