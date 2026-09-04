# CLAUDE.md — Dev Launcher

A tiny Windows desktop app that shows a tile per dev project. Clicking a tile opens
a Windows Terminal tab in that project's folder and starts `claude` by default with
a prompt telling it to auto-start the app. The launch dialog can also start Codex.

## What's here
| File | Role |
|------|------|
| `DevLauncher.exe` | The app. A standalone .NET WinForms exe — **the window belongs to this exe** (this is why it pins to the taskbar correctly). Built from `DevLauncher.cs`. |
| `DevLauncher.cs` | Source for the exe. UI + tile launch logic. |
| `apps.txt` | **Prompt overrides only** (no longer the tile list). Every folder under `C:\Dev` gets a tile automatically; an entry here (matched by path) overrides that tile's display name and initial prompt. Folders without an entry use the folder name + a generic default prompt. |
| `recent.txt` | Auto-written on every launch (`name|ticks` per line). No longer drives startup ordering (tiles sort by folder modified date); a launch still moves its tile to the front for the current session. Safe to delete. |
| `favorites.txt` | Starred folder paths, one per line. Written when a tile's ★ button is toggled. Starred projects show as pills in a favorites bar under the header, ordered by folder modified date. Safe to delete (nothing starred). |
| `view.txt` | Grid view mode: `tiles` or `rows`. Written by the ▦/☰ toggle in the header. Safe to delete (defaults to tiles). |
| `accounts.txt` | **Per-project Claude account/CLI override.** `folderPath\|cliCommand` per line (e.g. `C:\Dev\mixotrophic\|claudeba`). Written when a tile's account toggle (the **A**/**BA** button) is flipped. A project with no entry launches `claude` — except the legacy names `mixotrophic`/`rileys-orchestrator`, which default to `claudeba` until an explicit entry overrides them. Safe to delete (everything reverts to those defaults). |
| `.env` | **Local config & secrets — git-ignored.** `KEY=VALUE` lines read by the `Env` class (`.env` next to the exe, loaded once, lazily). Holds the Logic Apps repo path + Azure ids (subscription/resource group/site/location) and the Key Vault name. Missing file/key falls back to a literal in code, so the app still runs without it. Copy `.env.example` → `.env` and fill in. |
| `.env.example` | Committed template for `.env` with placeholder values. |
| `logic-apps.txt` | **Git-ignored cache** for the 🧩 Logic Apps launcher: one **Standard** workflow name per line. Written by the ↻ REPULL button (and auto-created on first open if absent) so the repo isn't rescanned every time. Re-sorted case-insensitively on load. Safe to delete (repull rebuilds it). |
| `logic-apps-consumption.txt` | **Git-ignored cache** for the 🧩 launcher's **Consumption** logic apps: `name\|resourceGroup` per line. Written by ↻ REPULL from an `az resource list` query over the subscription (each consumption app lives in its own resource group). Safe to delete (repull rebuilds it). |
| `secrets.txt` | **Git-ignored cache** for the 🔑 Token Manager's searchable name list: one Key Vault secret **name** per line (names only, never values). Written by the ↻ LIST button from `az keyvault secret list`. Safe to delete (LIST rebuilds it). |
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

Codex launches use the same `wt.exe` / PowerShell terminal path, with the agent
command changed to `codex --dangerously-bypass-approvals-and-sandbox
--ask-for-approval never --sandbox danger-full-access -m <model> -- <prompt>`.

## Launch dialog (the ✎ button)
The ✎ button on tiles/pills opens a launch dialog with these fields:
- **Provider dropdown** - Claude by default, or Codex.
- **Model dropdown** — Default / Opus 5 / Opus 4.8 / Fable 5.1 / Fable 5 / Sonnet 5 / Haiku 4.5.
  Non-default picks add `--model <id>` to the claude command (ids in the
  `ClaudeModelIds` array in `DevLauncher.cs` — update there when models change).
  When Codex is selected, the dropdown switches to Astra / 5.6 Sol / 5.6 Terra /
  5.6 Luna / 5.3 Codex Spark / 5.5, passed as `codex -m <id>`.
  Astra prepends an orchestration rule like Fable, but for Codex sub-agents.
