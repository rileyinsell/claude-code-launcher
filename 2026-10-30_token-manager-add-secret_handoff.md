# 2026-10-30 - Token Manager: Add new secret

## What happened
Riley asked for a way to add new secrets to the Key Vault from the 🔑 Token Manager,
with a verify step after adding.

## What changed
- `DevLauncher.cs` - new **➕ ADD NEW SECRET** section in `SecretGrabberForm` (name box,
  masked value box with 👁, ADD button). `DoAdd()` / `AddDone()` plus helpers `RunAz`
  and `ValidSecretName`. Window grew from 560 to 700 px tall.
- Flow: validate name -> refuse if already in cached list -> confirm dialog -> live
  check the name is free (`SecretNotFound`) -> write via temp `--file` (deleted
  right after) -> read back and compare to the typed value -> green "Verified" or red
  error. ADD never overwrites an existing secret (SET still does that, with its own
  confirm).
- `DevLauncher.exe` rebuilt. `CLAUDE.md` documents the section.
- Commit: see `git log` ("Add 'Add new secret' section with read-back verify to Token Manager").

## Verified
- Clean compile.
- Read-only check against the prod vault: a made-up name returns `SecretNotFound`,
  exactly what the "is the name free" step looks for.
- NOT done: a real end-to-end ADD, because that writes a new secret into the
  production vault. First real use by Riley is the live test.

## Blocked
Nothing.

## Suggested next prompt
"Open the Token Manager, add a throwaway secret, and confirm it shows Verified" - or
"Add a delete button for secrets I added by mistake".
