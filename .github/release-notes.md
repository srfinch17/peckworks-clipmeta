<!--
  TEMPLATE NOTE: this file is the source/template for the NEXT release body
  (v1.0.1), not a record of what was published for v1.0.0. The v1.0.0 body
  was hand-published (this workflow didn't exist yet, see CLAUDE.md's CI
  section) and had already drifted from this file by the time it shipped.
  A later task fills in the "What's new in v1.0.1" section below with the
  actual changelog before the v1.0.1 tag is cut; the rest of the body is
  meant to stay accurate release over release and should only need
  touch-ups, not a rewrite, each time. When cutting the release, delete
  this comment block, it is editorial, not part of the published body.
-->

Tag your game clips by voice, the moment they happen. clipmeta writes searchable metadata **inside** your MP4 files, so the tags travel with the clip.

## What's new in v1.0.1

<!-- TODO (D4): fill in with the actual v1.0.1 changelog before tagging. -->
- TODO: summarize this hardening/fix pass (docs truth fixes, PITFALLS repairs, CLI readme fix, and whatever code fixes Part D lands).

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
