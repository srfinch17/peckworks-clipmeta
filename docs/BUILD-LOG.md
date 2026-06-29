# docs/ build log, public GitHub Pages info page

Living record of how `docs/index.html` (the public project landing page) was built, the decisions
behind it, and the placeholders the owner still needs to fill.

## Purpose
A single self-contained HTML page for GitHub Pages (`main → /docs`) that explains peckworks-clipmeta
to a LinkedIn/GitHub visitor: the problem, the inspiration, the solution, the MCP + CLI approach, the
voice-while-gaming workflow, the safety engineering, and the caveats. Built to be shared publicly.

## Decisions (confirmed with owner 2026-06-22)
- **Deploy target:** `docs/index.html` on `main`. GitHub Pages "Deploy from branch → main → /docs".
- **Audience:** Both, story first (gamer/creator inspiration), engineering depth below (for recruiters/HMs).
- **Imagery:** Hand-built inline-SVG diagrams + styled terminal/voice mockups, plus clearly-labeled
  placeholder slots where the owner drops real screenshots / gameplay stills later.
- **Branding:** "Peckworks · clipmeta" + link to github.com/srfinch17/peckworks-clipmeta. Real name
  left as a `[YOUR NAME]` placeholder (not guessed). No fabricated claims.

## Visual identity
Reuses the mission-control THEME tokens from the `educational-html-prep` skill (the same dark dashboard
identity as the job-search study pages): ink backgrounds, orange `--signal`, teal `--have`, amber
`--partial`, blue/violet secondaries; Space Grotesk / IBM Plex Sans / IBM Plex Mono. Self-contained:
inline `<style>`, inline SVG, Google Fonts via CDN with `system-ui` fallback. Project-specific favicon
(film-frame + tag mark) so the public page has its own identity, not the job-search staircase.

## Content sourced from (ground truth, not invented)
- `CLAUDE.md`, architecture, 7 projects, metadata model, the watched-clip + queue summary.
- `docs/PITFALLS.md`, the safety stories (silent mdat deletion → read-lenient/write-strict; AV race
  retry; sandbox junction escape; deferred-tag queue lock; mdta/keys preservation).
- `clipmetascribe/Program.cs`, exact CLI command/flag surface + usage text.
- `clipmetamcp/Tools/*.cs`, exact 17-tool surface (7 read / 4 write / 3 backup / 3 queue).
- Memory store, inspiration framing, MCP v1.1.0, dogfooding status.

## Sections
1. The problem, the library you'll never get through; the montage you'll never make.
2. The idea, tag at the moment of capture; metadata that travels inside the file.
3. The solution, writing into MP4 `----` atoms; the metadata model.
4. Tag by voice while you play, Claude Code + MCP, captured when the memory is freshest.
5. How it's built, Core + two CLIs + MCP server; the 17-tool surface.
6. Find your clips, find / index / vocab / export / search.
7. Built not to corrupt your files, the safety-engineering story.
8. Caveats & roadmap, honest limits.
+ FAQ (defend-it Q&A), Get started, footer.

## Placeholders the owner must fill before publishing
- `[YOUR NAME]` / tagline in hero + footer.
- Logo slot in the topbar (optional).
- Screenshot slots (clearly marked `.shot-placeholder`): real terminal capture, a real
  `library_watching` voice exchange, a real search-results view, a montage/gameplay still.
- Confirm the GitHub URL + any demo video link.
- Verify GitHub Pages is enabled (Settings → Pages → main / /docs).

## Verification
Served `docs/` via `py -m http.server` and screenshotted each diagram with Playwright; fixed SVG
coordinate issues by eye. Screenshots cleaned up after.

## Pitfalls found while building this page
(Kept here, not in docs/PITFALLS.md, that file is reserved for MP4 parser/writer gotchas.)

- **SVG text overflow is invisible in source, obvious in a screenshot.** Two diagram labels
  rendered wrong only when viewed: the MP4-box "writes only here" annotation collided with the
  atom title, and the voice-sequence footnote ran off the right edge because it had no
  `text-anchor="middle"` and the string was longer than the `viewBox` width. Both looked fine in
  the markup. Lesson (matches the educational-html-prep skill): always serve + screenshot every
  inline-SVG figure; never trust the source. Long centered labels must fit the viewBox width, and
  any near-other-elements label needs its coordinates checked visually.
- **Playwright screenshots land in the CWD's `.playwright-mcp/`, not `docs/`.** The relative
  filename resolved against the repo root (where the tool ran), so `check-*.jpeg` appeared at
  repo root. Cleaned up after verification; don't commit them.

## Status: first draft complete + visually verified 2026-06-22.
