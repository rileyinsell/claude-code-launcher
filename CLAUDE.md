# CLAUDE.md — Dev Launcher

A tiny Windows desktop app that shows a tile per dev project. Clicking a tile opens
a Windows Terminal tab in that project's folder and starts `claude` with a prompt
telling it to auto-start the app.

## What's here
| File | Role |
|------|------|
| `DevLauncher.exe` | The app. A standalone .NET WinForms exe — **the window belongs to this exe** (this is why it pins to the taskbar correctly). Built from `DevLauncher.cs`. |
| `DevLauncher.cs` | Source for the exe. UI + tile launch logic. |
| `apps.txt` | **Prompt overrides only** (no longer the tile list). Every folder under `C:\Dev` gets a tile automatically; an entry here (matched by path) overrides that tile's display name and initial prompt. Folders without an entry use the folder name + a generic default prompt. |
| `recent.txt` | Auto-written on every launch (`name|ticks` per line). No longer drives startup ordering (tiles sort by folder modified date); a launch still moves its tile to the front for the current session. Safe to delete. |
| `favorites.txt` | Starred folder paths, one per line. Written when a tile's ★ button is toggled. Starred projects show as pills in a favorites bar under the header, ordered by folder modified date. Safe to delete (nothing starred). |
| `view.txt` | Grid view mode: `tiles` or `rows`. Written by the ▦/☰ toggle in the header. Safe to delete (defaults to tiles). |
| `DevLauncher.ico` | App icon (blue rounded tile + ⚡). Embedded in the exe and used by the shortcuts. |
| `AppLauncher.ps1` | **Legacy / unused.** The original PowerShell+WPF version. The exe no longer reads it. Kept for reference; safe to delete. |
| `Launch.vbs` | **Legacy / unused.** Old no-flash launcher for the PS1 version. |

Shortcuts pointing at `DevLauncher.exe`:
- Desktop: `Dev Launcher.lnk`
- Start Menu: `%APPDATA%\Microsoft\Windows\Start Menu\Programs\Dev Launcher.lnk`

## How a tile launch works
On click, `Launch()` in `DevLauncher.cs` builds a PowerShell command:
```
$Host.UI.RawUI.WindowTitle = '<name>'; Set-Location -LiteralPath '<path>'; claude '<prompt>'
```
It is **Base64-encoded (UTF-16LE)** and passed as `-EncodedCommand` so quoting/special
chars can never break it. It launches via Windows Terminal:
```
wt.exe -w 0 new-tab --title "<name>" --suppressApplicationTitle -d "<path>" powershell.exe -NoExit -ExecutionPolicy Bypass -EncodedCommand <b64>
```
`--suppressApplicationTitle` locks the tab name so `claude` can't overwrite it.
If `wt.exe` isn't available it falls back to a plain `powershell.exe` window.

## Launch dialog (the ✎ button)
The ✎ button on tiles/pills opens a launch dialog with these fields:
- **Model dropdown** — Default / Opus 4.8 / Fable 5 / Sonnet 5 / Haiku 4.5.
  Non-default picks add `--model <id>` to the claude command (ids in the
  `ModelIds` array in `DevLauncher.cs` — update there when models change).
- **Tab name (optional)** — overrides the terminal tab/window title for this
  launch only; empty = project name (current behavior).
- **Loop every N min (checkbox + numeric)** — wraps the final prompt as
  `/loop <N>m <prompt>` so Claude re-runs it on that interval.
- **Read CLAUDE.md first (checkbox, default ON)** — prepends
  `Read CLAUDE.md first.` to the prompt. Skipped automatically if the prompt
  already mentions CLAUDE.md (the default prompts do), so it never stutters.
- **Prompt (multiline)** — empty falls back to the project's default prompt,
  so the dialog can be used just to pick a model or rename the tab.

