# Organize mode (2.44)

Organize mode lets everyone in a session select sounds without playing them and put them into categories. In
**My Layout** the changes apply at once, in this browser only. In the **Host Layout** they become an
organization plan: guests may propose changes (when the host allows it), and only the host applies them to
Soundpad, where Soundpad can do them from outside, one verified step at a time.

Gates: `organize` (the plan, Review changes, guest proposals) and `missingFiles` (Needs attention), both
companion 2.44. My Layout organizing needs no companion feature and works with older companions too.

## Using it

- **Organize** on the board toolbar turns the mode on. `#board` gets `data-organize="on"`; every tile becomes
  its own checkbox. Click, Space or Enter toggles; Shift-click selects a range from the last tile clicked;
  Ctrl+A selects every sound shown; Escape (or **Done**) leaves. Nothing plays in Organize mode, and the
  board's Space shortcut steps aside. Panic and search still work.
- Drag the selection onto a category in the sidebar, or use **Add selected to…** (a keyboard menu: arrow keys,
  Enter, Escape closes only the menu). When a category is shown, **Move selected to…**, **Remove from this
  category**, **Remove this category** and **Rename category…** are there too.
- **My Layout**: Add selected to a folder or a new folder, Remove from this folder, and Undo (the last 20
  changes, in memory).
- **Host Layout**: every change lands in **Review changes (N)**, in the editor panel. Steps are grouped by
  category; each says "Soundpad can do this" or "Do this in Soundpad" and why, and who proposed it.

## What Soundpad Web does in Soundpad, and what it guides

| Change | How |
|---|---|
| New category at the top level | Native: `DoAddCategory("name", -1)` |
| Add a library sound to a category (at the end) | Native: `DoAddSound("path", index, -1)`, the index read at that moment |
| Remove an **empty** top-level category without subcategories | Native: `DoRemoveCategory(index)`, only after a re-read shows it empty |
| Remove a sound from a category; move between categories | Guided ("Remove … from …"); a move is the native add plus the guided removal |
| Rename, reorder, reparent, a category inside another, icons | Guided |
| Remove a category that holds sounds or subcategories | Guided: empty it in Soundpad first; the step then becomes native |

A move is never faked by removing and re-importing an entry. Soundpad Web never sends
`DoRemoveSelectedEntries`, `DoSelect*`, `DoScroll*`, `DoSearch`, `DoResetSearch`, `DoSaveSoundlist`,
`DoUndo`/`DoRedo`, recording commands, or anything built from guest text other than validated names.

Guided steps are re-checked on every library change and ticked off when Soundpad Web sees them done (a sound
gone from the category; the renamed or nested category there). A removal that also took the sound out of All
sounds is flagged. Reorder is ticked off by the host (**Mark done**).

## Apply

Apply first writes a checkpoint, then works through the native steps in order. For each step:

1. **The quiet rule** (`QuietRule.TryOnce`, shared with the Temp category removal since 2.44): Soundpad must
   be stopped, stay stopped for a second with nothing played by the companion, and still be stopped. While
   anything plays, Review changes says "Waiting for Soundpad to be quiet". Plays go between steps.
2. In one gate entry (background lane): read Soundpad again, find the step's category again and check its
   precondition. A category is found by its name, its parents' names and a hash of what is in it; failing
   that, the single category with the same name and parents is offered as a candidate the host confirms
   (**Rebind**). Drift elsewhere in Soundpad is fine.
3. The step is written to the plan as `applying` before its one command is sent.
4. A settle loop (20 × 150 ms) checks for exactly the expected structural change: categories compared as a
   multiset of "parents, name, contents", so it does not depend on how Soundpad renumbers them.

Anything else stops Apply: **conflict** (Soundpad changed: Skip, Retry or Rebind), **failed** (refused or not
the expected result: Skip or Retry), or Soundpad unavailable. The plan is paused and kept; nothing retries by
itself. After a restart, a step left `applying` is checked again: done in Soundpad → verified; Soundpad still
as before → pending (Apply tries again); otherwise a conflict.

After each verified step the companion's own Temp and Saved categories are re-found by their tagged names
(Soundpad renumbers categories when one is removed), and every page gets the new library at once.

**The first add-to-category on this Soundpad** measures whether All sounds gained a second entry for the
sound. If it did, Apply pauses after that one step and asks **Continue** (each later add shows the sound once
more in All sounds) or **Stop**. The answer Soundpad gave is remembered in `organize\settings.v1.json`
(`addBehaviour`).

**After a partial apply**, Review changes offers **Remove the empty categories this plan created** (native,
behind the quiet rule, verified) and lists the sounds already added, to remove in Soundpad by hand.

## Guests

With **Guests may propose organization changes** (Settings, off by default), a guest in Organize mode builds a
draft in the Host Layout and sends it with **Propose to the host**. The host sees each proposal in Review
changes and accepts or declines it; a guest can withdraw their own. A guest never applies. Guests send sound
ids and category ids only, never a path, and get ids, names and states, never a path. The companion refuses
guests whose session authorization changed (revoked, or another invitation).

