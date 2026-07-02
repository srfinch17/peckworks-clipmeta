Tag your game clips by voice, the moment they happen. clipmeta writes searchable metadata **inside** your MP4 files, so the tags travel with the clip.

## What's new in v1.1.0

This release adds a way to see the internal structure of your clips as structured data. It builds on v1.0.1's write-safety work; tagging behavior is unchanged, and nothing here writes to your files.

- **Inspect a clip's internal structure as JSON.** A new read-only view exposes the full MP4 box/atom tree of a clip as machine-readable JSON: every box's type, friendly name, size, position, and (for metadata boxes) its value, with clipmeta's own tags flagged. Useful for scripts, a structure view on a web page, or just understanding how a file is laid out.
  - In **Claude Desktop**, a new `clip_get_boxtree` tool lets Claude examine a clip's structure on request, as JSON or as a readable tree.
  - From the **terminal**, `clipmetaview <clip>.mp4 --json` prints the structure as JSON, and `clipmetaview --definitions` prints a dictionary of what each box type means.
- **One consistent structure view everywhere.** The tree renderer now lives in the shared core, so the readable tree from the command line and from the new Claude Desktop tool is identical, byte for byte.
- **All of v1.0.1's safety carries forward.** Verified, all-or-nothing writes; the hard clips-folder sandbox; refusal on damaged or non-standard files. Reading and inspection stay lenient.

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
