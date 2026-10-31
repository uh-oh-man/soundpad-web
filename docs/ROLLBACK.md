# Rolling back

Every release stays on GitHub permanently, so any earlier version can be restored by downloading it. Nothing in a newer version deletes or rewrites what an older one saved.

## Known-good points

| Version | What it is | Git tag | Download |
| --- | --- | --- | --- |
| 2.4.0 | Mimic merged in | `v2.4.0` | [Release](https://github.com/uh-oh-man/soundpad-web/releases/tag/v2.4.0) |
| 2.3.1 | Last version **before Mimic** (first Claude-maintained release) | `v2.3.1`, `backup/pre-mimic` | [Release](https://github.com/uh-oh-man/soundpad-web/releases/tag/v2.3.1) |
| 2.3.0 | Last version written by Codex | `v2.3.0` | [Release](https://github.com/uh-oh-man/soundpad-web/releases/tag/v2.3.0) |

`backup/pre-mimic` points at `main` right before the Mimic work began (website 2.3.1 plus a CI-only change).

## Roll back the Windows companion

1. Close the running companion (Enter or Ctrl+C in its window).
2. Download `SoundpadWeb.exe` from the release you want in the table above.
3. Run it as usual.

Your data in `%LOCALAPPDATA%\SoundpadWeb` keeps working:

- 2.4.0 adds one optional setting (`guestMimic`) to `state.json`. Older companions ignore it.
- Mimic clips are ordinary Temp entries, so older companions manage, keep or clean them up like any upload.
- The Mimic buffer is never written to disk, so there is nothing to remove.

## Roll back the website

The live site always serves `main`. To publish an older version from your fork:

```bash
git checkout -b rollback v2.3.1
```

```bash
git push origin rollback:main --force-with-lease
```

The second command replaces `main` on GitHub, so only do it when you mean to. GitHub Pages then redeploys that version. To get Mimic back later, push the newer tag the same way.

You don't need to roll back the website for an older companion to work: the 2.4 website hides Mimic when the companion is older than 2.4, and the rest works with 2.3 companions.

## Run an old version locally without touching GitHub

Download **Source code (zip)** from the release page, or:

```bash
git worktree add ../soundpad-web-2.3.1 v2.3.1
```

Then follow that version's README to build it.

## Voice Replay Studio

The desktop app's original folder outside this repository was not changed by the merge. Its copy in `mimic/` is the V1.1 source as imported in 2.4.0; only a pointer note was added to the top of its README.
