# Handoff — REPULL now git-pulls the Logic Apps repo first (2026-09-04)

## What happened
Riley noticed the Logic Apps launcher (🧩) was missing `FlexPoint_ClickToCallCount`.
Root cause: the Standard list is built by scanning subfolders of the local repo
`C:\Dev\insellerate-logic-apps\app`, and that repo was 22 commits behind. The
workflow existed in `origin/main` but the folder wasn't on disk yet, so it couldn't
appear. (Consumption apps come from `az`, but this one is Standard.)

Two things were done:
1. Pulled the repo safely by hand (stash the one modified tracked file `ATTRIBUTION.md`
   → `git pull --ff-only` → `git stash pop`). Folder now present; repo up to date;
   Riley's edit + untracked files untouched.
2. Made **REPULL auto-pull from git** so the list is always current.

## What changed (code)
`DevLauncher.cs`, `LogicAppsForm`:
- New field `gitPullNote` — appended to final REPULL status when a pull is skipped/failed.
- `Repull()` restructured: if the repo path is valid it now runs `GitPull()` on a
  **background thread** (button disabled, "Pulling latest from git…" status), then
  BeginInvokes the folder scan (the old body, renamed `RepullScan()`).
- New `GitPull()` — runs `git pull --ff-only --autostash` (60s). `--ff-only` never
  merges/rewrites; `--autostash` sets aside + restores uncommitted work, so a pull
  **cannot wipe local changes**. Diverged/failed = harmless, note recorded, scan uses
  disk as-is.
- New `RunGit(args, timeoutMs, out outp, out err)` — runs `git` with
  `WorkingDirectory = repoPath` (finds repo root from the `app` subfolder).
- `gitPullNote` appended to all four terminal status lines in `PullConsumption` /
  `ConsumptionDone`.

`CLAUDE.md`: updated the 🧩 REPULL bullet to document the git-pull-first behavior.

## Verified
- Rebuilt `DevLauncher.exe` (csc, clean, no errors) at 09:31.
- Ran the exact command `git pull --ff-only --autostash` from the `app` subfolder:
  "Already up to date", and confirmed `ATTRIBUTION.md` edit survived (autostash safe).

## Decided
- Chose `--autostash` over a hand-rolled stash/pop dance — git's built-in, cleaner,
  and preserves work in the stash even if reapply conflicts.
- Chose `--ff-only` so REPULL never creates merge commits or rewrites history.

## Blocked / open
- None. Riley should still hit REPULL in the launcher once to see
  `FlexPoint_ClickToCallCount` appear (or it auto-pulls on next open).
- Not committed — working tree has the usual `apps.txt`/`favorites.txt`/`recent.txt`
  churn plus this change; commit left to Riley's call.

## Suggested next prompt
"Commit the REPULL git-pull change and push to the launcher repo."
