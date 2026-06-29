clipmeta command-line tools  v{{VERSION}}  (Windows x64)
========================================================

Two self-contained tools. No .NET install required; each exe bundles its
own runtime.

  clipmetascribe.exe   read, write, and search metadata inside MP4 files
  clipmetaview.exe     show the internal box/atom tree of an MP4

Quick start (PowerShell or Command Prompt, from this folder):

  .\clipmetascribe.exe "clip.mp4" --list
  .\clipmetascribe.exe "clip.mp4" --set tags "airshot|headshot" --set rating 5 --backup
  .\clipmetascribe.exe --find "game=Team Fortress 2 rating>=4"
  .\clipmetaview.exe "clip.mp4"

Run either tool with --help for the full command list.

Tip: add this folder to your PATH to call the tools from any directory.

Notes
-----
* Windows 10/11, 64-bit.
* First run may show a Windows SmartScreen "unknown publisher" prompt,
  because these binaries are not code-signed. Click "More info", then
  "Run anyway". This is expected for an indie tool.
* Your video is never modified in place: writes go to a temp file, are
  verified byte-for-byte, then atomically swapped in. Use --backup to keep
  a timestamped copy of the original.

Project site:  https://srfinch17.github.io/peckworks-clipmeta/
Source code:   https://github.com/srfinch17/peckworks-clipmeta