- **Tab name (optional)** — overrides the terminal tab/window title for this
  launch only; empty = project name (current behavior).
- **Loop every N min (checkbox + numeric)** — wraps the final prompt as
  `/loop <N>m <prompt>` so Claude re-runs it on that interval.
- **Read CLAUDE.md first (checkbox, default ON)** — prepends
  `Read CLAUDE.md first.` to the prompt. Skipped automatically if the prompt
  already mentions CLAUDE.md (the default prompts do), so it never stutters.
- **Simple com (checkbox, default ON)** — prepends `ExecCommPreamble`, a rule
  telling the session to communicate with Riley clearly and simply, like reports
  to a busy executive (bold plain-English verdict first, short steps, jargon
  translated inline, costs/status in the sentence, a one-line "Bottom line:",
  plain hyphens only). Sits under "Read CLAUDE.md first" in the dialog. Saves
  having to tell every new session how to report.
- **Handoff (checkbox, default OFF)** — prepends a "use the last available
  handoff to catch up" instruction. Sits *after* the CLAUDE.md lead-in and
  *before* the initial prompt.
- **Prompt (multiline)** — empty falls back to the project's default prompt,
  so the dialog can be used just to pick a model or rename the tab.

Prompt composition order matters and is fixed in `LaunchWithPrompt()`:
`/loop <N>m Read CLAUDE.md first. Use the last available handoff… <Simple-com rule> <Fable rule> <prompt>` —
claude only parses a slash command at position 0, so `/loop` must come first and
the CLAUDE.md + handoff + simple-com instructions ride *inside* the looped prompt
(loop being on can't break it). The prepends are applied in reverse of display
order (Fable rule, then Simple-com, then handoff, then CLAUDE.md, then `/loop`)
so the final text reads top-down.

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
The header also has a **search box** (Ctrl+F focuses it): live name search over
all files *and* folders. One recursive scan builds a cached index (`EnsureIndex`,
shared with MODIFIED mode, same skips, newest first); results show in the list
pane — folders in cyan, "folder" in the size column, capped at 500
(`MaxSearchResults`). Esc or clicking a mode pill clears the search and restores
the mode. Folders open in Explorer on double-click.
Single-clicking a file shows it in the right pane (read-only, Consolas, with a
name/size/lines/modified strip). Binary files (NUL byte in the sample) and the
tail beyond 2 MB (`MaxPreviewBytes`) aren't rendered — the strip says so instead.
**⧉ COPY** puts the viewed file's full text on the clipboard; **⧉ PATH** copies
its full path (works for a selected folder too); **OPEN ↗** (or double-click on
the left) opens the file with its default app. The window opens
at up to 1400×880 (clamped to the working area).

## Logic Apps launcher (the 🧩 button)
Azure-blue 🧩 button in the header, left of the 🔑 token manager. Opens
`LogicAppsForm` — a searchable list of **both Standard workflows and Consumption
logic apps**; **single-clicking a name opens that item's designer in the Azure
portal** in the default browser. Search box filters live (case-insensitive
substring), Esc clears, Enter opens the selected/top match. Live count shown
("290 logic apps", "12 logic apps (of 290)" while filtering); the REPULL status
breaks it down as "N logic apps (X standard · Y consumption)". Each row carries
its type in a dim right-aligned badge ("standard" / "consumption") and is colored
by type — Standard cyan, Consumption amber. Items are `LaItem` objects (name +
`Consumption` flag + `Rg`) in the ListBox, so click/Enter hand the whole item to
`OpenWorkflow`.

- **Two kinds, two sources, two caches, two URLs:**
  - **Standard** workflows are the immediate subfolders of the logic-apps repo
    (`LOGIC_APPS_REPO` in `.env`, ~278). Cached in `logic-apps.txt`. Deep-link is
    the EMA `WorkflowMenuBlade` designer under the shared site
    (`AZURE_RESOURCE_GROUP` / `AZURE_LOGIC_APP_SITE` / `AZURE_LOGIC_APP_LOCATION`),
    `%2F`-escaped. Verified byte-exact against the known-good
    `Agave_AutoCompleteCalls-Migrated` link — don't reshape it.
  - **Consumption** logic apps are standalone `Microsoft.Logic/workflows`
    resources, **each in its own resource group**, enumerated across the
    subscription via `az resource list --resource-type Microsoft.Logic/workflows`.
    Cached in `logic-apps-consumption.txt` as `name|resourceGroup`. Deep-link is
    the classic resource blade `#@/resource/subscriptions/…/providers/
    Microsoft.Logic/workflows/<name>/logicApp` (the `logicApp` menu id is the
    consumption designer) using the item's **own** `Rg`, not `AZURE_RESOURCE_GROUP`.
- **↻ REPULL** first **`git pull`s the Standard repo** so the list is always
  current (the Standard source is a local folder scan, so a repo that's behind
  silently hides new workflows). The pull runs on a **background thread** (60s
  timeout, button disabled while it runs) via `git pull --ff-only --autostash`:
  `--ff-only` never merges or rewrites history, and `--autostash` sets aside and
  restores any uncommitted work, so **a pull can never wipe local changes** — if
  it can't fast-forward (diverged commits) it fails harmlessly and the scan uses
  whatever is on disk. Any skip/failure is appended to the final status as
  `(git pull skipped: …)` (via the `gitPullNote` field) so a stale list is never
  silent; `RunGit`/`GitPull` in `DevLauncher.cs` do the work. Then it rescans the
  repo for Standard (synchronous, fast) and rewrites `logic-apps.txt`, then fires
  the `az resource list` query for Consumption on a **background thread** (120s
  timeout) and rewrites `logic-apps-consumption.txt`. Cached Consumption entries
  stay visible during the query so the list never blanks. On open both caches load
  and merge; if both are empty it auto-repulls once (which now also git-pulls).
  Needs an interactive `az login` for the Consumption half; the Standard half
  works offline (the git pull just fails harmlessly with no network).
- **Config is `.env`-only** — nothing sensitive is compiled into the source. The
  literals passed to `Env.Get(...)` are non-secret fallbacks (empty for the
  Azure ids), so a missing `.env` degrades gracefully: Standard needs the repo
  path + site; Consumption needs only `AZURE_SUBSCRIPTION_ID`. Missing config
  shows a "configure .env" status rather than leaking values.

## Token manager (the 🔑 button)
Gold 🔑 button in the header, right of the 🧩 launcher. Opens `SecretGrabberForm`
(window titled **Token Manager**) — GET a secret from the production Key Vault
**and** SET a new value back into it, both via the `az` CLI on a background thread.
Vault name from `.env` (`KEYVAULT_NAME`). Requires an interactive `az login` with
Key Vault Secrets access (Secrets User for GET, Secrets Officer for SET).

- **Searchable secret list.** The SECRET NAME box doubles as a search box: typing
  filters a live dropdown (`secretList`) of the vault's secret **names** below it
  (case-insensitive substring; a count label shows "N secrets" / "M of N"). Single-
  clicking a name (or Enter on a selected one) fills the name box and immediately
  GETs it; Down-arrow from the name box jumps into the list. The names come from
  `az keyvault secret list --query "[].name" -o tsv` and are cached in `secrets.txt`
  (names only, never values — git-ignored). **↻ LIST** re-queries the vault and
  rewrites the cache (60s timeout, buttons disabled while it runs, background
  thread). On open the cache loads; if it's empty the form lists once automatically.
- **GET** runs `az keyvault secret show --query value -o tsv` for the named secret
  and fills the VALUE box (masked; 👁 reveals, ⧉ COPY copies). Unchanged behavior.
- **SET** writes the VALUE box back under SECRET NAME (`az keyvault secret set`),
  creating the secret or adding a new version. **Guards:**
  - A confirm dialog fires first — SET is destructive/outward-facing (it overwrites
    the value the vault returns for that name).
  - The value is passed to `az` via a **temp `--file`**, not the command line, so
    arbitrary characters can't break shell quoting or inject. The temp file is
    written UTF-8-no-BOM and **deleted in a `finally`** so the secret never lingers
    on disk.
  - The secret NAME is validated to `[0-9a-zA-Z-]` (same rule as GET) before it's
    interpolated into the `cmd.exe` arg — so the name is injection-safe too.
- The VALUE box is now editable; its `TextChanged` keeps `currentValue` in sync so
  ⧉ COPY always copies exactly what's shown (fetched or freshly typed).

## Tiles vs rows (the ▦ / ☰ toggle)
The header has a view toggle next to search. **Tiles** is the classic grid;
**rows** renders each project as a full-width line (name · path · modified date
· ★ ✎ 📁). Both carry `AppEntry` in `Control.Tag`, so search/filter/MRU work in
either mode. Rows restretch on window resize. Persisted in `view.txt`;
`BuildGrid()` rebuilds the whole grid whenever the mode flips.

## Per-project account (the A / BA button)
Each tile and row has a small account toggle in its action group (left of ★). It shows
**A** (the default `claude` account, understated grey) or **BA** (the second account
`claudeba`, glowing amber). Clicking it flips the project between the two and **auto-saves**
immediately to `accounts.txt` — no dialog. `claudeba` is a PowerShell profile function that
points `CLAUDE_CONFIG_DIR` at the second account and adds `--dangerously-skip-permissions`;
it sets its own config dir *after* the tab's `envScrub` wipes `CLAUDE*` vars, so the switch
survives. `Launch()` calls `AccountFor(app)` to pick the CLI: an explicit `accounts.txt`
entry wins; otherwise the legacy names `mixotrophic`/`rileys-orchestrator` default to
`claudeba`; otherwise `claude`. Adding a third account is a one-line change to `SecondCli`
plus turning the toggle into a cycle (see `ToggleAccount`).

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
- **Huge prompts can't ride the command line — there are TWO length limits, handled
  separately.** Windows caps a command line around 32K chars. `Launch()` handles prompts in
  three tiers (the launch dialog's prompt box itself is uncapped — `MaxLength = 0`):
  1. **Small** (escaped ≤ `InlinePromptMax`, 1500): passed inline, verbatim.
  2. **Medium** (1500 < escaped ≤ `CmdLinePromptMax`, 31000): the *outer* `-EncodedCommand`
     (UTF-16LE + Base64) would blow past 32K, so the prompt is written to
     `%TEMP%\DevLauncher\prompt-<guid>.txt`; the tab reads it into `$__p`, deletes it, and
     runs `claude -- $__p`. The file holds the `WinArgInner`-escaped text (NOT the raw
     prompt): PowerShell passes a variable to a native exe without escaping embedded `"`, so
     the argv escaping must already be baked in — same trick as `PsPromptArg`, minus the
     single-quote layer. **This only shrinks the OUTER command** — the prompt still lands on
     `claude.exe`'s own command line, so it can't fix tier 3.
  3. **Too large for claude.exe's own argv** (escaped > `CmdLinePromptMax`): otherwise the
     final `claude`/`claudeba` invocation fails *inside the tab* with Win32 206 (*"The
     filename or extension is too long"*) — the profile function calls `claude.exe` with the
     prompt as an argument, and that command line overflows. So `Launch()` writes the **raw**
     prompt to `<project>\.devlauncher-prompt-<guid>.md` and passes claude a short note
     telling it to read that file and delete it. The argv stays tiny, so a prompt can be any
     size. Trade-off: a tier-3 prompt is read from a file rather than received directly, so a
     leading slash command (e.g. `/loop`) in an oversize prompt won't be parsed — rare, since
     oversize prompts are pasted mega-prompts, not slash commands.
- The icon is generated with `System.Drawing`; `DrawString` needs a `RectangleF`, not a `Rectangle`.
- XAML in the old PS version needed `xmlns:x` declared or `x:Name` fails to parse (legacy file only).