Limits: 200 steps per plan, 50 per proposal, 20 waiting proposals per guest; names 1–80 printable characters
Soundpad can receive on this PC (`SoundpadText.CanSend`), without `"` or control characters.

Refused as targets or sounds: the app's Temp and Saved categories, All sounds, Temp clips, the app's session
files under `managed-audio\` and exact-playback slot files ("Keep it first").

## Needs attention

The companion checks, in the background, whether each library sound's file exists: four at a time, 2 s per
path. A path that does not answer in time is "unknown", never missing. Results are cached per path, rescanned
when the library changes and every 5 minutes, and never on the playback path. A missing sound carries
`missing: true` in the snapshot (written only when true), and the library version changes with the marks.

The board shows such sounds at the end of their category under **Needs attention (N)**, with "Relink or remove
this sound in Soundpad". Their Play says "The file is missing on the host's PC" instead of playing, and the
companion refuses `play`, `startAt` and `region` of them with the same reason: Soundpad's reaction to a
missing file is unknown and may open a dialog while the host is in a game. Guests see the same group and
reason, never the path.

Test and harness Soundpads list made-up paths, so there the scan runs only when a test asks for it
(`Bridge.UseFileExists`) or with the harness's `--missing-fixture`. The real companion always scans.

## Copy into My Layout

A guest's shared layout now opens a preview: which of its folders to copy (a folder brings its parents),
whether favourites, hidden sounds and the order come too, and **Merge** (the default: folders with the same
identity gain the shared sounds, lists combine, the shared order first) or **Replace** (each chosen part is
swapped for the shared one). After Copy, the notice offers **Undo**.

## Files

All in `<data>\organize\`; older companions ignore the folder.

- `plan.v1.json`: `{v:1, id, revision, state: draft|applying|paused|done, baseFingerprint, paused,
  pausedText, steps[], proposals[], measured}`. Paths are kept here only. At most 4 MiB, read strictly
  (unknown fields refused). A damaged file is renamed `plan.v1.corrupt-<utc>.json` and reported; a plan from a
  newer version (`v` ≠ 1) is left exactly as it is and nothing is written over it. Written to `.tmp`,
  flushed, then moved.
- `settings.v1.json`: `{format:1, guestOrganize, addBehaviour}`, at most 4 KiB. Unknown fields are kept and
  written back. A damaged file means the defaults (guests may not propose) and is left untouched until the
  host changes a setting. `guestOrganize` is shown as `Permissions.GuestOrganize`; it is never in
  `state.json`.
- `checkpoints\<planId>.v1.json` (the tree before Apply: sound list and categories as Soundpad gave them) and
  `checkpoints\<planId>.spl.bak` (a read-only copy of Soundpad's `soundlist.spl`, opened with
  `FileShare.ReadWrite`). Both are written once per plan, before its first Apply; a later Apply of the same plan
  (Continue, after a conflict, a removal that had to wait) keeps them. The `.spl.bak` is the list as Soundpad last
  saved it, which can be older than what Soundpad holds in memory; Review changes says so and when it was taken
  (`checkpoint.takenAtUtcMs`). The newest 10 are kept. The copy is never restored by the app and never written into
  Soundpad's folder; its name is not `.spl`, so nothing reads it as a Soundpad file. To restore it, with
  Soundpad closed, copy it over `%APPDATA%\Leppsoft\soundlist.spl`.

The managed-audio journal format is unchanged; following the Temp and Saved categories only updates their
`NativeId`, as before.

## Protocol

All ops carry their fields in `body` and are checked strictly by the companion (unknown fields refused).

| Op | Who | Body |
|---|---|---|
| `organizeGet` | host; guest with `guestOrganize` (own proposals and the plan, no paths) | `{}` |
| `organizeEdit` | host | `{planRevision, add?: StepInput[] (≤50), remove?: stepId[], acceptProposal?, rejectProposal?}` |
| `organizeApply` | host | `{planId, planRevision}` |
| `organizeResolve` | host | `{resolution: skip\|retry\|rebind\|done\|cancel\|continue\|stop\|removeCreated, stepId?, targetCategoryId?, targetCategoryName?}` |
| `organizeDiscard` | host | `{}` (guests' proposals stay) |
| `organizePropose` | guest with `guestOrganize` | `{steps: StepInput[] (1–50)}` |
| `organizeWithdraw` | guest, own proposals | `{proposalId}` |
| `organizeSettings` | host | `{guestOrganize}` |

`StepInput`: `{kind, soundId?, categoryId?, categoryName?, targetStepId?, targetKey?, name?, key?}`; `kind` is
one of `createCategory`, `addSound`, `removeCategory`, `removeFromCategory`, `renameCategory`, `createNested`,
`reorder`. A category id must still carry the given name, or the step is refused ("Refresh and choose it
again"). `key`/`targetKey` let one request create a category and add to it. The snapshot's `organize`
revision rises on every change; pages fetch `organizeGet`.

## Tests and the harness

- xUnit: `OrganizeTests` (plan file rules, every native step verified under both indexing models, empty-only
  removal, refusals and lag, drift and conflict with Rebind, restart reclassification, the quiet rule with a
  play landing between steps, duplicate-on-add, guests, Temp/session/slot/app-category refusals, the
  checkpoint, guided steps, recovery), `CategoryDriftTests`, `MissingFilesTests`.
- Every test's `MockSoundpad` fails on dispose if a forbidden command reached it (`AllowForbiddenCommands`
  opts out). Its new options keep the old behaviour by default: `NestedIndexing` (`Append` | `DepthFirst`),
  `DuplicateOnAdd` (default true, as before), `InsertPositions`, `RemoveCategoryScope` (`EveryOccurrence` |
  `Exclusive`), `ApplyLagReads`, `SoundlistLagReads` (All sounds lags behind the categories after an add, as
  measured), `ExitOnRiskyRemoval`, `BeforeCommand`.
- Harness: `--missing-fixture` (a sound in Reactions whose file does not exist, and the file scan on),
  `--organize-duplicates` (the mock lists an added sound twice; without it the harness's mock does not).
  Verbs: `library-mutate <json>` (`addCategory`, `removeCategory`, `renameCategory`, `addSound`,
  `removeSound`, as a person would in Soundpad), `remove-file <name>` (deletes a fixture file and rescans),
  `reject-prefix <text>` (the mock refuses those commands; empty clears).
- e2e: `tests/e2e/organize.spec.ts` (Chromium and WebKit).

## Measured on the real Soundpad (2026-11-15)

`--real-organize-probe` ran Organize against the owner's Soundpad 4.0.35 three times, only in new categories named
"SPW probe <time>…" holding one-second tones of its own, and left the list exactly as it was. What it found:

- **`DoAddSound(path, index, position)`** is accepted with `-1` (end), `1` (first) and `n` (before the nth entry).
  The entry lands in that category at that place and gets the next index at the end of All sounds.
- **Every `DoAddSound` makes a new All sounds entry**, also for a file that is already there (a second category, or
  the same category again). The first add on a Soundpad therefore always measures `duplicates` and pauses.
- **Soundpad shows a sound in its category a moment (up to about 60 ms) before All sounds lists the new entry.** Apply's verification
  now waits until the category's entry is in All sounds (`NativeTree.Listed`). Before that fix, a read in between
  could record "no second entry" (seen once: the fourth add of the first probe run).
- **Sub-categories are numbered depth first.** `DoAddCategory(name, parent)` makes a sub-category whose index is its
  parent's + 1; every later category moves up by one. `NativeTree`'s indexes match what Soundpad expects: an add
  and a removal with the index read just before reached the sub-category. Organize still creates only top-level
  categories (a sub-category stays a guided step).
- **Removing a category removes exactly its own entries.** The same file in another category stays there and in
  All sounds once per remaining entry. (The test Soundpad's `RemoveCategoryScope` options both differ from this:
  `EveryOccurrence` removes every entry with that path. Organize only removes empty categories, so it does not
  depend on it.)
- **Names with `(`, `)` and `,`** inside the quoted argument are taken as they are ("SPW probe … (empty, x)").
- **Soundpad saves `soundlist.spl` on its own timer: every 5 minutes, and only when the list changed** (writes at
  19:58:19, 20:03:19, 20:13:19 and 20:18:19; none at the 5-minute marks when nothing had changed). So the checkpoint's
  `.spl.bak` can be up to about 5 minutes older than what Soundpad holds, and a change made just before Apply may
  be missing from it. The checkpoint's `.v1.json` (the tree Apply read) is always exact.
- Apply of 7 steps (create, 4 adds, an empty-category removal, one guided rename) took about 2.5 s to the
  duplicates pause and finished after Continue; "Remove the empty categories this plan created" removed the plan's
  empty new category in about 1.2 s (its quiet second included).

Still unknown: Soundpad's reaction to playing a missing file (Organize never sends it).

### The probe

`SoundpadWeb.Harness.exe --real-organize-probe --data-dir <short folder in the temp folder> [--spl-watch-s 60]`
(`companion/harness/RealOrganizeProbe.cs`). It refuses while the companion or VRChat runs or Soundpad is busy, plays
nothing, uses a real Bridge on a throwaway data folder inside `--data-dir` for the plan, and removes its categories
deepest first, each after Stop, 2 s stopped and a re-read, then compares the exact `GetSoundlist` and
`GetCategories` text with the start and waits (up to 330 s) for Soundpad's next save to put `soundlist.spl` back.
Back up `%APPDATA%\Leppsoft` first.
