# Session handoff — 2026-08-31 — "Simple com" launch toggle

## What was asked
Add a launch-dialog toggle called **Simple com**, placed under "Read CLAUDE.md
first", ON by default. When on, it auto-instructs the session to communicate
with Riley clearly and simply, like reports to a busy executive, so he doesn't
have to tell every new session how to report.

## What changed (all in `DevLauncher.cs`, then rebuilt `DevLauncher.exe`)
1. **`LaunchOptions`** — added `public bool SimpleComm;`.
2. **`ExecCommPreamble`** — new constant (after `FableOrchestratorPreamble`)
   holding the executive-style communication rule: bold plain-English verdict
   first, short numbered/lettered steps, jargon translated inline in parens,
   costs/thresholds/status in the sentence, a one-line "Bottom line:", plain
   hyphens only.
3. **`LaunchWithPrompt()`** — prepends `ExecCommPreamble` when `SimpleComm` is
   on, placed after the Fable rule and before handoff. Final order stays
   `/loop → CLAUDE.md → handoff → Simple-com → Fable rule → prompt`.
4. **`PromptForLaunchOptions()` dialog** — added `simpleCheck` ("Simple com",
   Checked = true) at (452,158), pushed `handoffCheck` to (452,180), shifted the
   prompt label/box/hint/buttons down 22px, grew the dialog to 640×522, added the
   checkbox to `Controls` and to the returned `LaunchOptions`.
5. **`CLAUDE.md`** — documented the new toggle and the updated prompt-composition
   order.

## Status
**Done and building clean** (csc exit 0). Not yet committed — the user has not
asked to commit. `recent.txt` shows as modified from before this session.

## Suggested next prompt
"Open the launcher, click a tile's ✎, confirm 'Simple com' shows ON under 'Read
CLAUDE.md first', launch, and verify the session gets the exec-comm instruction.
If good, commit."
