# Session log — 2026-08-29 — Per-project Claude account toggle

## What happened
Added a per-project selector for **which `claude` CLI/account a tile launches under**,
replacing the hardcoded name check. Before, only the app names `mixotrophic` and
`rileys-orchestrator` launched `claudeba` (the second account) — it was baked into
`Launch()`. Now every tile/row has an **A / BA** toggle button that flips the project
between `claude` and `claudeba` and auto-saves the choice.

## What changed (all in `DevLauncher.cs`, rebuilt `DevLauncher.exe`)
- New field `Dictionary<string,string> accounts` (folder path → CLI command); loaded in
  the constructor via `LoadAccounts()`.
- New accounts section: `AccountsFile()` / `LoadAccounts()` / `SaveAccounts()`
  (`path|cli` lines, next to the exe), `AccountFor(app)`, `ToggleAccount(app)`,
  `AccountLabel`, `StyleAccountButton`, `MakeAccountButton`.
- `Launch()` — the `useBaAccount` name check is gone; `string cli = AccountFor(a);`.
  `AccountFor` keeps the two legacy names as *defaults* (so nothing regresses) until an
  explicit `accounts.txt` entry overrides them.
- `MakeTile()` — account button added at `(64,66)`, left of ★.
- `MakeRow()` — account button at `(row.Width-132,10)`; `path` right-inset widened
  226→258 and `mod` shifted `Width-222`→`Width-254` to make room (no overlaps).
- Docs: `CLAUDE.md` gained an `accounts.txt` table row and a
  "Per-project account (the A / BA button)" section.

## Decisions
- **Placement:** toggle button on the tile (user's pick), not a launch-dialog dropdown.
- **Command set:** fixed `claude` / `claudeba`; default is plain `claude`; selecting the
  other auto-saves (user's requirement — no confirm/dialog).
- **Storage:** only touched projects get a line; deleting `accounts.txt` reverts to
  defaults. Legacy names stay on `claudeba` by default via `AccountFor`.
- Kept everything C# 5 (Framework `csc`) — no interpolation / expression-bodied members.

## Verification
- Rebuilt with the documented `csc` command — no errors/warnings.
- Launched the exe; it starts and renders. **Not yet click-tested** end-to-end that a
  BA toggle actually launches a `claudeba` tab (needs a real launch to confirm).

## Blocked / next
- Nothing blocked. Suggested next: click a tile's toggle to BA, launch it, confirm the
  tab runs under the second account; consider adding the toggle to favorites **pills**
  too (currently tiles + rows only), and making `ToggleAccount` a cycle if a third
  account is ever added.
- Not committed — left for the user to review/commit.

## Suggested next prompt
"Click-test the A/BA account toggle end to end, add it to the favorites pills, then commit."
