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
  `Exclusive`), `ApplyLagReads`, `ExitOnRiskyRemoval`, `BeforeCommand`.
- Harness: `--missing-fixture` (a sound in Reactions whose file does not exist, and the file scan on),
  `--organize-duplicates` (the mock lists an added sound twice; without it the harness's mock does not).
  Verbs: `library-mutate <json>` (`addCategory`, `removeCategory`, `renameCategory`, `addSound`,
  `removeSound`, as a person would in Soundpad), `remove-file <name>` (deletes a fixture file and rescans),
  `reject-prefix <text>` (the mock refuses those commands; empty clears).
- e2e: `tests/e2e/organize.spec.ts` (Chromium and WebKit).

## Not verified on the real Soundpad

Nothing in 2.44 runs Organize against the real Soundpad. Tested against the test Soundpad only, so unknown:

- how Soundpad numbers categories when one is created inside another (Organize offers that as a guided step);
- whether `DoAddSound` of a path already in All sounds lists it twice (the first such Apply measures it and
  pauses);
- Soundpad's insert-position semantics for `DoAddSound` (Organize always adds at the end);
- what removing a category does to sounds that are also in other categories (Organize removes only empty
  categories);
- Soundpad's reaction to playing a missing file (Organize never sends it);
- category names with parentheses or commas inside a quoted command argument.

### A future opt-in probe (design only, not built)

A `--real-organize-probe` harness mode could answer the questions above on the owner's Soundpad, following
the repo's real-Soundpad test permissions: back up `%APPDATA%\Leppsoft\soundlist.spl` and the companion's
data first; work only in two new top-level categories with a random tag in their names; create a category,
add one existing library sound to it at the end, read whether All sounds gained an entry, try names with
`(`, `)` and `,`, create a nested category by hand and read the numbering; then remove both tagged categories
behind the quiet rule (each empty first) and verify the library is exactly as before. It must never play a
missing file, never remove a category with sounds, and stop at the first unexpected result. No code path for
this exists in 2.44.
