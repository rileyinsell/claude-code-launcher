# 2026-11-16 - Servbank_Take_To_New_Lead missing from the Logic Apps lister

## What happened
Riley reported that the Standard workflow `Servbank_Take_To_New_Lead` did not show in the
🧩 Logic Apps launcher, even after ↻ REPULL.

## Root cause
REPULL's `git pull --ff-only --autostash` on `C:\Dev\insellerate-logic-apps` was failing every
time. Three untracked local files in `sessions/` had the same names as files that newer commits
on GitHub add, and git refuses to overwrite untracked files. The local repo was stuck 120
commits behind (at c963b5a, Oct 22), and the workflow was added later (PR #392, 96a1274).
The launcher worked as designed: the pull failed harmlessly and the scan used the stale folder,
with a "(git pull skipped: ...)" note in the status line that was easy to miss.

## What changed
- No launcher code change.
- In `insellerate-logic-apps`: confirmed the 3 blocking files were byte-identical to origin/main,
  deleted the local copies, and pulled. Repo is now level with origin/main (6f99992 at the
  time) and `app/Servbank_Take_To_New_Lead` exists.
  Files: `sessions/HANDOFF-2026-11-09-keyvault-connector-cost_rcohen.md`,
  `sessions/SESSION-2026-10-27-02_rcohen.md`, `sessions/SESSION-2026-11-09-01_rcohen.md`.

## Decisions
- Did not change the REPULL logic (scope kept to the fix). Option for later: make the
  "git pull skipped" note louder (e.g. amber status), or auto-handle the "untracked files
  would be overwritten" case when the local files are identical to origin.

## Blocked
Nothing.

## Suggested next prompt
"In the Logic Apps launcher, make a failed git pull during REPULL impossible to miss
(highlight the status in amber and show the reason)."