Prompt composition order matters and is fixed in `LaunchWithPrompt()`:
`/loop <N>m Read CLAUDE.md first. <prompt>` — claude only parses a slash command
at position 0, so `/loop` must come first and the CLAUDE.md instruction rides
*inside* the looped prompt (loop being on can't break it).

Dialog buttons are custom flat dark-theme buttons (`MakeDialogButton`) — default
WinForms buttons render black-on-grey and are unreadable on the dark forms.

## Folder viewer (the 📁 button)
Every tile, row, and favorites pill has a 📁 button that opens `FolderViewerForm`
— a dark VS Code-style browser for that project: explorer on the left, file
content on the right, draggable splitter between. Left side has two modes,
toggled by header pills:
- **FOLDER** — normal directory tree, lazy-loaded (children read only on expand,
  so node_modules can't stall it). Hidden items skipped.
- **MODIFIED** — every file flat, sorted by modified date desc. Skips `.git`,
  `node_modules`, `.vs`, `__pycache__`, `bin`, `obj`, `.idea` and caps at 2000
  rows (`MaxFlatFiles`). Rows are color-coded by recency (<24h cyan, <7d normal,
  older dim). Scans once per window, on first switch to the mode.
Single-clicking a file shows it in the right pane (read-only, Consolas, with a
name/size/lines/modified strip). Binary files (NUL byte in the sample) and the
tail beyond 2 MB (`MaxPreviewBytes`) aren't rendered — the strip says so instead.
**⧉ COPY** puts the viewed file's full text on the clipboard; **OPEN ↗** (or
double-click on the left) opens the file with its default app.

## Tiles vs rows (the ▦ / ☰ toggle)
The header has a view toggle next to search. **Tiles** is the classic grid;
**rows** renders each project as a full-width line (name · path · modified date
· ★ ✎ 📁). Both carry `AppEntry` in `Control.Tag`, so search/filter/MRU work in
either mode. Rows restretch on window resize. Persisted in `view.txt`;
`BuildGrid()` rebuilds the whole grid whenever the mode flips.

## Look & feel
Neon-dark theme throughout: near-black base (`#070D1A`), cyan accents (`#22D3EE`),
palette colors used as *accents* (gradient wash + left edge bar painted in
`Paint` handlers) over dark tiles rather than solid tile fills. Favorites pills
are cyan capsules with a glowing outline. Tile names auto-shrink
(`FitTileFont`, 11.5pt→8pt, two-line wrap) so long project names are never cut
off. The main window opens at up to 1280×820 (clamped to the working area).

## Favorites bar
Each tile/row has a ★ toggle (in the action button group with ✎ and 📁). Starring
pins the project as a neon capsule pill on a favorites bar between the header and
the grid; pills launch on click and are ordered by folder modified date, same as
the grid. Each pill carries its own ✎ (custom-prompt launch dialog) and 📁
(folder viewer) buttons on its right edge.
Persisted as folder paths in `favorites.txt`; the bar hides when nothing is starred.

## Search + ordering
- The header has a search box (top-right): typing filters tiles by name,
  **Enter launches the top visible match**, **Esc clears**. The ➕ New Project tile
  hides while a search is active.
- **Tiles = all folders under `C:\Dev`**, ordered by folder `LastWriteTime`
  (most recently modified first). Hidden folders are skipped. `LoadApps()` scans
  the folder on startup — no list to maintain. Every `Launch()` still stamps
  `recent.txt` and moves its tile to the front for the current session. Tiles
  carry their `AppEntry` in `Control.Tag`; the filter keys off that.

## Customizing a tile's prompt
Edit `apps.txt` (overrides only — folders show up without an entry). One entry per
line, `#` lines are comments:
```
Name | C:\Path\To\Folder | What Claude should do first (the initial prompt)
```
Matched to a folder by path. Reopen the launcher to pick up changes. Keep `|` out
of the prompt text (it's the delimiter). An apps.txt entry pointing outside
`C:\Dev` still gets a tile (appended after the scanned folders).

## Rebuilding the exe (only when DevLauncher.cs changes)
```powershell
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
Get-Process DevLauncher -ErrorAction SilentlyContinue | Stop-Process -Force   # release the file first
& $csc /nologo /target:winexe /out:DevLauncher.exe /win32icon:DevLauncher.ico `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll DevLauncher.cs
```
Build target is .NET Framework `csc` (always present on Windows). `winexe` = no console window.

## Gotchas / hard-won notes
- **Taskbar pinning must be the exe, not a PowerShell host.** Earlier versions launched
  PowerShell and the pin captured PowerShell instead. The exe owns its own window now —
  don't reintroduce a "launch powershell then exit" stub.
- **Windows 11 (24H2) blocks programmatic pinning** of both taskbar AND Start (`Access denied`).
  Pinning is a manual right-click → Pin to taskbar. Don't waste time scripting it.
- **Pasted prompts need TWO escaping layers — both were once missing.** A prompt pasted from
  Outlook/Word hits two distinct Windows quoting traps; `Launch()` now handles both (see the
  helpers above it). Verified end-to-end against the real `claude.exe`.
  1. *Curly "smart" quotes (PowerShell layer).* PowerShell treats the Unicode curly single-quotes
     (U+2018/2019/201A/201B) as string delimiters too — not just ASCII `'`. A curly apostrophe
     ("I've", "don't") closed the `claude -- '...'` string early and the rest got parsed as
     PowerShell code (`ampersand not allowed` / `missing terminator`). `NormalizeQuotes()` maps the
     curly singles to ASCII `'` BEFORE doubling. Used for name/path/prompt.
  2. *Double-quote stripping (native-arg layer).* Windows PowerShell 5.1 wraps a native-command arg
     with spaces in `"..."` but does NOT escape the arg's own `"`, so a JSON/quoted prompt loses its
     quotes and then word-splits on the now-unquoted spaces — `claude` receives only the first chunk
     and the prompt looks "cut off" (e.g. `{ objection: Already`). `WinArgInner()` applies the
     standard `CommandLineToArgvW` escaping (`"` -> `\"`, backslash-run doubling). Only the **prompt**
     needs this (it's the only field passed to `claude.exe` as an argv element); name/path are
     consumed by PowerShell itself. So: prompt = `PsPromptArg` (both layers), name/path =
     `PsSingleQuote` (curly + `'`->`''` only). Don't collapse these back to a bare
     `.Replace("'", "''")`.
- **Launching the launcher from inside a Claude Code session used to taint every tab.**
  A DevLauncher started from a Claude session inherits `CLAUDE_CODE_CHILD_SESSION` (new
  claude thinks it's a nested child → "Transcript saving is off" warning) plus the
  color-suppressing vars Claude Code sets for subprocesses (claude UI loses its colors) —
  and passes them to every tab it opens. `Launch()` now scrubs `CLAUDE*`, `NO_COLOR`, and
  `FORCE_COLOR` from the tab's environment (the `envScrub` block) before starting claude,
  so launches are clean no matter where the launcher was started from.
- **Huge prompts can't ride the command line.** Windows caps a command line around 32K chars;
  a long pasted prompt (UTF-16LE + Base64 into `-EncodedCommand`) blows past it and
  `Process.Start` fails with Win32 error 206, which Windows reports as *"The filename or
  extension is too long"*. `Launch()` now writes any prompt whose escaped form exceeds
  `InlinePromptMax` (1500 chars) to `%TEMP%\DevLauncher\prompt-<guid>.txt`; the tab reads it
  into `$__p`, deletes the file, and runs `claude -- $__p`. The file holds the
  `WinArgInner`-escaped text (NOT the raw prompt): PowerShell passes a variable to a native
  exe without escaping embedded `"`, so the argv escaping must already be baked in — same
  trick as `PsPromptArg`, minus the single-quote layer.
- The icon is generated with `System.Drawing`; `DrawString` needs a `RectangleF`, not a `Rectangle`.
- XAML in the old PS version needed `xmlns:x` declared or `x:Name` fails to parse (legacy file only).
