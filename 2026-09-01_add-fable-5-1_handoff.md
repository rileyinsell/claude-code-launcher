# Session log — 2026-09-01 — Add Fable 5.1 to model list

## What happened
Added **Fable 5.1** as a model choice in the launch dialog dropdown and wired it to the same
Fable orchestrator rule that Fable 5 uses (plan + delegate to Opus sub-agents).

## What changed
- `DevLauncher.cs`
  - `ModelLabels` / `ModelIds`: inserted `Fable 5.1` → id `claude-fable-5-1` (kept `Fable 5`
    below it). Also reflects `Opus 5` now present in the labels.
  - `LaunchWithPrompt()`: the Fable orchestrator preamble now fires for both
    `claude-fable-5` and `claude-fable-5-1`.
- `CLAUDE.md`: launch-dialog model list line updated to match the new dropdown.
- `DevLauncher.exe`: rebuilt with .NET Framework `csc` (winexe, icon embedded).

## Decisions
- Fable 5.1 reuses the exact same `FableOrchestratorPreamble` — no separate rule text.
- Left runtime files (`favorites.txt`, `recent.txt`) out of the commit; they are auto-written.

## Blocked
Nothing.

## Suggested next prompt
None needed. If more models arrive, edit `ModelLabels`/`ModelIds` and the Fable-match `if`
in `LaunchWithPrompt()`, then rebuild.
