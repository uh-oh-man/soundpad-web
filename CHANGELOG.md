# Changelog

Notable changes to Soundpad Web. Versions follow the website's `package.json`; the Windows companion is versioned separately and only needs updating when a release says so.

## 2.45.0 — Feedback, and fixes from the real Soundpad

A **Feedback** button for you and your guests, no more storage limits for guests, and two fixes found by testing on a real Soundpad. **Update the companion** for the Feedback button and for the limits to go; with an older companion the button is hidden, the old limits apply and everything else works as in 2.44.

### Added

- **Feedback.** The header has a **Feedback** button for the host and every guest. Choose **Bug**, **Idea** or **Opinion**, write a note and press **Send** (or Ctrl+Enter). The host's companion adds each note, with who sent it, when, and which part of the app they were in, to one file on the host's desktop that always has the same name: **`Soundpad Web feedback.md`**. Notes are only ever added to the end of that file. Guests are never told where it is, can send up to 10 notes in 10 minutes, and a note can be up to 4000 characters. If the file grows past 8 MB it stops taking notes until you move or rename it.

### Changed

- **Guests have no storage limit.** Their clips, copies, uploads and kept clips never wait for you any more, and the Temp and kept-audio allowances are gone from **Session & access → Keep and guest storage**. You still see how much each guest uses. Your saved allowance settings are left as they were, so going back to an older companion brings the old limits back.

### Fixed

- **Exact cue starts and regions work from the start of a session on the real Soundpad.** Soundpad lists the first file added to a new category about 100 ms after it accepts it, and the companion checked only once, straight away. So in a new session the slot files behind exact cue starts never registered (every cue start and region played the older way, start then seek), and the first upload or copy into a new Temp category could be left "not verified". The companion now waits up to 2 seconds for Soundpad to list the file. Found by the first run of the exact-playback check on the owner's Soundpad 4.0.35; after the fix, 17 of 18 measured cue starts and regions played exactly the wanted audio with nothing before or after it.
- **Organize asks about duplicates in All sounds reliably.** Soundpad shows a sound in its category a moment before it adds the new entry to All sounds, and Apply could check in between. It then recorded "no second entry" for that sound, and for the first add on a Soundpad it would have remembered "Soundpad doesn't list sounds twice" and never asked. Apply now waits until All sounds lists the entry. Found by the first run of Organize on the owner's Soundpad 4.0.35, which also showed that Soundpad lists a sound once more in All sounds every time it is added to a category.

### Tests

- 3 new companion tests for Feedback: the host's and a guest's notes land in the one file with a fixed name, a guest never gets the path, empty, unknown, too long and too many notes are refused, and a full file stops growing. The browser-test harness writes notes to its own temporary folder, never to the desktop.
- New real-Soundpad checks (harness, opt-in, run on the owner's PC with everything restored afterwards): import timing, responsiveness during slow work, session cleanup, Organize on the real Soundpad and Mimic capture from Soundpad's own output.
- 1 new companion test: guests are never paused or asked to wait, whatever allowances were saved; the tests of the old limits switch them back on for themselves. The browser test for guest storage now checks that a guest's clips never wait.
- The exact-playback tests no longer fail on a busy machine (CI failure on 3612520). Their setup checks that the slot files are not registered while the first sound still plays, but that sound had only 0.6 to 2.9 s left, and on a slow run it ended first, so the companion rightly registered the slots and the check failed. The first sound now plays until the test stops it, and the setup checks that it is still playing. The companion itself did nothing wrong and is unchanged.

## 2.44.0 — Backups, updates, Organize and setup

Backups, updates from inside the app, Organize mode, a guided setup and your own shortcuts. **Update the companion** for all of them except **Settings → Keyboard**, Organize mode in **My Layout** and the **Copy into My Layout** preview: with an older companion the other parts are hidden, and everything else works as in 2.43.

### Added

- **Backups.** Choose a folder in **Settings → Backups** (or **Use suggested folder**, `Documents\Soundpad Web Backups`) and the companion backs up your kept sounds, voice library, sentences, statistics, icons, settings, and the companion window's and your browser's layouts. It does this once a day and 10 minutes after a change, keeping the newest 14 backups, everything from the last 30 days and one a week before that (all adjustable). Later backups copy only what changed. **Back up now** makes one at once. Backups are off until you choose a folder, and they contain your companion's identity: keep the folder private.
- **Restore from a backup.** Open a backup to restore a single kept clip, recording, voice or sentence, a layout (merge or replace), or the whole library. A **Before restore** backup is made first. Restored recordings, voices and sentences can be undone from the notice. A restored clip goes back to its old place only; a different file already there is reported, never overwritten, and a clip Soundpad no longer lists is not added to Soundpad by itself (you are told which file to add again). Restoring the whole library asks first, needs playback stopped and is refused while a Keep is being finished (try again when it is done).
- **If local state ever becomes unreadable**, the companion's message now says where the copy in your newest backup is, and `SoundpadWeb.exe --restore-state "<backup>"` puts it back while the companion is closed (the replaced file is kept beside it).
- **Updates from inside the app.** At launch the companion offers a newer release: **Update now**, **Later** or **What's new**. After **Later**, **Update** stays in the header and in Settings, with **Check again** and a setting to stop checking at launch. The update comes from this project's GitHub releases and is checked against the size and SHA-256 hash published with it, and the new version is test-run before it replaces the old one. A backup is made first when a backup folder is set. The companion then closes and opens again by itself and continues the same session: Temp, drafts, history, the invitation and guests' admission stay, guests reconnect on their own, recording continues on the same output and the timeline marks the gap. Sounds already playing in Soundpad are not stopped. If the new version doesn't start, the previous one comes back, and `SoundpadWeb.previous.exe` stays beside it. An ordinary launch still gets a new invitation and never starts recording.
- **Organize mode.** Press **Organize** on the board, then tick sounds (Shift-click a range, Ctrl+A for all shown) without playing anything, and drag them onto a category or use **Add selected to…**. Esc leaves Organize mode. In **My Layout** changes apply at once, with Undo. In the **Host Layout** they collect in **Review changes**: guests can propose (if you allow it in Settings: **Guests may propose organization changes**, off by default), and only you apply. Soundpad Web makes the changes Soundpad allows from outside (a new category, adding sounds to a category, removing an empty category), waits until Soundpad is quiet, checks each result and pauses instead of guessing if Soundpad changed meanwhile. Anything Soundpad can't do from outside (removing a sound from a category, moving, reordering, renaming) is explained as a step to do in Soundpad and ticked off when Soundpad Web sees it done. The first time a sound is added to a category, Soundpad Web checks whether Soundpad listed it twice and asks you before going on.
- **Needs attention.** Sounds whose file is missing on the host's PC are grouped at the end of their category with how to fix them in Soundpad, and are not sent to Soundpad to play.
- **Copy into My Layout** from a guest's shared layout now shows a preview where you choose what to copy (folders, favourites, hidden sounds, order) and whether to merge (the default) or replace, with Undo.
- **Guided setup** for a new host: it checks Soundpad and says exactly what to fix (not running, not answering, started as administrator, too old), lets you remember a capture output without recording anything, explains invitations and permissions, and offers shortcuts. An existing install gets a small **Check your setup** card instead. Run it again from **Settings → Setup**. Soundpad Web still never starts with Windows.
- **Your own keyboard shortcuts.** **Settings → Keyboard** lets you change or turn off Search (`/`), Play/Pause (Space), Panic (Shift+Escape) and a new **Stop** shortcut, in each browser. Keys the browser or other parts of the app need are refused with the reason.
- **Host-wide hotkeys** (off until you set them, in **Settings → Setup**): a **Capture** key that starts or stops listening on your remembered output, a **Panic** key and optionally **Stop playback**, working in any app on your PC while the companion runs. A key another app already uses is reported and your previous key keeps working. F6 "save the last N seconds" is unchanged and can't share a key with them.

### Changed

- The connection banner names the exact Soundpad problem and its fix, with **Retry** (companion 2.44).
- An update is refused while a whole library is being restored or an organization plan is being applied, and those are refused while an update installs (try again when it has finished).

### Fixed

- After a Soundpad category above the companion's Temp or Saved category was removed, adding to Temp failed and the Temp category could be left behind at the end of the session. Two categories with exactly the Temp category's tagged name are now refused instead of using whichever sits at the old position.
- An elevated Soundpad (started as administrator) left a stale "Waiting for Soundpad" message; it now says the two run with different rights.
- Starting the companion twice now says it is already running instead of showing a raw file error.
- Guests no longer receive the host's F6 status line.

### Reviewed

Four reviews before release (Organize and Soundpad safety, backup and update data safety, the new screens' keyboard use, release readiness) found these, all fixed:

- **Restoring from an old backup could delete that backup.** The "Before restore" backup made first could push the backup being restored out of the ones kept, and with it the only copy of a deleted clip, so the restore then said the clip was missing. The backup being restored from is now kept until the restore has finished.
- **Update, whole-library restore and Organize never overlap.** Each checked the others only before its own "before" backup, so in that time an update could start under a restore or an Apply. Each now marks itself busy before that backup, and the others are refused until it is done. Editing the plan is refused while Apply is starting, and the downloaded update is checked against its published hash again right before it replaces the old version.
- **Organize recovery.** A change Soundpad stopped answering during could stay "Applying…" for good when Soundpad came back with nothing changed, and a later Apply then marked the plan done without it. It is now checked against Soundpad as soon as Soundpad answers, and Apply never goes past a change whose result is unknown. Editing the plan no longer answers the "listed twice in All sounds" question for you. **Remove the empty categories this plan created** stops waiting after two minutes of Soundpad playing and says what it kept, and is refused during an update or a restore like Apply.
- **The plan keeps only what was accepted.** A change refused for the 200-change limit stayed in the plan and was saved with the next change, and many guests' proposals could block the host from editing or discarding the plan. Refused changes now leave the plan as it was, and proposals stop at 200 in all.
- **A stale category is refused.** After Soundpad renumbered its categories, a change could land on a different category with the same name elsewhere in the tree; the page now sends each category's parents, and the companion checks them.
- **The copy of Soundpad's sound list kept before a plan** was replaced by every later Apply of the same plan, so it no longer showed the library from before the plan. The first copy is kept, and Review changes says when it was taken and that it is the list as Soundpad last saved it.
- **Backups.** Retention that was cut short (a crash, a file held by an antivirus) could leave a backup without its manifest, after which old files were never removed again; a backup is now renamed before it is deleted. A scheduled backup that failed (the drive was missing) is tried again after an hour instead of the next day. A whole-library restore no longer writes a damaged settings file from the backup over a good one, and a restore only ever writes audio files of the backup copy's type. The hint shown for an unreadable `state.json` only points at this install's own backups.
- **Keyboard.** Leaving Organize mode (Esc or **Done**), removing a step, **Discard**, **Accept**, **Decline**, **Withdraw**, **Later** on the update offer, a restore and its confirmation, and closing the setup guide no longer drop focus to the page (where Space paused the sound and Escape closed the panel): focus goes to the Organize toggle, the next step, the panel heading, the **Update** chip, the restore button or the setup card. **Turn off**, **Reset**, the host-wide **Off** and the guide's **Back** stay focusable when they have nothing to do. Review changes' **Apply** bar and the Organize bar never cover the focused step or tile, and on a phone the Organize bar stays below the header instead of under it.
- **Words.** The example says it doesn't change Soundpad categories (it said a companion update was needed), a guest whose host's companion is too old is told so (not that proposals aren't allowed), and **Select all** and Ctrl+A take only the sounds that have tiles, not those in Needs attention. Starting the companion twice no longer mentions a tray icon it doesn't have.

### Tests

- About 100 new companion tests: backups and restore (hashes, copying only what changed, edits during a backup, a crash mid-backup, retention, clean-up that never touches other files, folder rules, low disk space, damaged settings, never delaying Play or Stop, every restore kind with Undo, identity and limits, the offline restore), updates (release choice and fallbacks, the allowed download hosts, hash and version checks, the encrypted hand-over, every failure of the swap rolling back, and a full session continuing across an update), Organize (the plan file, each native step verified, drift and conflicts, a crash mid-step, the quiet rule with plays in between, duplicates on add, guests' proposals, refused targets, never sending forbidden commands), missing files, the Temp-category renumbering fix, setup, Soundpad diagnosis and host-wide hotkeys. No test registers a real Windows hotkey or opens a microphone.
- About 40 new website unit tests (the backups, updates, Organize, shortcuts and setup modules) and 25 new browser tests, run in Chromium and WebKit: backups and restore with Undo, an update with a guest connected (same invitation, history and Temp, the gap marked), Organize mode with guest proposals, Apply, pauses, recovery and Needs attention, the setup guide, the Soundpad fix, rebinding Panic and the host-wide hotkeys, and each feature hidden for a 2.43 companion.
- A harness round trip backs up a fixture library, deletes and damages it, restores it and compares every byte; it also runs on CI. A process-level script (`scripts/update-swap-test.ps1`) updates a real published EXE from a local test release server and checks that a wrong hash, a cut-off download, a wrong version, a failed test run or a version that never starts each leave the old EXE working with the same port and invitation.
- The new files (`backup-settings.v1.json`, `backup-workspace.v1.json`, `update-settings.v1.json`, `organize\`, `setup.json`, `hotkeys.json`) were checked against the 2.3.1 and 2.43.0 companions (see `docs/ROLLBACK.md`).

### Not yet checked on a real Soundpad or real hardware

- Organize: nested categories, whether adding a sound already in Soundpad lists it twice in All sounds (the first such change pauses to check), removing categories whose sounds are also elsewhere, names with brackets or commas, and Soundpad's reaction to a missing file were tested against the test Soundpad only. Nothing in this release was run against the owner's Soundpad.
- Updates were tested end to end with local test releases, not real GitHub downloads. The EXE is not code-signed, so the check proves the file matches what was published with the release, not who built it. Windows SmartScreen or Smart App Control may still block a new version (Soundpad Web then keeps the current one). Recording continuity on a real output, guest reconnection over the internet and the length of the gap on real hardware are not measured.
- Host-wide hotkeys inside games and fullscreen apps, and the elevated-Soundpad message, were not tried on real hardware.
- Backups to network, removable or cloud-synced drives, restores on another PC or after reinstalling Windows, and how long a backup of a large real library takes were not tried.

## 2.43.0 — Blue studio

A new look and layout. **Update the companion** for three parts of it: the Kick session count, edited and speed copies of any Soundpad sound, and sharing your style between your browser and the companion window. With an older companion those three are hidden or say what they need, and everything else works.

### Added

- **Sections.** **Soundboard**, **Mimic**, **Voices** and **Sentences** are listed in the sidebar, or as tabs along the top (Settings → Appearance → **Navigation**). Voices and Sentences are now full views instead of windows, and each remembers its tab, the recording or sentence you had open and where you had scrolled.
- **The editor panel.** Mimic, the copy editor, a sound's editor and **Activity** open in a panel on the right, beside a board that stays playable. Drag its edge, or use the arrow keys on it, to change its width; the width is remembered. Under 1280 px the sidebar folds to icons while the panel is open. On a phone the panel is a sheet over the board, with **Show board** to fold it to a bar and **Show editor** to bring it back.
- **A sound's editor.** Every tile has **Open editor for …**, and the player has **Details for …**. The editor shows the sound's waveform with its cues and regions, makes effect and speed copies, and has its icon and statistics. Copies can start from any Soundpad sound, not only from Temp audio (companion 2.43; an older companion shows a one-line note instead).
- **Quick Reverse and Speed** on a tile, shown on hover or keyboard focus: a Temp copy at 0.5× to 2× (or reversed) that plays as soon as it is ready.
- **Kick session count.** Kick shows "+N this session" beside its lifetime count (companion 2.43; the session count is kept in memory only, so a restart starts a new session). **Decrease** and **Reset** moved behind **Kick counter details**, and `+` and `-` work on the focused Kick.
- **The header ribbon.** An animated ribbon in the header follows the sound that is playing. It stands still under reduced motion and has its own setting (**Header animation**: On, Still or Off).
- **Activity** keeps each notice, with its **Undo** or **Jump**, for a minute after it leaves the screen.
- **Appearance settings**: **Motion** (System or Reduce), **Contrast** (Standard or High), **Header animation** and optional subtle **Notice sounds**.
- **Your style follows you** (companion 2.43): as host, your browser and the companion window share navigation, theme, motion, contrast, header animation and the Mimic draft you have open. The companion keeps them in a new file, `ui.json`. Guests keep their own choices. Notice sounds, the panel width and Spotify's expanded row stay per browser.
- **Skip links** at the top of the page (to the sounds, the player, and the editor while it is open), and a **capture chip** in the header while Mimic records.
- **The example** now has Mimic's recording history with drafts, **Preview** and **Play into game**, a simulated guest's selection, and simulated voices and sentences. It says plainly what is simulated and what leaving clears. Controls that only work on the host's PC are marked **Host's PC only** and explain why when pressed. Guest names read "(simulated)" once.

### Changed

- **Blue studio** is the new default look: midnight navy, soft glass, electric-blue focus and playback, red Stop and amber Panic. **Original Purple** and **Ocean** remain. Focus is a clear 2 px ring, scrollbars are thin and visible, and no text is smaller than 11 px. The system's More contrast, Reduce transparency and forced colours are honoured.
- **DM Sans** is the app's typeface. It is part of the website itself (nothing is loaded from a font service), under the SIL Open Font License; see `THIRD-PARTY-NOTICES.md`.
- **Tiles** show **Queued**, **Playing** and **Held** with text as well as colour. The board's toolbar fits on one row, so at 1440×900 the first tiles start about 170 px higher.
- **The player** is one compact bar with filled **Stop** and **Panic**, which stay visible at every width.
- **Spotify** is one compact row in the sidebar; **More Spotify controls** shows Previous and the progress, and the choice is remembered.
- **Notices** sit in a strip under the board (above the player on a phone) and never cover a control. Screen readers hear each notice once.
- **Dialogs** make the page behind them unreachable by mouse, keyboard and screen readers.
- **Record performance keeps every tap.** In the palette, a tap made while the previous tap's preview was still being made could be lost, either for a newer tap or because **Stop recording** was pressed quickly. Each tap now waits its turn, and Stop waits until the companion has every tap the page counted.
- **Sentences** shows the sentence that was open when you come back to it.
- The three old stylesheets are now one, and the rules the new look overrides are gone. Two temporary comparisons of exact screenshots and computed styles (at many widths, in every view, theme, contrast and motion setting, under keyboard focus and hover) found no difference from either step; as in 2.9.2 they were not kept.

### Reviewed

A review of the redesign before release found 24 problems, and a second review 4 more, all fixed:

- **Keyboard.** **Skip to player** went to **Skip to sounds** instead of the player. **Show board** on a phone, and running a notice's action in **Activity** (or that action running out), dropped the keyboard to the page, where the next Space paused the sound and Escape closed the editor; the button now stays focused as **Show editor**, and Activity keeps focus on the notice's row. With two dialogs open (the companion's quit prompt over **Session & access**), Escape closed the one underneath; now only the top one closes and the keyboard stays in the other. An editor brought back after a reload or a reconnect no longer takes focus into its heading.
- **Nothing focused hides under the notices or the editor bar on a phone.** In the editor sheet, Tab keeps controls above the notices, and a press on a notice's text (not its buttons) reaches the sheet under it, as at a dialog's foot. With the editor folded to its bar, the page keeps room for the bar too. The example's banner, which the sheet covers, can no longer be reached by Tab while the sheet is shown.
- **Phone navigation.** **Soundboard**, **Voices** and **Sentences** fold an open editor sheet to its bar, so the chosen view shows and can be used.
- **Screen readers.** Error notices are no longer alerts as well as being announced, so an error is said once, not again whenever a dialog opens or closes. On a phone the capture chip still says "Capturing · <source>" to screen readers.
- **Companion window.** With an editor docked under 1280 px, Spotify keeps **Play or pause** and **Next** in the folded sidebar.
- **Host style sync.** A browser that syncs for the first time keeps its own earlier theme and navigation where the companion still has the defaults, so the page that happened to connect first cannot reset the other one's choices. A choice made while a page was saving or reading the shared style is no longer lost or reverted.
- **Leaving the example or a session** drops its Temp list, so the next session never shows or sends the old sources. The sound editor asks for **Statistics** only once they are opened, and not on an older companion. A Soundpad file that cannot be read as a copy source now says "That sound file could not be read." instead of a general failure, and a `ui.json` with a key written twice reads as the defaults instead of stopping style sync.
- **Look.** The header ribbon's strands move as one layered wave instead of crossing in a mesh. The capture chip ends in an ellipsis. The Voices search is one field, like the board's, with space under it; the **New sentence** field takes the row's room. The example's compact Spotify row shows its name, **Host's PC only** and the reason in full, without transport buttons that did nothing.
- **The example reports companion 2.43**: Speed and effects work on any example sound, and Kick shows the session count, as on a host.
- **Focus under the header or a dialog's title.** At 1000 px wide and below, the header stays in view, and Shift+Tab up the board, Voices or Sentences hid **Search**, the tiles' buttons and the Spotify switch under it. In a dialog such as **Session & access**, Shift+Tab hid the invite, **Theme** and other controls under the dialog's title row. Both now keep the focused control just below them.
- **Escape in an editor field** (the copy form's **Name**, say) closed the editor and lost what was typed. Now the first Escape only leaves the field, and a second closes the editor.
- **Settings → Appearance** said only your browser's appearance changes. A host on companion 2.43 is now told that this browser and the companion window share these choices, except **Notice sounds**.

### Tests

- 60 new browser tests: the sections and the editor panel at desktop, companion-window and phone widths (with the phone sheet, its bar and Tab order), notices never covering a control at four widths, skip links and dialogs, tiles, quick actions, Kick, Spotify and the ribbon, the sound's editor on companion 2.43 and on 2.42, host style sync between two pages, the example's history, voices and sentences, and both reviews' fixes (focus under the phone header and a dialog's title, Escape in an editor field). The palette's performance test now taps twice in quick succession and stops at once (it failed 1 run in 3 before the fix).
- About 55 new website unit tests (the ribbon, the playhead, notices, the panel's width, host style sync, the colour tokens' contrast, and the example's history, drafts, voices and sentences, with a check that the example answers every command the website sends) and 20 new companion tests (`ui.json`: saving, reading, refusing bad input and guests, keeping unknown keys, damaged files; the Kick session count; copies from a Soundpad sound, including one that cannot be read).
- The 2.41 and 2.42 test fixes (no public relays in browser tests, failure notes from every page, the phone focus tests) carry over to the new layout; the phone test for focus behind the notices now uses a 667 px screen, where the copy editor's sheet is short enough for **Save copy** to start under the notices.
- `ui.json` was checked against the 2.3.1 companion (see `docs/ROLLBACK.md`): it starts normally and leaves the file alone.
- The companion test for seeking inside a slice and then resuming it failed once on CI: its slice lasted only half a second, and on a slow runner the test's one status check came just as the slice ended. Its sound is now 4 seconds long, so the check comes well before the end. The companion itself was not at fault and is unchanged: its status checks are timed to catch a slice shortly before it ends, and 2.42.0 behaves the same.
- Not checked yet: by ear or with a screen reader. NVDA hearing each notice once, and the look of the phone ribbon and of Spotify's **Host's PC only** row, are still to be checked by hand.

## 2.42.0 — Sentences from recorded words

**Update the companion** for this one. With an older companion, **Sentences** and the pinned sentence tiles are hidden, and everything else works as in 2.41.

### Added

- **Sentences.** A new **Sentences** button beside **Voices** opens a builder: type a sentence, and the companion puts it together from words and phrases said in your voice library's recordings, laid out on a timeline under the text. Typing plays nothing. Every word comes from a real recording; nothing is synthesised, imitated or cloned. A word no recording has stays a **missing word**, marked in the text and on the timeline, and plays as a gap or, if you choose, a bleep. Similar recorded words, and splices of the start of one recorded word with the end of another, are offered for a missing word but used only when you pick one, and stay marked when you do. **Find missing again** looks again after you have recorded more; a saved sentence never changes by itself.
- **Smooth to Chaotic.** Under **Mix**, choose the voices to use and how to mix them: Smooth prefers long recorded phrases in one voice, Chaotic switches voices and uses shorter pieces. The same settings give the same sentence every time. Changing the text chooses again only for the words you changed. **Swap take** picks another recording of a word (each can be previewed first), **Shuffle voices** mixes up a selection or the whole sentence, **Try contrasting deliveries** is suggested for unsettling lines, and **Use altered takes** also allows reversed, sped-up or effect copies.
- **Shape it on the timeline.** Drag pieces anywhere (Shift snaps to a neighbour); moving one moves nothing else, and overlaps are shown striped. Set each piece's edges, fades and volume (balanced by default), add or lengthen pauses, and apply reverse, speed, keep-pitch speed or echo to the selection or the whole sentence. Choose **Fluent joins** or **Original timing** (the pauses the words had when they were said), with **Compare**, and **Gentle cleanup** of noise between syllables, with Original and Cleaned previews. From the keyboard: Left and Right move between pieces, Space selects (Shift+Space adds), Enter previews, Alt+Left and Alt+Right nudge by 10 ms (100 ms with Shift), Delete leaves a pause of the same length, and Escape clears the selection, or closes the window when nothing is selected.
- **Preview here and Play into game.** **Preview here** plays the sentence in your own browser only. **Play into game** plays it through Soundpad as it was when you pressed it, from exactly its first sample; the newest click, Stop and Panic win as for any sound, and the player names it by its title or text.
- **Keep, versions and pinned tiles.** **Keep** saves the sentence's audio as version 1, and **Keep new version** adds the next (up to 50); **Restore** brings an older version back to edit. A kept version always plays exactly as it was kept, even after its recordings are edited or removed, and the sentence says which pieces' recordings have changed since. Kept sentences are listed for everyone and can be pinned with **Pin to board**: their tiles on the board play the latest kept version into the game.
- **The speaking palette.** The **Palette** tab lists the recorded words and phrases in the voice library, the best takes of each voice first, with a search. A tap plays exactly that piece, privately (**Preview here**) or into the game (**Play into game**). **Name…** marks a nonverbal sound on a recording, such as a laugh, a gasp or a breath, so it appears in the palette and can be typed into a sentence as `[laugh]`.
- **Record performance.** Record a run of your own palette taps with their timing (up to 10 minutes or 400 taps), then **Open the take as a sentence** to edit it like any other.
- **Performance sets.** Everyone has their own sets of voices, palette favourites and templates (sentences with `{slots}` to fill in), and picks which set is active. Sets refer to recordings in the library and hold no audio of their own; a guest's sets follow them across reloads.
- **Sentences for guests.** Guests can read, search and preview sentences, and play kept ones into the game with remote playback. Building sentences, naming pieces and recording performances follow **Edit the voice library**, and Keep also follows **Keep clips** and the guest's Keep budget, all including the host's per-guest **Off**. A palette word or a sentence put together from words is part of a recording, like a region, so playing one into the game also needs **Seeking & pause**; kept versions and pinned tiles need only remote playback. A guest's unkept sentences are theirs alone, and their edits not kept yet stay private: others see the kept version. A kept sentence can be changed only by its maker or the host, and only the host can remove one. Guests may make up to 240 sentence changes a minute; the host is never limited.

### Changed

- **Voices' search also finds kept sentences**, by title or text, with a **Sentence** badge.
- **A guest's Keeps are checked together.** Keeps a guest makes at the same moment, of clips or of sentences, can no longer go past their allowance between them, and a Keep that fails leaves no audio file behind.
- **Sentences never hold up Stop and Panic.** Sentences and palette words are put together on the low-priority audio worker, one at a time for the game, and a newer play, Stop or Panic makes an older one give up. A sentence longer than 2 minutes is refused before anything is put together. A guest's sentence or palette word does not play if, while it was being prepared, the host removed the guest, turned remote playback or seeking off, or pressed Panic.
- **The exact-start slots are also used for sentences**, even with exact starts off. The companion adds them to its Temp category at a quiet moment, waiting up to 2 s while Soundpad plays, and otherwise Play says it is getting ready. If they cannot be added, Play says so at once and the companion waits a while before trying again, instead of waiting 2 s on every Play.
- **Room for the host.** Guests leave 400 of the 2000 places in the sentence list and 1000 of the named pieces to the host, and may fill only three quarters of the sentences file. Guests' unkept sentences that nobody has touched for 14 days are removed.
- A brief refusal or reconnect no longer empties the sentence list, the builder or the pinned tiles; they keep what they showed and try again. Quick clicks on favourites, voices and templates no longer undo each other.
- **Keep**, **Play into game**, **Preview here** and going back to the list include text typed a moment before.
- **In Safari, a clicked timeline piece takes the keyboard.** Safari does not put the focus on a button you click, so after clicking a piece, Delete, the arrow keys, Space and Alt+arrows did nothing, and Escape closed the window instead of clearing the selection. Clicking or dragging a piece now gives it the keyboard, as in other browsers, without showing a focus ring. A check of the other new windows (palette, performances, voice library, review queue and Mimic) found nothing else that depends on a click focusing a button.
- A notice's **Jump** or **Open** closes Sentences first, as it does the other windows.
- Sentences are saved in `%LOCALAPPDATA%\SoundpadWeb\library\` (where kept sounds go by default): one file for the sentences, named pieces and performance sets (with a backup copy, and never saved larger than the companion can load), and one audio file for each kept version in `library\sentences\`. `state.json` and Temp's records are unchanged.

### Reviewed

A review of sentences and the palette before release found 27 problems, all fixed: putting a sentence together for the game ran outside the low-priority audio worker, several at a time, and its length was checked only afterwards; sentence and palette commands and their saves ran on the companion's connection and could hold up everything behind them; a guest's sentence could still play after the host removed them, turned remote playback or seeking off, or pressed Panic while it was being prepared; guests with seeking off could play palette words and sentences built from words into the game; two Keeps at the same moment could overspend a guest's allowance, and a Keep that failed left its audio file behind; guests had no limit on sentence changes, and their unkept sentences could fill the list, the named pieces and the sentences file for good; that file could be saved larger than the companion loads; a long performance could not be saved; edits not kept yet were shown to others; the Sentences window ignored a guest's own **Off** settings; a guest listing sentences before the library had loaded got an error; a failed slot setup was tried again, with a 2 s wait, on every Play; and ten smaller ones in the window (text typed just before Keep, Play or Preview was left out; a lone timeline piece could not be selected from the keyboard; quick Alt+arrow nudges and quick list changes undid each other; the volume slider ignored the keyboard; removing a pause lost the focus; Escape on a piece did not close the window; a brief failure emptied the list and the builder; the player called every sentence "kept sentence"; and a notice's Jump opened a window over Sentences). Left as designed: a guest whose library editing is off can still change their own performance sets.

### Tests

- The test that holds the voice library file the way a virus scanner does now lets go from its own thread, so a busy
  runner can no longer hold it past the save's retry window.
- 25 new companion tests (matching that keeps the unchanged words when the text changes, Smooth to Chaotic repeatable per setting, missing words never filled in by themselves, gaps and bleeps, overlaps, that sentences are made only from library recordings and the companion contains no speech engine, the sentences file and its backup, Play into game from a slot with Panic and permissions, Keep and versions that stay exact when their recordings change, performances and sets, and the review's fixes), 16 new website unit tests (the builder's selection, keyboard and timeline rules, missing-word markers with reduced motion, guest permissions including per-guest Off and seeking, and the list keeping its place after a failure), and 3 new browser tests (build, swap, bleep, play into the game, keep and pin a sentence; a recorded performance opened as a sentence; and the review's keyboard, focus, player name and typed-just-before-Keep fixes). That last test also checks that a clicked piece takes Delete, the arrow keys and Escape, which failed in Safari's engine (WebKit) before the fix.
- Not checked yet: nothing was listened to. The tests use the test Soundpad and stand-in recordings, so how joins, balancing, cleanup, bleeps and effects sound on real voices, and sentences played on the real Soundpad, are still to be checked.

## 2.41.0 — The voice library

**Update the companion** for this one. With an older companion, **Voices** is hidden, and Mimic's **Discard** after a crash warns that it removes the whole recording history. Guests' browser keys, the new place for notices, scrubbing and word clicks are in the website and work with any companion.

### Added

- **The voice library.** A new **Voices** button beside **Mimic** opens every clip you kept, as a recording with where it came from and the words said in it. Clips kept from Temp, a draft's new **Keep in voice library**, a guest's Keep and the quit prompt's **Keep selected** all go in. Words come from Mimic's subtitles when the clip came from the history; otherwise the companion finds them in the background, at the Gaming setting. Sounds kept before this release are added by hand with **Add to voice library** in Temp; nothing is added by itself. Removing a recording from the library never deletes its file.
- **Titles, tags, favourites and voices.** Give a recording a title, tags, a favourite star and a **voice** (a name, colour and initials for who it sounds like), and correct its text without losing the word timings. Select several recordings to set their voice or tags at once. Every change has **Undo** on its notice, and the library keeps a list of who changed what.
- **Find any word.** One search finds a word or an exact phrase across your Soundpad sounds and voice clips, with a badge saying which is which. Click a word to hear just that word: **Preview here** in your own browser, or **Play into game**. **Words** lists every word said, for one voice or all of them.
- **Wanted words.** Note a word or phrase you want a voice to say. Voices shows whether you already have it, and when Mimic hears it, people allowed to use Mimic get a quiet "possible match" notice with **Jump** to that moment.
- **Search the recording.** In Mimic, **Find words said in this recording** searches the history's subtitles, within what you may see.
- **Word index of Soundpad categories** (host). In **Word index**, choose categories whose sounds should be found by their words. The companion works through them one file at a time on this PC, can be paused, carries on where it stopped and skips files that have not changed. It only reads your files.
- **Review queue.** **Review** shows new recordings one at a time to sort from the keyboard: Space plays, 1–9 picks a voice, F favourites, Enter keeps and moves on, S or Right skips, Left goes back.
- **Group speakers (experimental)**, off by default (host, in **Word index**). Mimic groups the history's phrases by who seems to be speaking, and leaves a phrase **Unassigned** when unsure. A person names a group or links it to a voice; nothing is named automatically. Measured on synthetic speech only so far.
- **Guests are known by their browser.** Each guest's browser now keeps a private key, and the host's page checks it on every connection. A reload, a reconnect, or a switch between a direct and a relayed connection keeps a guest's place, permissions, history start, drafts and Keep budget, and nobody can take those over by copying another guest's name or profile. **Settings → Connection details** has a **This browser** line saying whether the host will know you after a reload, and a browser that cannot keep the key says so once.
- **New permission: Edit the voice library** (on by default, and can be turned off per guest). Without it, guests can look, search and listen but change nothing. Guests may make up to 60 library changes a minute; the host is never limited.

### Changed

- **Discard after a crash keeps what is new.** Mimic's **Discard** on the "Recovered … of history" notice now removes only the audio the crash recovered. Everything recorded since the companion started again stays, with its subtitles and drafts, and the confirmation says so. Words still being recognised for the discarded part are never added back.
- **Stop and Panic stay prompt during slow work.** Making an edited copy (including keep-pitch speed changes), a preview or a Mimic clip no longer holds up Play, Stop or Panic pressed meanwhile. In a test with a long keep-pitch copy and speech recognition running, Stop's slowest answer fell from 0.7 s to 37 ms and Panic's from 0.27 s to 36 ms. A copy "and play" that someone overtook is still saved, and never plays over the newer sound. Long background transcription also lets live subtitles in between its pieces.
- **Notices sit bottom right, above the player**, clear of Settings, Leave example, Mimic, Stop and Panic. In an open window they appear at its foot, never over its Close button; Tab reaches their Undo, and a drag on the waveform under one goes through to the waveform. Every window's title and Close button stay in view while it scrolls.
- **Keyboard focus is never hidden behind notices.** With notices above the player, Tab on a phone could scroll the next button to just above the player and so under a notice (Edit copy's **Save copy**, found when 2.41.0's CI failed in Safari's engine). Scrolling to a focused control now keeps clear of the notices as well as the player, the board gains room at its end while notices show so its last buttons can scroll clear, and a control that gets focus under a notice, or that a new notice lands on, is moved clear. Inside a window, its notices at the foot are kept clear the same way. The player's waveform tools (**Edit** on a phone), which open where the notices sit, gain the same room, and moving focus clear there never scrolls the page behind them.
- **Edits stay with their recording.** When Mimic or the companion restarts, the old selection, open draft, preview, waveform and words go at once, so nothing selected on one recording can play, keep, share or preview another.
- **Jump**, word-search results and **Last N s** put an open draft away instead of moving its edges.
- Dragging Mimic's overview moves the outline at once and fetches the waveform and words where it stops, without piling up requests.
- Clicking a word next to where a guest joined, the oldest recorded audio or a gap never takes audio from the other side, and says why when the word itself is not all there.
- When subtitles or a correction cannot be saved to disk, the host sees it in Mimic. They are kept while the companion runs and saved automatically once the disk works again.
- The host sees **Unverified** on guests and requests from pages older than 2.41. Such a guest counts as new on every page load until they reload Soundpad Web, and their direct and relayed connections count as two guests.
- "Possible match" notices reach only people allowed to use Mimic. For a guest the host does not allow seeking, a clicked word plays in their own browser only, and Voices says so.
- **Keep in voice library** that the library cannot take (it is full, or its file cannot be saved) now says so and leaves the draft open, instead of keeping the clip outside the library.
- Corrected text is changed on one recording at a time.
- The example: the start page says what you can try, leaving it says what is cleared (favourites and view settings stay), its guests are marked simulated, its Mimic is labelled as the older 30-second Mimic until the example is redesigned, and the hotkey says it only works on the host's PC.
- **A guest's busy moment is no longer refused.** Joining, then opening Voices and a recording, sends a dozen requests within a second, and the host's page refused those over its limit of 12 a second per guest: a guest's tag could be lost with "Too many requests", a refused status read left Voices showing "Loading the voice library…", and waveforms showed the same error. A guest's page now sends at most 8 such requests a second and queues the rest in order; Play, Stop and other playback never wait. (Found when 2.41.0's first CI run failed.)
- **Saving the voice library waits out a virus scanner.** A scanner that briefly holds the library file (as one did to `state.json` on GitHub's Windows runners) could make a change fail with "The voice library could not be saved". The save now retries for about a second, as `state.json` has since 2.35.
- **A change shows at once in the window that made it.** Right after a change, Voices could read the library's old revision on a busy PC and keep showing the recording as it was until the next update.
- The voice library is saved in `%LOCALAPPDATA%\SoundpadWeb\library\` (where kept sounds go by default): the library, its words, its list of changes (at most two files of 32 MiB) and the category word index. `state.json` and Temp's records are unchanged.

**One-time on update:** guests a 2.40 host page let in, or gave their own **Off** settings, are new to a 2.41 host page. Let them in or set them again.

### Reviewed

A review of 2.40 by Codex found nine problems, all fixed here: any invitation holder could take over another guest's admission, permissions, history start, drafts and budget by copying their profile id; **Discard** after a crash also removed what was recorded since; a selection could carry over to a new recording; slow audio work delayed Stop and Panic; notices covered Mimic's Close and Leave example; scrubbing restarted every fetch on each pointer move; word clicks could take audio from before a guest joined or across a gap; failed subtitle saves were silent; and the example promised more than it showed (its Mimic is now labelled as the older one until the example is redesigned).

A review of the voice library before release found 17 more, all fixed: "possible match" notices reached guests not allowed Mimic; a guest could hear any Soundpad sound by adding it to the library (guests now add and preview only sounds in categories indexed now, and a category taken out of the index is no longer searched); the list of changes sent guests the host's file paths and other guests' ids; Voices ignored a guest's own **Edit the voice library: Off** and single words ignored **Seeking & pause: Off**; the library could save a file too big to load again, and its list of changes grew without limit; **Keep in voice library** reported success when the library could not take the clip; words recognised for discarded audio could come back; a long background transcription held up live subtitles; Jump and quick selections moved an open draft's edges; a guest on an older page could be impersonated by copying their hello; and four smaller ones (the session view waiting for the library, a change id reused after a backup was loaded, the category index stopping after one pass, and speaker naming without a recording check).

### Tests

- Fixes after the first CI run: 2 new companion tests (a status read right after a change counts it; a change waits out a scanner holding the library file), 1 new website unit test (a guest's burst is paced in order, playback is not), and the browser tests' failure notes now include every page's notices, also those already gone. A history test that compared a guest's start with the live frame after the fact now compares it with the live frame at joining (it failed on a slow runner, where the fixture's audio kept arriving between the two).
- The phone test for focus behind the player bar now also checks the notices, with one and with two showing, and makes Tab scroll the way it does mid-page, so Chromium catches what only WebKit did (Chromium's focus on the field before had centred it, leaving nothing to scroll). 5 new website unit tests for how far a focused control is moved clear. 1 new browser test: Tab through the player's waveform tools on a phone with notices showing keeps each control clear of them and leaves the page where it was.
- The browser tests no longer connect to the public relays (the opt-in live-relay tests still do). A host page connects to them when its session starts, and in Playwright's WebKit on Windows that held up the page's own connection to the companion while it lasted: with 10,000 sounds, Play was answered after 2.5 s on the owner's PC (the companion itself took 50 ms; 2.40.0 is the same) and after more than the page's 12 s limit on a GitHub runner, which failed 2.41.0's CI. Without the relays it takes about 0.1 s. Chromium (Chrome and Edge) answers in under 0.1 s either way. The companion test that background transcription lets a live subtitle in between its pieces now queues the subtitle while the first piece runs, as one really arrives; it queued it a moment later before, and failed on a busy PC. The phone test for the player's waveform tools reads each control's place a frame after Tab, once the page's second check has run: WebKit scrolls inside the tools only then, and the test failed 7 times in 8 on the owner's PC (it still fails without the fix it was written for, in both engines). The test with 10,000 sounds types its search again when WebKit drops the text (the box kept focus but the page never got the input, 1 run in 5 on the owner's PC while a second suite ran).
- 30 new companion tests (the voice library, its limits and audit, guest scoping of search, previews and notices, the category index, speaker groups, Discard keeping new capture, commands refused on another recording, Stop and Panic during slow work, unsaved subtitles), 28 new website unit tests (guest keys and proofs, the voice library's rules, scrubbing, word clamping), and 8 new browser tests (four for the voice library, notices never covering controls, scrubbing with Stop and Play prompt, a selection and draft not carrying over to a new recording, and Last N s with a draft open).

## 2.40.0 — Drafts, and guests who can keep

**Update the companion** for this one. With an older companion, Mimic's **Mimic it** works as before.

### Added

- **Capture makes a draft.** In Mimic, **Capture** (and Enter) no longer adds a clip to Temp straight away: it opens an editable **draft** for whoever captured it. Drag its edges into the audio around it, **Preview** it privately, **Play into game**, then **Keep in Temp** (or **Keep in Temp and close draft**), or **Discard** it. A draft protects its audio, so it is not lost when that part of the history would otherwise expire.
- **Everyone has their own drafts.** Each person sees one draft at a time, with the rest under **Draft history**. Other people's drafts show as coloured bands ("Alex's selection") over audio you may see, with **Copy to my drafts**. When someone captures, the others get a quiet notice. Mimic and Temp remember what you had open after you close them or reload the page.
- **Latest phrase** and **Last 10 seconds** make a draft in one click. Latest phrase skips a phrase that is entirely Soundpad playing something you sent it, but keeps people talking over it.
- **Guests can preview privately.** A guest allowed to use Mimic can listen to a selection or draft in their own browser (up to 30 seconds, only audio they may see). It never plays into the game, and there is no download button.
- **Guests can keep clips.** A new permission, **Keep clips** (on by default, and can be turned off per guest), lets guests keep clips into the folder and category the host sets once in **Settings**. Guests can never choose a folder.
- **Storage budgets per guest.** The host sets how much each guest may add to Temp and to kept sounds (per session or in total). A guest who reaches a budget sees "Waiting for the host to allow more", and the host gets an **Allow more / Not now** request; an allowance lasts until the companion closes, and nothing kept is ever deleted. Usage shows in the host's guest list.

### Changed

- **End hosting keeps Temp.** Temp now stays until the companion quits, so ending and resuming hosting no longer loses clips.
- **Quitting asks first.** Closing the companion window with clips in Temp asks: **Keep selected**, **Quit anyway** or **Cancel** (in the window's page, or in a small dialog when no page can answer).
- **After a crash**, the next start offers **Recover previous session's Temp** or **Discard**. Discard removes only what the crashed session left.
- Guests can remove their own Temp clips, also after reloading their page. Keep is unavailable, with a reason, while a guest is reconnecting.

### Reviewed

A review found seven problems, all fixed before release: guests' drafts could use up the space that protects the host's own drafts (each guest now has a share, the host can discard anyone's draft, and a guest's drafts go when they are removed or away for 10 minutes); other people's draft names could show words from before a guest joined; a guest's upload budget restarted when they reloaded; Discard after a crash also deleted clips made since; and End hosting then closing the window quit without asking.

### Tests

- 20 new companion tests (drafts and their protected audio, budgets, guest previews and keeps, the quit prompt, crash recovery), new website unit tests, and 3 new browser tests (capture, edit and keep a draft; a guest's preview limited to what they may see; a budget pause the host allows).

## 2.39.0 — Ten minutes of history, word by word

**Update the companion** for this one. With an older companion, Mimic keeps its 30-second view.

### Added

- **Mimic keeps a recording history.** Instead of the last 30 seconds in memory, the companion now keeps a rolling history of the chosen output or app: 10 minutes by default, from 1 minute to 1 hour (**Keep the last** in Mimic's host controls). The Mimic window shows the newest 10 seconds with an overview of the whole history underneath: drag either to look back, use **Back 10 s** / **Forward 10 s** and **Go live**, and see the time of day and how far behind live you are. Recording carries on while you look around. Where capture was stopped or the output went away, the gap is drawn, not hidden.
- **Word-by-word subtitles.** Each word has its own start and end: click a word to select it, Shift-click another to select everything between, and make a clip of exactly that. Words the companion is unsure about are underlined with dots. **Subtitles: Gaming / Balanced / Fast** decides how much CPU recognition may use; Gaming, the default, uses one low-priority thread so games are not slowed.
- **Guests see only what was recorded since they joined.** The companion enforces it for the waveform, the words, clips and previews. The host can **Share with guests** an earlier part, and **Stop sharing**.
- **Stopping capture keeps the history** until the companion quits. If the output device goes away, the history is kept and Mimic asks for another output. The companion remembers the output you used last.
- **After a crash** the next start recovers the history and says so, with **Discard**.

### Changed

- The history and its words are written to `%LOCALAPPDATA%\SoundpadWeb\history\` and `transcripts\` while the companion runs, and deleted when it quits (also when Windows logs off or shuts down). Up to 2.38 Mimic audio was never written to disk. See [Mimic privacy](docs/MIMIC.md#privacy-and-safety).
- Mimic refuses any source that is not an output device or an app's own sound, checked when capture starts as well as in the source list: it can never record a microphone.
- A guest the host disconnects or turns away starts again from their next admission; a reload of the host's page no longer resets guests' join times or the host's shares.

### Measured

On synthetic test speech (two Windows voices, 144 sentences, real Whisper on this PC, nothing played): words found 99 % clean and 96 % with background noise; word edges within 15–20 ms (median); about 13 % of single-word cuts clip part of the word or catch a neighbour, 18 % with noise. Real game voices have not been measured yet, and no real capture device was used in testing.

### Reviewed

A review of the change found nine problems, all fixed before release, among them: a guest let back in after a Disconnect could see what was recorded while they were out; a guest's starting point could fall slightly before their join; a long pause in capture pushed history times into the future; closing the companion's console or logging off left the history on disk; word and phrase numbers told guests how much was said before they joined.

### Tests

- 26 new companion tests (history wiring, gaps, crash recovery, guest scoping on every query, outputs only, word timing including one real Whisper check), 24 new website unit tests, and 2 new browser tests (looking back and clipping words; a guest who cannot see before they joined).

## 2.38.0 — Try the example

Website only; works with every companion. The companion reports 2.38.0 in this release, with no other change.

### Added

- **Try the example** on the start screen. It opens an example session that runs entirely in your browser, with 19 example sounds (effects, a Sousa march and two Apollo 11 recordings), so you can see what Soundpad Web does before setting anything up. Nothing is sent to Soundpad: the sounds play in the browser, the guests are simulated, and **Leave example** forgets everything.
- What it shows: categories with artwork, search, all five views and waveforms; play, pause, seek, cue starts, regions and repeat; the newest click winning and Panic; the Kick counter and statistics; Temp with reverse, Edit copy with any speed and Keep pitch, preview and Undo; a simulated guest "Alex" who joins and sometimes plays a sound, and "Sam" asking to join when you choose Ask me first, with per-guest permissions; and Mimic "listening" to the Apollo 11 clips with a transcript you can clip from. Things that need your PC (Keep permanently, Spotify, the Mimic hotkey, Voice Replay Studio import, invitations) say so instead of pretending.
- The example sounds are public domain or CC0; where each one comes from is listed in `web/public/demo/CREDITS.md`. They are downloaded only when you open the example.

## 2.37.0 — Sturdier connections, exact starts

**Update the companion** for exact starts and for keeping the host's session choices across a reload of its window. The rest works with any companion.

### Added

- **A backup route for guests.** When a guest's browser cannot reach the host directly within about 12 seconds (some home routers, school or work networks, a browser with WebRTC off), it carries on through the same free public relays the invitation already uses to find the host, encrypted end to end with keys from the invitation. Everything works that way except uploading audio files, which need a direct connection. The status reads "Connected (relayed, slower)", and it keeps trying to connect directly. No paid service, account or server is involved.
- **Connection problems say what is wrong.** Instead of a general "Host unavailable", a guest sees the likely cause: WebRTC blocked, no relay reachable, host not found (offline, or the invitation was replaced), direct connections blocked, or a strict network. **Settings → Connection details** shows the full picture, with **Check network**. The host sees for each guest whether they are connected directly or relayed.
- **Guests reconnect by themselves** after a dropped connection, and the banner says how long it has been trying. If the host started a new session, the guest is told the invitation has ended instead of waiting forever.
- **Ask me first.** In **Settings → Session & access**, the host can choose **Admission: Let guests in at once / Ask me first** and **Guest places** (1–12). Waiting guests appear in a list with **Let in** and **Turn away**. When the session is full, a new guest can **Ask for an extra place**. A guest who reloads keeps their place.
- **Permissions per guest.** Each guest in the list has a **Permissions** control: each permission can be turned off for that guest alone, or left "Same as everyone". (A permission that is off for everyone cannot be turned on for one guest: the companion checks the session-wide setting.)
- **Exact starts and ends.** Clicking a sound's waveform, or playing a region or cue, now starts at exactly that point and a region stops exactly at its end. The companion copies just that part of the sound into one of three small "slot" files in its Temp category and plays that. Before, Soundpad started the sound from the beginning and then jumped, and measured on the owner's Soundpad the jump was sometimes ignored, so the whole beginning played.

### Changed

- Removing the companion's Temp category (Clear Temp, End hosting, quitting) waits until nothing has played for a second: Soundpad once closed itself when its category was removed right after one of its sounds played.

### Measured

With the test Soundpad: 100 of 100 cue starts and regions played the wanted audio from their first sample, 31 ms (median) after the click. With 2.36, 86 of 99 played from the top of the sound until replaced. Exact starts were proven on the owner's real Soundpad 4.0.35 before they were built (a rewritten slot file is read again on every play, its first and last 20 ms are played exactly); the finished feature has not yet been run on the real Soundpad. The relay route was tested with simulated relays and the public relays were probed from this PC; it has not yet been tried between two real networks.

### Reviewed

A review of the change found eight problems, all fixed before release: a repeating region started again after it was stopped in Soundpad itself, and another sound started from Soundpad near a region's end was shown as that region; a seek inside an exact start could play the wrong Soundpad entry after the list changed; quick clicks on a long sound rendered several large slices at once, none of which played, and a sound too long for exact starts was decoded again on every click; over the relay, a guest with a large library never got in, because each of its hellos restarted the host's welcome; switching to "Let guests in at once" let in every waiting guest whatever the number of places; a per-guest "On" showed controls the companion then refused; a connected guest could take on another id to leave their own "Off" behind; and one invitation holder could fill the waiting list without limit until the views lost the session.

### Tests

- 15 new companion tests for exact playback and slots, 1 for the session settings the companion keeps, and 6 for the review's fixes.
- 7 website unit tests for the review's fixes.
- 27 new website unit tests (relay route, diagnosis, reconnect, lobby, places, per-guest permissions).
- 3 new browser tests: Ask me first, a full session with an extra place, per-guest permissions.

## 2.36.0 — Hosting without a browser tab

**Update the companion** for this one. The website still works with older companions, which host from a browser tab as before.

### Added

- **The companion has its own window**, and that window hosts your session. Guests stay connected while it is open, also when it is minimized or behind a game, and you no longer need a browser tab open. Closing the window quits the companion with its usual cleanup. Each launch makes a fresh invitation; reloading the window keeps it, so guests reconnect by themselves.
- **Browser tabs are views.** Opening the website on the host PC (for example from an old launch link) shows the same session: the same invite, guests and controls. Replace invite and Disconnect work from a view; End hosting stays in the window. Closing a view does not affect guests.
- Without a window (`--no-window`, or a PC without the WebView2 Runtime) the companion works as in 2.35: it opens your browser and that tab hosts. If that tab closes, another open tab takes over after a few seconds and guests reconnect to it.
- The companion keeps a log at `%LOCALAPPDATA%\SoundpadWeb\companion.log`.

### Changed

- The EXE is now a Windows desktop app (about 13 MB larger, for Windows Forms and WebView2). Started from a terminal, it still prints there.
- Wording across the site now says the Soundpad Web window keeps the session running, where it used to say to keep the host tab open.
- Quitting while Soundpad is closed takes about 3 s instead of 7: the companion no longer waits for Soundpad to answer before it exits, and removes that session's temporary audio at the next start instead.

- `--hidden-window` runs the window without ever showing it, for automated checks on a PC someone is using.

### Measured

On this PC, with a test Soundpad and the public relays (no real Soundpad, no second PC): a guest joined the window's session in under 2 s, and with the window minimized for 2 minutes 15 of 15 guest plays arrived, 37–67 ms from click to Soundpad. Closing the window exited in about 1 s and left no WebView2 processes. With the 2.36 website in a hidden window, a browser tab became a view with the same invite, a guest joined in 1.8 s and 8 of 8 plays arrived in 33–96 ms, also after the view closed.

### Reviewed

A review of the change found six problems, all fixed before release: a tab demoted while reconnecting could keep hosting the same invitation; two overlapping Replace invite requests could leave a second working invitation; an invitation ended with End hosting could come back from a tab's saved copy; a page that stopped reading for 8 s kept its place and could be handed hosting it never heard about; a reloaded hosting tab lost its place to another tab; and the developer benchmark script opened a window.

### Tests

- 9 new companion tests over real loopback connections (owner and views, the window taking over, a reload keeping the invitation, hand-over after the owner leaves, relayed session details, up to 8 pages).
- 13 new website unit tests and 5 new browser tests (a view's Replace invite and Disconnect, closing a view, reloading the owner, hand-over without a window, the window taking over, an older companion).

## 2.35.0 — The newest click wins

**Update the companion** for this one: the playback changes and Panic live in it. The website still works with older companions, which behave as before.

This is the first step of a larger overhaul: making rapid back-and-forth between two people reliable.

### Added

- **Panic** (host). A separate button beside Stop, and **Shift+Escape** anywhere on the page except while typing. It stops what is playing at once, cancels anything still about to start, and pauses guest playback until you press **Resume guest playback**. It does not end the session, Mimic or Temp, and you can still play sounds yourself. Guests see a calm note that the host paused playback; their play buttons are held, not broken.
- **A clicked sound shows that it is on its way** until the companion answers. "Playing" still appears only when the companion reports it.

### Changed

- **Only the newest click plays.** Every Play, cue, region, Temp play, reverse, Stop and Panic is put in order as it arrives at the companion (never by anyone's clock). An older click that a newer one replaced is dropped quietly: it never plays late, is not counted in statistics, and shows no error.
- **Stop is immediate.** It no longer waits behind a library refresh, audio being prepared or queued plays, and nothing that was asked for before a Stop can start after it.
- **Save & play, Mimic & play, quick effects and Import & play** are one request now. If you or a friend play something else (or press Stop) while the clip is being made, the clip is still added to Temp but does not interrupt.
- **Rapid playing of Temp and kept sounds** no longer answers "Audio preparation is busy". A sound that is already prepared plays as directly as a normal one.
- **Playing no longer waits for the disk.** Play statistics are saved a moment later in the background. The Kick counter and settings are still saved before they are confirmed.
- **A guest who clicks fast is never disconnected for it.** With an updated companion, playback controls have their own generous allowance, separate from the limits that protect against abuse and heavy requests (with an older companion the 2.34 limits stay, because it has one shared budget). A guest whose page is newer than the host's page is told so instead of being dropped.
- **A flood cannot get in the host's way.** Many Stops arriving together share one request to Soundpad, a newer seek replaces one that is still waiting, and the host's own Stop and Panic never compete with guests' clicks for room. A page that floods another guest is still cut off.
- Clicks made while disconnected are dropped, not sent late. Old clicks (over 5 seconds) are ignored quietly. A guest's clock changing mid-session no longer makes every play expire.
- Play buttons in Temp stay usable while a copy is being saved.

### Fixed

- The companion could close by itself if Soundpad refused the stop at the end of a region.
- Saving settings no longer fails when a virus scanner holds the file for a moment just after it was written; the save retries briefly.
- A refused command no longer makes the companion re-read its settings file, and the icon folder is only tidied when an icon changes.
- Clicking a waveform just after someone else started a sound now starts it where you clicked, instead of doing nothing. A sound that was paused after you clicked its waveform stays paused.

### Measured

With the test Soundpad answering in 5 ms, two controllers alternating plays, cues, regions and stops for 20 seconds while waveforms, statistics, the counter and uploads ran:

| | 2.34.2 | 2.35.0 |
| --- | --- | --- |
| Click to Soundpad starting it (median / slowest) | 1333 ms / 1833 ms | 31 ms / 118 ms |
| Stop: sent to answered (median / slowest) | 1126 ms / 1761 ms | 21 ms / 31 ms |
| Plays that started after a newer click had already arrived | 239 of 241 | 0 of 250 |
| Error answers to playback | 175 | 0 |

These are from the test harness (`--playback-bench`), not from the real Soundpad or a real second PC.

### Tests

- 22 new companion tests (ordering, Stop and Panic, floods of Stops and seeks, quiet answers, prepared Temp sounds, deferred saving, the poll loop surviving a refused command).
- A review of the change (concurrency, the website, security and compatibility) found seven problems, all fixed above before release.
- 33 new website unit tests (the playback path, guest limits with new and older companions, clock drift, older host pages).
- New browser tests with **two pages in one browser** (a host and a guest joined without any relay): competing controllers, Stop after a burst, Panic and Shift+Escape, a newer Play during Save & play and Mimic & play, the pending mark, and an older companion.

## 2.34.2 — Undo notices wait for you

Website only; works with every companion. The companion reports 2.34.2 in this release, with no other change.

### Fixed

- **A notice with an Undo button no longer disappears while you're reaching for it.** It stays as long as the pointer is over it or keyboard focus is inside it, then for at least 3 more seconds. Before, it left after 10 seconds regardless, which is too short for some people to read and act on (WCAG's timing guidance). Notices without a button keep their usual timing.

### Tests

- New e2e test: with the pointer on an upload's Undo notice for 12 s, past its usual 10 s, it is still there; moving away, it leaves within 5 s. It fails on 2.34.1.

## 2.34.1 — Uploads wait their turn

Website only; works with every companion. The companion reports 2.34.1 in this release, with no other change.

### Fixed

- **An upload made while the companion was busy with other audio (for example a reverse) failed with "Audio processing is busy; retry shortly."** The website now retries it for you, up to six times over about 8 seconds, and only shows the message if it still can't get in.

### Tests

- **Failures on CI now explain themselves.** Several browser tests were flaky on GitHub's runners, passing only on their retry: uploads that never appeared, a guest without WebRTC, Loop, phone focus, Temp search and Zoom. CI's traces and logs need a login, but failure messages are public. So a failed test now adds what the page showed (its notices and status line) and the browser's console errors to its message, and the no-WebRTC test adds what the guest's page showed. The next flaky run will show its cause.
- Some of those flaky upload failures may have been this busy refusal; the retry above should settle them if so.

## 2.34.0 — Undo a Voice Replay Studio import

Website only; works with every companion that imports from Voice Replay Studio (2.15+). The companion reports 2.34.0 in this release, with no other change.

### Added

- **Undo on the "Imported … into Temp" notice** (host) after **Import** or **Import & play** from Voice Replay Studio. Every way into Temp now has Undo: Mimic clips, Edit copies, uploads and imports.
  - It removes only the copy in Temp. Voice Replay Studio's own clip and library are never touched, as always.
  - If the import is still playing after Import & play, it's stopped first, never another sound.

### Tests

- The Voice Replay Studio e2e test now presses Undo after Import & play: the playing import leaves Temp, and the other import stays.

## 2.33.0 — Undo an upload

Website only. The host can use it with every companion; guests need companion 2.32 or newer (the one that allows guest Undo). The companion reports 2.33.0 in this release, with no other change.

### Added

- **Undo on the "Added to Temp for this session" notice** after **Add session audio**: the wrong file comes straight back out of Temp and Soundpad. The host gets it for any upload, and a guest for their own (the companion checks ownership, as for 2.32's guest Undo).
  - Like the other Undos, it stops the upload first only if it's the sound playing, and never stops another sound.

### Tests

- New e2e test: upload a file, press Undo on its notice, and it leaves the board and Temp.
- The live-relay guest test now checks that a guest's own upload notice offers Undo, so the new source's id reaches guests over the relay.

## 2.32.0 — Undo for guests

**Update the Windows companion** to get this. With an older companion, guests see no Undo, exactly as before.

### Added

- **Guests can Undo their own mistakes.** Until now only the host could take a clip or edited copy back out of Temp. Now the guest who made it gets **Undo** too: on the "… mimicked …" notice for their own Mimic clip, and on the "Copy added" notice after their own Edit copy.
  - Only their own: the companion checks that the guest added that audio. Anyone else's, the host's, and anything kept permanently are refused.
  - Only while the host lets guests add audio (Session & access, uploads). Turning that off turns guest Undo off too.
  - As for the host, Undo stops the clip first if it is the one playing, and never stops another sound. If something else is playing, it asks to stop playback first.

### Tests

- New C# test: a guest can't remove another guest's or the host's audio, can't remove without a session identity or while guest uploads are off, and can remove their own.
- The host website's guest-command filter test now expects removals to pass only while guest uploads are on (the companion decides the rest).
- The live-relay guest test (it runs for every release tag) now has a real guest make a clip and Undo it, and the host sees it leave Temp.

## 2.31.0 — Keyboard on Edit copy's waveform

Website only; works with every companion. The companion reports 2.31.0 in this release, with no other change.

### Added

- **Edit copy's waveform takes the keyboard, like Mimic's.** Tab to it, then **Left/Right** moves the end, **Shift+Left/Right** the start, and **Ctrl** makes 1-second steps. Start and End follow. Screen readers hear it as a slider that says which part is chosen ("From 0.40 s to 0.90 s of 1.00 s"). It shows a focus ring.
  - It uses Mimic's own nudge rules, so both waveforms behave the same. This also settles the review's note that Edit copy duplicated Mimic's selection logic.

### Tests

- The Edit copy waveform e2e test now also nudges with Shift+Left, Left and Ctrl+Shift+Left and checks Start, End and what a screen reader hears. The accessibility scan of the newer controls passes with it.

## 2.30.1 — "See what's new" always opens

Website only; works with every companion. The companion reports 2.30.1 in this release, with no other change.

### Fixed

- **The "See what's new" link after a website update (2.18.2) could open a 404 page.** It pointed to the version's GitHub release. But the website is published as soon as `main` passes its checks, before the version tag is pushed and its release built, so the release page doesn't exist for the first 15 minutes or so. For v2.29.0, which never got a release, it never existed. The link now opens the changelog, which always starts with the new version's notes.

### Tests

- The What's-new e2e test expects the changelog link.

## 2.30.0 — Undo decided by the companion

**Update the Windows companion** to get this. With an older companion, Undo works as in 2.29.3.

### Changed

- **Undo (on Edit copy's and Mimic's notices) now asks the companion to stop the sound only if it is the clip being undone.** The companion knows what it started playing, so it decides exactly. It no longer guesses from a playback report that may not have reached the page yet, or matches the wording of an error message. Two problems found in the 2.29 review go away:
  - Undo pressed the instant after Mimic & play or Save & play no longer depends on timing.
  - Another sound that is playing is never stopped. Undo then says to stop playback first, as before.

### Tests

- New C# test: with one Temp sound playing, Undo of a different one is refused and that sound keeps playing; a guest's request stops nothing; Undo of the playing one stops it and removes it.
- New unit test: with companion 2.30, Undo sends one request and leaves the decision to the companion. Older companions keep the 2.29.3 path, which is still tested.

## 2.29.3 — Fixes from a review of 2.19–2.29

A code review of everything since 2.18.2 found these. The website fixes work with any companion. The two naming and preview fixes marked (companion) need this release's companion.

### Fixed

- **Undo no longer stops an unrelated sound.** Soundpad removes nothing while any sound plays, so 2.29.2's Undo stopped whatever was playing, such as someone's music, before removing the clip. Now it stops only the clip being removed. If something else is playing, it leaves that sound alone and says to stop playback first. It still waits briefly for the playback report, so Undo right after "& play" keeps working.
- **Loop no longer breaks when the cut changes quickly.** The companion makes one copy at a time. A new preview requested while a slow one (for example, same pitch at a slow speed) was still being made was refused with "Another copy is being made", which also ended the loop. Previews now wait their turn, only the latest change is made, and no work is wasted on previews nobody will hear.
- **(companion) A '/' or '\' in a copy's name no longer cuts it short.** Recognised words like "open 24/7" named the copy just "7 (trimmed)", because the name becomes a file name first. They become "-", as Voice Replay Studio imports already did.
- **(companion) Names made from recognised words stay plain ASCII.** Whisper's curly quotes, long dashes and "…" become ' " - and ..., which Soundpad shows correctly whatever the PC's code page.
- **(companion) A preview of a surround (for example 6-channel) sound ends on a whole frame.** Its size cap could end it partway through a frame, which some players reject.
- A Mimic Edit or Keep request that Temp can't carry out within 20 s (for example, the clip was undone meanwhile) is dropped, instead of opening the form much later.

### Tests

- Unit tests for Undo: a different sound playing is never stopped, and a late playback report for the clip itself still stops and removes it.
- New C# tests: typed and recognised names keep every word ("Open 24/7 AC\DC" becomes "Open 24-7 AC-DC"), words become plain ASCII, and a 6-channel preview ends on a whole frame (it fails on 2.29.2).
- The Loop e2e test now changes the speed four times while same-pitch previews are made, with no "Another copy" error and the loop still playing.

## 2.29.2 — Undo right after Mimic & play is reliable

Website only; works with every companion. The companion reports 2.29.2 in this release, with no other change.

### Fixed

- **Undo pressed right after Mimic & play (or Save & play) could fail with "Stop playback before removing temporary audio."** Undo stopped the sound only if this page already knew it was playing. Pressed quickly, the page hadn't heard yet, so the companion refused, and Undo retried without stopping. Now Undo stops the sound when the companion says it is playing, then removes it.
- That expected refusal no longer flashes as an error notice. Only a real failure is shown, once.

### Tests

- New unit tests for Undo's remove step: it stops a sound the page knows is playing; it stops and retries when the companion reports playback first, with no error shown; and it reports any other refusal once. Two of the three fail on 2.29.1.
- On CI, browser tests now allow up to 45 s for an upload or copy to be ready (25 s locally) and 90 s per test (45 s locally). v2.29.0's tag run was about twice as slow as usual, and a failed tag run publishes no EXE.

## 2.29.1 — 2.29.0, released

Website only. The app is identical to 2.29.0 apart from the version number, so everything in 2.29.0 below arrives with this release.

- **2.29.0's tag CI failed on a slow GitHub runner,** so no EXE was published for v2.29.0 (the same commit had passed on `main`). The run took 6.5 minutes against about 3 normally. In both attempts, the "guest without WebRTC" test waited over 30 s for the guest's name prompt, and an upload test needed its retry. Public tags are never moved, so v2.29.0 stays source-only, and this is the release.
- That test is now sturdier. It joins as a returning guest who already chose a name, which is where the WebRTC check happens (the name prompt is tested elsewhere), and it allows 60 s for a fresh browser to load the app, like the other guest tests.

## 2.29.0 — Undo a Mimic clip

Website only; works with every companion. The companion reports 2.29.0 in this release, with no other change.

### Added

- **Undo on the "… mimicked …" notice** (host): a clip grabbed by mistake (the wrong moment, a stray F6, a guest's clip that shouldn't stay) comes straight back out of Temp and Soundpad with one press, like Undo after Edit copy (2.28).
  - A clip still playing after **Mimic & play** is stopped first, because the companion never removes temporary audio while it plays.
  - The clip leaves "Just mimicked" too. Guests' notices have no Undo, because only the host can remove Temp audio.

### Changed

- Edit copy's Undo and the new one share the same remove step.

### Tests

- New e2e test: a clip made with Mimic & play is removed by Undo on its notice while it plays, leaves Just mimicked, and shows no "Stop playback" error.

## 2.28.0 — Undo an edited copy

Website only; works with every companion. The companion reports 2.28.0 in this release, with no other change.

### Added

- **Undo on the "Copy added to Temp" notice** after Edit copy's **Save copy** or **Save & play** (host), like Voice Replay Studio's **Undo trim**. One press removes that copy from Temp and Soundpad. The original is never touched.
  - If the copy is still playing after Save & play, Undo stops it first, because the companion never removes temporary audio while it plays.
  - The notice stays for 10 seconds, long enough to reach its button.
  - Guests don't get Undo, because only the host can remove Temp audio.
- Notices can now carry one button. This is the first use.

### Tests

- New e2e tests (Chromium and WebKit): Undo after Save copy removes the echo copy and keeps the original; Undo right after Save & play stops the copy and removes it, without the "Stop playback" error.

## 2.27.2 — Keyboard focus never hides behind the player bar

Website only; works with every companion. The companion reports 2.27.2 in this release, with no other change.

### Fixed

- **On a phone, pressing Tab could move focus to a control hidden behind the player bar.** For example, Edit copy's **Save copy** and **Save & play** ended up under the bar at the bottom of the screen, so a keyboard user couldn't see what was focused. The page now always keeps room for the bar when it scrolls a control into view, everywhere on the page. It follows the bar's real height, including when the bar is expanded.
- Temp's search box (2.25) shows a shorter placeholder ("Search 7 sources"), which was cut off on phones. The full rule is in its tooltip.

### Tests

- New e2e test at phone width (Chromium and WebKit): tabbing to Save copy, Save & play and Preview shows each one, not the player bar, under the focused control. It fails without the fix.

## 2.27.1 — Readable clip names in Mimic on phones

Website only; works with every companion. The companion reports 2.27.1 in this release, with no other change.

### Fixed

- **On a phone, a fresh clip's name in Mimic's "Just mimicked" list was squeezed to its first letter** ("A… · Host"), because the Edit (2.21) and Keep (2.26) buttons took the row's space. On a narrow screen the name now keeps its room, and the buttons and Effect menu move to the line below when they don't fit beside it. On a wider screen each clip is one row, with its buttons lined up under each other's.

### Tests

- New e2e test at phone width: a long clip name stays over 100 px wide (it was 4 px before the fix), and every button and menu stays inside the window.
- New harness tool `--real-edit-copy-probe` checks Edit copy on this PC's real Soundpad, with the companion closed. It uses a throwaway data folder (never the companion's), so it has its own Temp category, and plays nothing aloud. It uploads a tone, makes a chipmunk copy, previews an echo copy, and ends hosting. On the owner's PC (2026-11-10): the copy was made in 272 ms and listed by Soundpad; it lasted exactly 1.00 s (1.5 s at 1.5×); the preview worked; Soundpad ended exactly as before (143 sounds, 9 categories, the same paths and names); and the companion's own `state.json` and journal were byte-for-byte unchanged.

## 2.27.0 — Save & play in Edit copy

Website only; works with every companion that has Edit copy. The companion reports 2.27.0 in this release, with no other change.

### Added

- **Save & play** next to Edit copy's **Save copy**, like **Mimic & play**: it makes the copy and plays it through Soundpad straight away, so everyone hears the edit without hunting for the new sound.
  - Shown to the host, and to guests allowed to play sounds remotely (the same rule as Mimic & play).
  - Enter in the form still means **Save copy**.

### Tests

- New e2e test (Chromium and WebKit): a chipmunk copy made with Save & play appears in Temp, Soundpad is asked to play it, and the form closes.

## 2.26.0 — Keep a fresh Mimic clip in one press

Website only; works with every companion. The companion reports 2.26.0 in this release, with no other change.

### Added

- **A Keep button (bookmark) on every clip in Mimic's "Just mimicked" list** (host). Voice Replay Studio keeps every capture in its library. Here, one press closes the Mimic window and opens Temp's **Keep permanently** step for that clip, with the folder field focused. Choose a folder and category, or keep the defaults, and confirm.
  - Before, this took four steps: close Mimic, open Temp, find the clip, press Keep normal permanently.
  - Nothing is kept until you confirm, exactly as in Temp.
  - Pressing it for a clip that is already kept just says so.
  - Guests don't see it: only the host can keep audio.

### Changed

- Mimic's Edit (2.21) and Keep buttons share one way of opening Temp at the right step.

### Tests

- New e2e test: make a clip and press Keep. Mimic closes, the confirmation for that clip opens with the folder field focused, confirming keeps it, and pressing Keep again says it is already kept.

## 2.25.0 — Search Temp

Website only; works with every companion. The companion reports 2.25.0 in this release, with no other change.

### Added

- **A search box in Temp** once a session has more than six sources (reversed sounds, uploads, Mimic clips and edited copies), like the one in Mimic's Voice Replay Studio list.
  - It matches the name or who added the sound.
  - A source whose Edit copy is open stays listed whatever the search says, so a search never hides the form you're working in.
  - When nothing matches, it says so.

### Tests

- New e2e test: six sources show no search; the seventh adds it; "fox" leaves only Foxtrot; an open Edit copy stays listed while the search matches nothing; "No sources match" appears after closing it; searching "host" (who added them) shows all seven.
- The permanent accessibility (axe) test now also covers Edit copy's waveform, Zoom to selection, Preview and Loop (on a desktop and a phone screen) and Mimic's Edit button (2.19–2.23). No serious or critical problems were found, in Chromium or WebKit. Edit copy was also checked at phone width: nothing overflows, and every control scrolls clear of the player bar.
- New harness tool `--trim-words <wav> <fromMs> <toMs>` shows the name a trimmed copy gets from real Whisper (2.24). On the owner's PC, three cuts of the JFK sample were named "And so my fellow Americans.", "not what your country can do for you." and "Ask what you can do for your country.", in about 1.7 s each.

## 2.24.0 — Trimmed copies named after their words

**Update the Windows companion** to get this. With an older companion, copies keep their usual names, and Settings lists the feature under what an update adds.

### Added

- **A trimmed copy is named after what it says** while Mimic transcripts are on, like **Save + retranscribe** in Voice Replay Studio. Trim "Mimic 21.04.17" down to the funny bit and the copy is called, for example, "no way that worked (trimmed)", so the board's search finds it by its words.
  - It applies to plain trims of up to 30 s with the Name box left empty. A typed name always wins.
  - Effects (Chipmunk, Echo, speed changes) keep their effect names, because they garble speech for recognition.
  - Recognition runs on the host PC with the same speech model as the live transcript. Nothing is uploaded. If it hears no words, or takes over 20 s, the copy keeps the usual name.
  - Edit copy says when this will happen ("left unnamed, it is named after its words"), and the Name box shows "Its words (trimmed)".
  - Previews are never transcribed.

### Tests

- New C# test: with transcripts off, the usual name is used and the engine is never asked. With them on, a plain trim is named after its words. Silence, effects and typed names keep their names, and previews are not transcribed.
- New e2e test: with transcripts on, Edit copy shows the hint (not for Echo), and the saved trim is named after the stand-in engine's words.
- Unit tests list the new feature for older companions.

## 2.23.0 — Loop the preview while you tune the cut

Website only; Loop needs a companion with Preview (2.20+). The companion reports 2.23.0 in this release, with no other change.

### Added

- **Loop** next to Edit copy's **Preview** (host), like **Loop selection** in Voice Replay Studio's editor.
  - With Loop ticked, the preview repeats until **Stop preview**.
  - Change the start, end, effect or speed while it plays (drag on the waveform or type), and after a short pause the new cut plays instead. Edges can be tuned by ear without pressing Preview again.
  - Unticking Loop lets the current play finish and stop by itself.
  - As before, only the host hears it, on their own PC. Soundpad plays nothing and nothing is saved.

### Changed

- **Stop preview** now also cancels a preview the companion is still preparing (Edit copy and Mimic). Before, a preview stopped during that moment would start playing anyway.

### Fixed

- Edit copy's **Keep pitch** checkbox (2.17–2.22) was drawn as a large empty box away from its label, because the form's text-field styling also applied to it. It is now a normal checkbox beside its words.

### Tests

- New e2e test: with Loop ticked, Preview plays with repeat on and is still playing after its 3 seconds; changing Start plays the new cut, still looping; unticking Loop lets it stop by itself; without Loop, changing the cut starts nothing; Soundpad plays nothing throughout.

## 2.22.0 — Zoom in on Edit copy's waveform

Website only; works with any companion that has Edit copy. The companion reports 2.22.0 in this release, with no other change.

### Added

- **Zoom to selection** under Edit copy's waveform, like the zoom in Voice Replay Studio's editor.
  - It shows just the chosen part, with a little room either side, so both edges can be set finely by dragging even on a long sound.
  - On a 3-minute sound the whole strip covers about 0.2 s per step. Zoomed to a 2-second part it covers about 0.003 s.
  - **Show whole sound** zooms back out. A line under the strip says which seconds it shows.
  - The button appears once the chosen part is less than most of the sound.

### Fixed

- Dragging on Edit copy's waveform could stop partway when text near it was selected: the browser started dragging the selected text instead. Pressing on the waveform no longer selects or drags text.

### Tests

- New e2e test: on a 10-second tone, choose 4–6 s and zoom. The strip shows 3.70–6.30 s, and dragging the start edge across half of it moves it by only about 1.3 s. Show whole sound zooms back out.
- Unit tests for the zoomed view (room either side, at least half a second, inside the sound).

## 2.21.0 — Edit a fresh Mimic clip in one press

Website only; works with any companion that has Edit copy. The companion reports 2.21.0 in this release, with no other change.

### Added

- **An Edit button (scissors) on every clip in Mimic's "Just mimicked" list.** Voice Replay Studio opens a new capture straight in its editor. Now one press closes the Mimic window and opens **Temp → Edit copy** for that clip, with **Start** focused. Trim it on the waveform, preview it, add an effect, and save the copy.
  - Before, this took four steps: close Mimic, open Temp, find the clip, press Edit copy.
  - Shown to the host, and to guests whom the host allows to add audio (the same rule as Edit copy).

### Tests

- New e2e test: make a clip, press Edit. Mimic closes, Edit copy for that clip opens with Start focused, and the trimmed copy saves.

## 2.20.0 — Preview an edited copy before saving it

**Update the Windows companion** to get this. With an older companion, the website hides the button, and Settings lists it under what an update adds.

### Added

- **Preview in Edit copy** (host): hear the copy before saving it, like **Play Selection** in Voice Replay Studio's editor.
  - It plays the chosen part with the chosen effect, speed and Keep pitch, exactly as **Save copy** would make it.
  - It plays in the host's browser tab on this PC only. Soundpad plays nothing, guests hear nothing, and nothing is added to Temp.
  - It plays the first 20 s of the copy, so even a long, slowed-down copy starts quickly. The companion decodes only the part it needs.
  - Press it again (**Stop preview**) to stop; **Save copy** and **Cancel** stop it too.

### Changed

- Mimic's Preview and the new one share the same playback code; Mimic's Preview works as before.

### Tests

- New C# test: a preview has the same length as the copy would (1 s cut; 2 s at half speed with Keep pitch), stops at 20 s for long or very slow copies, is refused for guests and for invalid cuts, and saves nothing.
- The Edit copy e2e test now also previews: Soundpad plays nothing and Temp gains nothing.

## 2.19.0 — Drag to choose what Edit copy keeps

Website only; works with any companion that has Edit copy. The companion reports 2.19.0 in this release, with no other change.

### Added

- **Edit copy shows the sound's waveform, and you can drag on it to choose the part to copy,** like the gold handles in Voice Replay Studio's clip editor.
  - Drag across it in either direction to choose a part, or drag the start or end edge to adjust it.
  - Start and End fill in as you drag, and typing in them moves the drawn range, so exact values and the keyboard work as before.
  - The skipped part is shaded and the copied part highlighted.
  - It uses the same waveform data as the board's sound buttons, so no companion update is needed.

### Tests

- New e2e test: drag across the middle of a 1-second tone and Start/End read 0.25 and 0.75; pull the end past the edge and it stops at 1; typing 0.5 moves the drawn edge; the copy saves.
- Unit tests for the drag rules (which edge a press grabs, crossing edges swap, staying inside the sound).
- The Mimic hotkey test now waits for each on/off command to finish before toggling again. The page ignores a toggle while the previous one is still running, so a slow run could fail. It failed once in a full local run and did not reproduce in 24 stress runs, before or after the change.

## 2.18.2 — "What's new" after an update

Website only; the companion is unchanged apart from its version number (it reports 2.18.2).

### Added

- **The website says when it has updated.** The website updates itself, so nobody saw which version they were on or what changed. Now, the first time a browser opens a newer version than it last used, one line above the board says "Soundpad Web updated to **vX.Y.Z**. See what's new", linking that version's release notes on GitHub.
  - It shows once per version, for hosts and guests, until dismissed or followed.
  - A first-ever visit shows nothing.
  - Nothing is sent anywhere: the last version seen is kept in this browser only.

### Tests

- New e2e test: a first visit shows nothing. A browser that last used an older version sees the line, with the link to `/releases/tag/vX.Y.Z`. Dismissing it keeps it gone after a reload.
- Unit test for the version comparison.


- **Tests:** a new permanent accessibility test scans the controls added in 2.15–2.18 with axe, in Chromium and WebKit, on desktop and phone width. It covers Temp's **Clear all temporary audio**, Edit copy's **Any speed** and **Keep pitch**, Mimic's **Effect** menu and the **From Voice Replay Studio** list. The first run found no violations of any level.

## 2.18.1 — Quick effects for fresh Mimic clips

Website only; works with companion 2.9 and newer. "Slow, same voice" needs 2.17. The companion reports 2.18.1 in this release.

### Added

- **An Effect menu on every clip in Mimic's "Just mimicked" list.** Pick **Chipmunk**, **Slow-mo**, **Echo** or **Slow, same voice** (0.5× with Keep pitch). It makes that copy of the whole clip and plays it through Soundpad straight away, so "grab what they just said and play it back silly" takes one click.
  - Available to the host, and to guests whom the host allows to add audio (the same permission as Edit copy).
  - The copy lands in Temp like any edited copy, for example "Effect me (echo)", and is cleared with the rest of Temp.

### Tests

- New e2e test: make a clip, choose "Slow, same voice", and the "(0.5x speed, same pitch)" copy appears and Soundpad is asked to play it.
- The weekly live-relay guest test now has the real guest apply Echo to their clip over public relays, and the host sees "Guest mimic (echo)". It passed locally.

## 2.18.0 — Clear all temporary audio, any time

**Update the Windows companion** to get this. With an older companion, the website hides the button, and Settings lists it under what an update adds.

### Added

- **Clear all temporary audio** (host, in **Temp**): removes every reversed sound, upload and edited copy from Soundpad and deletes their files, exactly as End hosting does, but hosting carries on. It asks first.
  - Soundpad's Temp category goes too, in one command; the next reverse or upload starts a fresh one.
  - Audio you kept permanently is untouched.
  - If a Temp sound is playing, it is stopped first; your own sounds are never interrupted.
  - The notice says how many files were cleared.
- Protocol: a new host-only `clearTemp` command, gated as `FEATURES.clearTemp` (2.18).

### Tests

- New C# test: two temporary uploads and one kept permanently. Clearing removes the two (files, Soundpad entries, Temp category) and keeps the permanent one and the owner's sounds. Hosting continues, and a new upload lands in a fresh Temp category. Guests can't clear. 91 C# tests.
- New e2e test: upload a sound, clear, see the notice and the sound gone from the board, and the original sounds still playable.

## 2.17.0 — Keep pitch when changing speed

**Update the Windows companion** to get this. With an older companion, the website hides the option, and Settings lists it under what an update adds.

### Added

- **Keep pitch.** In **Temp → Edit copy → Any speed**, tick **Keep pitch** to slow down or speed up a copy without the voice going deep or squeaky. The copy is named for example "Laugh (0.5x speed, same pitch)". Without the tick, speed still works like a tape, as in 2.16.
  - It uses WSOLA time-stretching: the audio is cut into overlapping 40 ms slices and laid down at the new pace. Each slice is taken from wherever, within ±10 ms, it best continues the previous one, so the waves line up instead of clicking. Stereo channels stay together.
  - Checked with real speech: Whisper transcribed the JFK sample word for word at half speed and at double speed with Keep pitch. The tape-style half-speed copy came out too deep for Whisper to recognise.
- Protocol: `trimAudio` accepts `keepPitch: true` with `action: "speed"`. The feature is gated as `FEATURES.keepPitch` (2.17).

### Tests

- New C# test: a 440 Hz tone at 0.5× and 2× with Keep pitch changes length but stays at 430–450 Hz. Tape-style moves the pitch as expected, and stereo channels stay in step. 90 C# tests.
- The C# trim test makes a "(0.5x speed, same pitch)" copy. The e2e speed test ticks Keep pitch, checks the wording and name, and saves.
- New harness tool: `SoundpadWeb.Harness.exe --stretch <in.wav> <out.wav> <speed> <keep-pitch>` writes the effect to a file.

### Also in this release


- **Every version now has a download.** The owner asked for ready-made EXEs for v2.14.3 and v2.15.4, whose CI failed when they were tagged, so no release was published for them. A new workflow, *Rebuild an old release* (`.github/workflows/rebuild-old-release.yml`, started by pushing a `rebuild-vX.Y.Z` tag), builds an old tag's exact source the way its release job did and runs the EXE smoke test. It then publishes the release, labelled as rebuilt later, with that version's changelog notes. The tests that failed back then are not re-run, and the release says so. v2.14.3 has a waveform-zoom bug fixed in 2.14.4; v2.15.4's app is the same as 2.15.5. Both are now published. All 59 version tags have a download, and docs/ROLLBACK.md links each one.

## 2.16.2 — Temp stops saying "needs attention" for old failures

**Update the Windows companion** to get this.

### Fixed

- **Old failures leave Temp once they no longer apply.** Temp's issues ("This variant could not finish", "A prepared file is waiting for a verified native entry") were never cleared. Even after reverse worked again, Temp kept saying "needs attention"; the owner's PC had over 20 such issues left from the broken versions.
  - A successful reverse now clears that sound's earlier failure, including when a retry only re-imports a file that was already made.
  - Session end drops issues about sources with nothing left pending.

### Tests

- New C# test: the mock Soundpad refuses one import, so a reverse fails with an issue. The next reverse succeeds and clears it, and session end leaves no issue for that sound. 89 C# tests.

## 2.16.1 — Snappier with big libraries

**Update the Windows companion** to get this; nothing looks different.

### Changed

- **The background library refresh is about 5 times faster, so a play never waits behind it.** The companion refreshes the library every 30 seconds, and a play pressed during that waits until it finishes.
  - The refresh used to read every category with its icon each time: about 0.3 s on the owner's Soundpad.
  - It now reads categories without icons (milliseconds), and fetches icons only when the categories changed, or every 5 minutes so a changed icon still shows up.
  - Measured on the owner's Soundpad (143 sounds), read-only: 309 ms for the first refresh, then about 60 ms.

### Tests

- New C# test: an unchanged library refresh skips the icon read, and a changed category brings it back. 88 C# tests.
- `SoundpadWeb.Harness.exe --refresh-bench` times refreshes on the real Soundpad, read-only, using a throwaway data folder.

## 2.16.0 — Any speed, no reverse limits, everything gone when you close

**Update the Windows companion** to get this. With an older companion, the website hides the option, and Settings lists it under what an update adds.

### Added

- **Any speed, as slow or as fast as you want.** In **Temp → Edit copy**, the Effect list now has **Any speed**.
  - Pick any speed from **0.05×** (20 times slower) to **20×** (20 times faster), with a slider or an exact number.
  - The slider is logarithmic, so slowing down gets as much room as speeding up.
  - The form shows how long the copy will be. The copy is saved as a new Temp sound, named for example "Laugh (0.5x speed)". The original is not changed.
  - It works like a tape: pitch moves with speed (slower is deeper, faster is higher).
  - Soundpad's remote control has no playback-speed setting, so speed applies to copies, like Chipmunk and Slow-mo.
  - Very slow copies are long, so Temp's 16 MiB limit still applies. Long copies are reduced in quality to fit. If even that is too long, the companion says how many seconds fit at that speed.
- **Faster speeds sound cleaner.** When a copy is sped up (including Chipmunk), the companion now averages the samples it skips instead of dropping them, which avoids harsh aliasing at high speeds.
- **Reverse has no size or count limits anymore.**
  - Gone: the ten-minute and 64 MiB decoded caps, the 30-second conversion timeout (now 10 minutes), and Temp's 32-file / 256 MiB total for reversed copies. You can reverse long sounds and as many as you like.
  - What stays: one file per reversed sound can be up to **512 MiB**, about 46 minutes of 48 kHz stereo. Every companion version checks that number in its saved journal, so a larger file would make an older companion refuse the journal, and going back to an older version would stop working.
  - Uploads and Edit copy keep their 16 MiB per sound, for the same rollback reason.
- **Everything temporary disappears when hosting ends, and when you close the companion window with the X.**
  - Before, cleanup deleted the companion's files but left every reversed or uploaded entry in Soundpad's Temp category. Soundpad can only remove "the selected entry", so it asked you to remove them by hand.
  - Now, at **End hosting**, the companion removes its own Temp category in one command. Measured on Soundpad 4.0.35, that takes the category's sounds out of Soundpad's list too (the files are not touched). Then it deletes its files.
  - It only does this when every entry in that category is one of its own temporary files and nothing is playing. If you put something of your own in there, it falls back to the old careful way.
  - Closing the window with the X, or logging off or shutting down, now runs the same cleanup in the few seconds Windows allows. If a Temp sound is playing, it is stopped first; your own sounds are never interrupted. Anything not finished in time is cleaned up on the next start, as before.
  - Each session starts a fresh Temp category.
- **The journal no longer fills up.** At session end, records of files that are already gone are forgotten. Before, a sound you reversed often eventually hit the 64-version limit for good, and old uploads counted toward the 2,048-source limit.
- **Scrolling the board no longer zooms the waveforms it passes over.** The mouse wheel now scrolls the page until the pointer has rested on a waveform for one second, with no scrolling in between. Before, scrolling down the board zoomed whichever tile's waveform slid under the pointer. Once you've paused on a waveform, the wheel zooms it as before. This covers tiles and the Waveforms view.
- Protocol: `trimAudio` accepts `action: "speed"` with `speed` (0.05 to 20). The feature is gated as `FEATURES.speed` (2.16).

### Tests

- C# tests for cleanup:
  - session end removes the whole Temp category and its file without any selection command, and forgets the record;
  - a Temp category holding someone else's sound is kept, with the old manual-removal note;
  - nothing is removed while a sound plays;
  - a locked file loses its Soundpad entry, is kept and retried, then deleted.
- The mock Soundpad's category removal now also removes that category's sounds, as measured on the real one.
- **Checked on the owner's PC:**
  - a harness probe confirmed the window-close handler runs when the window is closed (the same message as the X);
  - a real Soundpad probe added one test sound in its own category, removed the category, and saw the sound leave the main list, leaving the library exactly as before (122 sounds, 9 categories).
- C# tests for speed:
  - 4× slower is 4× longer, and 4× faster is a quarter as long;
  - fast speeds average the skipped samples;
  - a 3 s source at 0.5× becomes a 6 s "(0.5x speed)" copy;
  - missing and out-of-range speeds are refused.
- An e2e test sets the exact speed, checks the predicted length and name, checks the slider's ends (0.05× and 20×), and saves the copy.
- Unit tests cover the new feature gate and the wheel rule.
- New e2e test, in both browsers: a wheel right after arriving on a waveform doesn't zoom it, and after a quiet second it does. It fails on 2.15.9. The three existing wheel-zoom tests now pause for a second first.

## 2.15.9 — Reverse works again, no more Temp category spam, instant playback

**Update the Windows companion** (SoundpadWeb.exe below). Then, if Soundpad shows many "Temp Â· Soundpad Web" categories, open **Temp** in the website and press **Remove duplicate categories**.

### Fixed

- **Reverse (and session uploads) work again, and Soundpad no longer fills up with Temp categories.**
  - Soundpad reads the companion's commands in the Windows ANSI code page (code page 1252 on most English PCs), but the companion sent UTF-8.
  - So the Temp category "Temp · Soundpad Web [...]" arrived as "Temp Â· Soundpad Web [...]".
  - The companion never recognised that category, so every reverse and every recovery pass created another one. On the owner's PC there were **1,507**.
  - Reverse waits for that category, so it never finished.
  - Now commands are sent in the code page Soundpad expects, and text it can't carry is refused with a clear message instead of being garbled. Characters such as emoji in managed file names become `_`, and new app category names are plain ASCII ("Temp - Soundpad Web [...]").
- **The create loop can't come back.** If garbled copies of the app's category exist, the companion stops instead of making another one. **Temp** then shows how many there are, with a **Remove duplicate categories** button.
  - The button removes only empty categories carrying the app's own tag, never your categories or sounds.
  - It works from the highest index down and checks Soundpad after every 50.
  - On the owner's PC it removed all 1,507 and left the other 8 categories and every sound untouched.
- **Playing a sound is immediate and doesn't fail.**
  - Every press used to re-read the whole library first: the sound list plus every category with icons, about 1.4 MB and 0.35 s on a large library.
  - It also refused to play ("Soundpad library changed. Try the sound again") whenever anything in the library had changed since the page last looked. With the Temp categories piling up, that was nearly always.
  - Now a play reads only the sound list (about 10 ms), and re-reads the rest only if that list changed.
  - A sound that moved plays at its new place, never a stale one; reverse works the same way.
- **Temp says why something failed.** Issues used to show only a generic line ("This variant could not finish"); they now include the actual reason.

### Changed

- **Connected guests are shown at the top of the board,** under the title, with their names (up to four, then "+N more"), instead of a count in the footer.
- **Every GitHub release now carries its version number and what changed.** The title and notes come from this changelog ("Soundpad Web v2.15.9 — ..."), instead of GitHub's list of commits. CI fails before any tag if the changelog has no section for the version being built.

### Tests

- The mock Soundpad now decodes commands in code page 1252, like the real one; decoding UTF-8 is what hid this bug.
- New C# tests, each failing on 2.15.8:
  - a "·" category name reaches Soundpad intact and is created once;
  - garbled copies stop the create loop, and only the empty ones are removed;
  - a play with an unchanged library reads no categories, and an added category doesn't stop a play.
- Updated C# test: a moved sound plays at its fresh index. There are now 86 C# tests.
- New e2e tests: the Temp cleanup button, and connected guests at the top.
- New unit tests for the release notes.
- **Checked on the owner's real Soundpad (4.0.35), with permission:**
  - the cleanup removed the 1,507 duplicates;
  - the companion then created its Temp category once, under exactly the expected name, and found it again instead of making another.
  - Not checked live: a full reverse, which would have played audio through the mic route.

### Reports

- Reports now go in a local `reports/` folder (bugs, features, spam, done), which is not uploaded. `CLAUDE.md` tells Claude to read it before every change.

## 2.15.8 — A taken hotkey points at the key picker

**Update the Windows companion** to get the new wording.

### Changed

- **When another app already uses the hotkey's key, the message says what to do.** It used to say "Close it or keep using the Mimic window", written before the key could be changed. Since 2.14 the key can be changed, so the message now says "Pick another key next to Hotkey in the Mimic window, or close that app".

### Tests

- The Mimic e2e test checks the new wording when the harness's taken key (F9) is chosen. 82 C# tests pass.

## 2.15.7 — Touch wording in Mimic on phones

Website only; the companion is unchanged apart from its version number (it reports 2.15.7).

### Changed

- **Mimic's help speaks touch on phones and tablets.** It used to tell everyone to press 3, 5, 0, Enter, Shift+Enter and Esc, keys a phone doesn't have. On touch screens it now says to drag across the waveform, tap a transcript line or a Last 3/5/10 s button, then Mimic it. With a mouse, the keyboard shortcuts are shown as before. The choice is made by the device's pointer (`hover: none` and `pointer: coarse`), not by screen width.

### Tests

- Two new e2e tests, in both browsers: with a mouse the shortcuts are shown and the touch wording is hidden; on an emulated touch phone it's the other way round.

## 2.15.6 — Guest phones: no false "Too many requests", notices out of the way

Website only; the companion is unchanged apart from its version number (it reports 2.15.6). Found by screenshotting a guest on a phone over the real relays.

### Fixed

- **Guests no longer get "Too many requests" right after making a clip.** Processing a clip updates Temp several times within a second, and the Temp panel fetched its list on every update. Measured: 9 requests in 0.4 s, which with Mimic's polling went over the host's limit of 12 messages per guest per second. The panel now waits until updates settle (0.3 s) and fetches once. Measured afterwards: 1 request.
- **On phones, notices no longer cover an open dialog's title and close button.** A dialog fills a phone screen, and the close button is the only way out without a keyboard. While a dialog is open on a narrow screen, notices appear at the bottom instead.

### Tests

- The weekly live-relay guest test now checks that the guest sees no "Too many requests" after making a clip.
- The phone Mimic test checks that the "Mimic is listening" notice sits below the dialog's close button. It fails on 2.15.5.


- **CLAUDE.md:** releases are now tagged only after `main` passes CI, so a failing build can't leave another version without a download.

## 2.15.5 — 2.15.4, released

**Update the Windows companion** for 2.15.4's fixes. The app code is identical to 2.15.4 apart from the version number.

- **2.15.4's CI failed on its own new test,** so no EXE was published. The test turned on Mimic transcripts and spoke straight away. Transcripts start from "now" once their loop picks up the buffer, every 0.5 s, so on a slow runner the speech came too early and was rightly skipped. The test now waits for the loop before speaking. Its fake speech engine also fails only after it has produced a line. The test fails on 2.15.3 and passes on 2.15.5, 5 runs out of 5.

## 2.15.4 — Security review follow-ups

**Update the Windows companion** to get these; nothing looks different.

A fresh security review of everything guests can reach since 2.12.3 found **no vulnerabilities**. It confirmed:
- guests can't reach host-only commands, including the Voice Replay Studio list and import and the hotkey settings;
- guests can't spoof who sent a command;
- the existing rate limits cap what one guest can do;
- Mimic audio reaches guests only as waveform levels.

It suggested three hardening steps, all done here:

### Fixed

- **If the speech engine fails, its transcript lines are cleared**, as when transcripts are turned off. Before, the last 30 s of recognised text kept going to guests while the status said "Transcripts stopped".
- **An unreadable Voice Replay Studio clip gets an answer.** A WAV that Windows won't let the companion read (locked, or no access) left the host's import waiting with no reply. It now says the file "couldn't be read".
- **docs/MIMIC.md** now says that hotkey clips are named after their transcript words while transcripts are on, and that guests see those names.

### Tests

- New C# test: a speech engine that recognises one line and then crashes leaves no lines behind. It fails on 2.15.3. There are now 82 C# tests.


### Documentation

- **docs/ROLLBACK.md:** v2.14.3 now links its source-code zip, which GitHub provides for every tag. That version can be downloaded and built even though it has no prebuilt EXE. The row explains why: its CI caught a real bug, fixed in 2.14.4.

## 2.15.3 — Import & play, and a real-chain check

Website only; works with any 2.15 companion, which reports 2.15.3 in this release.

### Added

- **Import & play** next to each clip in **From Voice Replay Studio**: it imports the clip and plays it through Soundpad at once, like **Mimic & play**. On a phone the two buttons now sit together under the clip's title, instead of squeezing it to a few letters.

### Checked

- The silent real-chain self-test (`SoundpadWeb.Harness.exe --mimic-silent-selftest jfk.wav`) still passes on the current companion. Real app-loopback capture at stream volume 0 came through (peak 783/1000), and real Whisper transcribed the JFK sample word for word. This confirms the companion changes since 2.12.3 left the actual capture path intact.

### Tests

- The Voice Replay Studio e2e test now also uses **Import & play** and checks that Soundpad was asked to play.

## 2.15.2 — Tell the host what a companion update adds

Website only; the companion is unchanged apart from its version number (it reports 2.15.2).

### Added

- **Settings tells the host what a newer companion would add.** Controls that need a newer companion are hidden on older ones, so a host who never updated had no way to know they existed. Now **Session & access** names them, for example "Your Windows companion is 2.13.0. A newer one adds the hotkey key choice (F6–F10), importing from Voice Replay Studio", with a download link. The note is host only, appears only in Settings, never on the board, and only once the companion has reported its version. It comes from the same `FEATURES` table that hides those controls, so the two always agree.

### Tests

- A unit test covers `missingFeatures()`.
- New e2e tests:
  - with the companion made to report 2.13.0, Settings names exactly those two features and links the download, and the Mimic window hides both controls;
  - the current companion gets no note.
- Two more upload waits allow 25 s for decoding on slow runners.

## 2.15.1 — Review fixes for the Voice Replay Studio import

**Update the Windows companion** for the first and last two fixes. The rest are website fixes and apply at once.

An independent review of 2.15.0 found no way for a guest to list or import clips, and no write to Voice Replay Studio's folder. It did find these problems:

### Fixed

- **Importing a clip no longer holds up the host's other commands.** The import ran on the host's command connection, so Stop, Play or Seek waited until a large clip was decoded. Listing and importing now run in the background, like Mimic clips.
- **The list loads after a reconnect.** Opening the section while the companion was disconnected left "Loading…" on screen for good. It now says it is waiting, and loads when the companion connects.
- **A library that can't be read says so** ("couldn't be read", with **Try again**) instead of claiming it has no clips. The notice still gives the reason.
- **Refresh** shows clips Voice Replay Studio saved while the list was open. The clip being imported is marked busy for screen readers.
- **A slash in a clip's title no longer cuts the sound's name short.** "AC/DC" used to arrive in Temp as "DC"; it is now "AC-DC".
- **Both files are read with a size cap**, even if one grows while it's read: clips at 16 MiB, `library.json` at 32 MiB.

### Tests

- The C# Voice Replay Studio test imports an "AC/DC" clip and expects "AC-DC". The e2e test uses Refresh.

## 2.15.0 — Import clips from Voice Replay Studio

**Update the Windows companion** to get this; older companions don't show the new section.

### Added

- **Bring Voice Replay Studio clips into a session.** The Mimic window has a new host-only section, **From Voice Replay Studio**. It lists the clips that the desktop app (`mimic/`) saved on this PC, newest first, with title, length, date and transcript, and a search box when there are many. **Import** copies one into Temp, where everyone can play it like any Mimic clip. Until now those clips lived only in the desktop app; now its library feeds the shared soundboard.
  - **Read-only:** the companion reads Voice Replay Studio's `library.json` and the clip's WAV, and never writes, moves or locks them, so that app can stay open. A damaged library is reported and left untouched.
  - **Host only:** guests can neither list nor import, and never see the titles. Only WAV clips up to Temp's 16 MiB limit are imported, through the same checks as uploads.
  - **Protocol:** new host-only `mimicLibrary` and `mimicLibraryImport` ops, gated as `FEATURES.voiceReplayLibrary` (2.15). Nothing new is saved by the companion, so rolling back is unaffected.

### Tests

- New C# test against a temporary library:
  - lists a clip and skips one whose file is missing;
  - imports a clip into Temp under its title;
  - refuses guests, MP3s, missing files and unknown ids;
  - proves `library.json` is never written;
  - reports a damaged library without touching it.

  81 C# tests pass.
- The e2e harness uses a fixture Voice Replay Studio library (never the real one). A new e2e test lists its two clips, imports one, plays it from Temp, and runs axe on the open section. The live guest test checks that guests don't see the section.
- `SoundpadWeb.Harness.exe --vrs-library` reads this PC's real library read-only and prints only counts. On the owner's PC: 2 clips, 20 s, parsed correctly.

### Also in this release

- **README:** the user guide now covers the hotkey key choice (F6–F10), waveform zoom, which browsers guests can use, and what "WebRTC blocked" means.
- **Tests:** two more timing assumptions fixed after GitHub's WebKit runner needed retries. The Mimic Edit copy check now also accepts a 3.00 s clip, and the session-upload test allows 25 s for decoding the upload.

## 2.14.5 — CI upkeep

Nothing changes in the website or companion; the companion reports 2.14.5. This release exists so the new CI actions publish a release once.

- **CI: GitHub Actions moved to their current major versions** (checkout v7, setup-node v7, setup-dotnet v6, upload-artifact v7, download-artifact v8, configure-pages v6, upload-pages-artifact v5). The v4 actions ran on the deprecated Node.js 20, and every run warned about it. Each release used is at least two weeks old.
- **Tests:** two tests stabilised for GitHub's slower runners. The permissions test now waits a second for the companion to store a change before reloading. The no-WebRTC guest test is skipped in WebKit, where on CI the guest's name prompt never appeared (Chromium still covers it). The first CI run with the new actions passed apart from these; Pages deployed.
- **docs/ROLLBACK.md:** the 2.14.3 row now says which downloads to use instead. It has no release of its own; its companion is identical to 2.14.2's apart from the version number.

## 2.14.4 — Wheel zoom on waveforms that load late

Website only; the companion is unchanged apart from its version number (it reports 2.14.4).

### Fixed

- **Scrolling to zoom a waveform now always works.** The zoom logic attached its wheel listener when a sound first appeared. If the waveform data arrived afterwards (likely on a busy PC or slow link) and reported the same length the library already knew, the waveform surface mounted without a listener. Scrolling then did nothing, both in the Waveforms view and on the small waveforms of tiles without artwork. The listener now follows whichever surface is mounted. The new WebKit step in CI found this on GitHub's slower runner; it was not a Safari issue.

### Tests and CI

- New e2e test: waveform requests are held back 1.5 s, then wheel zoom must show "Fit waveform". It fails on 2.14.3.
- The Mimic e2e test accepted only clips just under 3 s; on a slower machine the clip is exactly 3 s, so both are now accepted.
- The no-WebRTC guest test waits up to 30 s for the name prompt in its fresh browser context.
- **2.14.3 has no GitHub release.** Its CI run failed on these points, so no EXE was published, but its website changes went live. 2.14.4 contains everything from 2.14.3.

## 2.14.3 — Safari checks and a clear message when WebRTC is blocked

Website only; the companion is unchanged apart from its version number (it reports 2.14.3).

### Fixed

- **A guest whose browser has no WebRTC is told why straight away.** WebRTC can be turned off by a browser setting or a privacy extension, or missing from an old browser. Before, such a guest waited 25 s for a generic "Host unavailable … can be blocked". Now the header says **WebRTC blocked** at once, and the board explains what to change.
- **Closing a dialog returns focus to the button that opened it in Safari too.** Safari doesn't focus a button when you click it, so focus used to drop to the top of the page. The dialog now falls back to the button that was clicked.
- **If the browser refuses to play Mimic's Preview,** the window now says so ("press Preview again") instead of silently ending the preview.

### Tests and CI

- The whole e2e suite now also runs in **WebKit**, Safari's engine, on every CI build, because guests often join from iPhones and Macs. Locally it gives 32 passed and 6 skipped:
  - Skipped as everywhere else: the opt-in live-relay and deployed-site tests.
  - Also skipped: the HTTPS-to-loopback test, because hosts run on Windows, where there is no Safari.
  - Preview is likewise not checked in WebKit.
- Playwright's Windows WebKit has no WebRTC, so the live guest tests stay Chromium-only. Real Safari has WebRTC.
- New e2e test, in both browsers: a guest without WebRTC sees the message within 3 seconds.
- Firefox couldn't be added: Windows on the owner's PC refuses to start Playwright's Firefox build, probably Smart App Control. That setting was left unchanged.

## 2.14.2 — A taken hotkey key is reported straight away

**Update the Windows companion** to get this; with 2.14.0–2.14.1 the hotkey works as before.

### Fixed

- **Choosing a key another app owns is refused while the hotkey is off too.** Before, the companion only checked the key when the hotkey was turned on. Picking F9 while it was off was accepted, and ticking **Hotkey** then failed and left it off on F9. Now the companion briefly registers the key with Windows when it is chosen and releases it again, so the window says straight away that the key is taken and keeps the previous one.

### Tests

- The C# hotkey test and the Mimic e2e test pick a taken key (F9) while the hotkey is off and check that it is refused and the key stays F6.
- In one local run the "landing page" e2e test failed while the dev server compiled its first page; it passed 3 of 3 on rerun and in the next full run. CI retries once.

## 2.14.1 — The Mimic hotkey remembers your key

Website only; the companion is unchanged apart from its version number (it reports 2.14.1).

### Changed

- **The host's browser remembers the hotkey's key and length.** The companion forgets them when it closes, so someone who needs F8 used to pick it again every session. Now ticking **Hotkey** turns it on and then puts back this browser's last key and length. The hotkey is turned on first and moved afterwards, so a remembered key that another app now owns is reported and the hotkey stays on its working key. Storage problems are ignored, and the companion's defaults (F6, 10 s) still work. Nothing new is saved on the host PC.

### Tests

- New e2e test: with F7 and 15 s remembered, ticking Hotkey gives "Press F7 … 15 s"; with a remembered key the harness treats as taken (F9), the error is shown and the hotkey stays on F7.
- Fixed an xUnit analyzer warning (xUnit2029) in the Mimic preview test.

## 2.14.0 — Choose the Mimic hotkey

**Update the Windows companion** to get this; with an older companion the window keeps F6, as before.

### Added

- **Pick the hotkey: F6, F7, F8, F9 or F10.** Before, if a game or another app (for example Voice Replay Studio) already used F6, the Mimic window could only say so. Now a list next to **Hotkey** offers F7–F10.
  - Changing the key while the hotkey is on moves it straight away.
  - The new key is registered before the old one is released, so if the new one is taken, the window says so and the old key keeps working.
  - The choice lasts until the companion closes. Nothing is saved to disk, so rolling back is unaffected.
  - Host only, like the rest of the hotkey settings.
- Protocol: `mimicHotkey` accepts `action: "key"` with `name` `F6`–`F10`; the Mimic snapshot adds `hotkeyKey`. The website shows the list only for companions 2.14 and newer (`FEATURES.hotkeyKey`). Older websites ignore the new snapshot field, so mixed versions work in both directions.

### Tests

- A new C# test changes the key while off and while on, checks that a taken key leaves the working one registered, and checks that only listed keys and only the host are accepted (80 C# tests).
- The Mimic e2e test switches to F8 and then tries F9, which the harness pretends another app owns.
- `SoundpadWeb.Harness.exe --hotkey-probe` now registers and releases each of F6–F10 with Windows; on the owner's PC all five were free.

## 2.13.5 — Dependency check

**Optional companion update:** it picks up a Microsoft servicing patch; nothing looks or behaves differently.

### Changed

- Companion: `System.Drawing.Common` 10.0.0 → 10.0.13, Microsoft's monthly servicing release from 2026-10-13. It is used to read and resize sound icons. The newer 10.0.14 is under two weeks old, so it was left for later. 79 C# tests and all e2e tests pass, including the icon tests.
- Checked and deliberately not updated:
  - ESLint 10.14.0 is a day old.
  - lucide-react 1.x and TypeScript 7 are major versions with no benefit here.
  - NuGet test-tool major versions.
  - `minimatch` 11, which fixes the dev-only advisory, is 4 days old and ESLint doesn't use it yet.
  - `npm audit --omit=dev` reports 0 vulnerabilities in what the website ships.

## 2.13.4 — Review fixes for zoom and Temp

Website only; the companion is unchanged apart from its version number (it reports 2.13.4). An independent review of every change since 2.12.3 found these two problems; nothing else.

### Fixed

- **Mimic zoom: a drag that starts while the view is widened now stays where you point.** When a selection had widened the zoomed view to the whole buffer, starting a new drag snapped the view back to the zoom mid-drag. The drag then ended on a different moment from the one under the pointer (for example −6 s → −1 s instead of −6 s → −3 s). The view is now held from press to release, and a cancelled touch drag also resets cleanly. This was introduced in 2.13.0.
- **Temp: Edit copy, Keep permanently and the save form's Cancel ignore presses while another Temp command runs,** as they did before 2.13.3. Otherwise a form opened during a running Save copy or permanent save was closed again, losing what had been typed, when that command finished.

### Tests

- The Mimic e2e test drags inside a widened zoomed view and checks the exact range; it fails on 2.13.3.

## 2.13.3 — Keyboard focus in Temp, Refresh library and the icon editor

Website only; the companion is unchanged apart from its version number (it reports 2.13.3).

### Fixed

- **The last controls that dropped keyboard focus while busy now keep it.** These are Temp's Play normal/reversed, Edit copy, Save copy, Keep permanently, Retry preparation, Remove temporary audio, Retry recovery & cleanup, Confirm permanent save and Cancel, plus **Refresh library** and the icon editor's Save, Remove and Cancel. Each used to disable itself, or the whole panel, while a command ran, which sent keyboard users back to the top of the page on a slow link. As in 2.13.2, they now look busy and ignore presses until the companion answers, while keeping focus. Remove temporary audio no longer asks for confirmation while another Temp command is still running.

### Tests

- The slow-connection e2e test now also presses Temp's Play normal and Refresh library from the keyboard; it fails on 2.13.2.

## 2.13.2 — Keyboard focus stays on Play, Reverse and the Kick counter

Website only; the companion is unchanged apart from its version number (it reports 2.13.2).

### Fixed

- **Pressing Play from start (or the artwork), Reverse and play, or the Kick counter buttons with the keyboard no longer throws focus back to the top of the page.** It is the same problem 2.13.1 fixed in Mimic, found by checking the rest of the app: each button disabled itself while its command was running, and Chrome takes focus away from a disabled control. On a slow link, a keyboard user who pressed Play landed on the page body and had to tab all the way back. These buttons now look busy (dimmed, with a progress cursor) and ignore repeat presses until the answer arrives, but keep focus. **Mimic it** and **Mimic & play** got the same treatment.

### Tests

- The slow-connection e2e test (every companion message delayed 600 ms) now also presses Play from start, the Kick counter and Reverse and play from the keyboard and checks focus through each command. It fails on 2.13.1.

## 2.13.1 — Mimic keeps keyboard focus on slow connections

Website only; the companion is unchanged apart from its version number (it reports 2.13.1).

### Fixed

- **Ticking Transcript or F6 hotkey, or changing F6's length, no longer loses keyboard focus** while the companion answers. These controls used to be disabled during each command, and Chrome moves focus away from a control that becomes disabled. On a quick PC the command usually finished first, but on a busy PC or slow link keyboard users ended up back at the top of the page, and Mimic's number-key shortcuts stopped working. They are now marked busy (`aria-disabled`) and ignore input until the answer arrives, but keep focus. This was also the cause of an occasional failure in the Mimic keyboard e2e test.

### Tests

- New e2e test delays every companion message by 600 ms and checks that F6 stays focused through the command. It fails on 2.13.0 and passes now.

## 2.13.0 — Mimic waveform zoom

Website only; the companion is unchanged apart from its version number (it reports 2.13.0).

### Added

- **Zoom the Mimic waveform.** Buttons under the waveform show the last **30 s**, **10 s** or **5 s**, like Voice Replay Studio's Live Buffer views. Zoomed in, the same drag covers less time, so a short moment is easier to pick, especially on a phone. Ticks mark every second when zoomed in. Each browser remembers its zoom, and a selection that starts before the zoomed view (an older transcript line, or a start nudged back) shows the whole buffer so it is never off-screen. Nothing changes for the companion or guests' permissions: the waveform still uses the same 300 shared levels.

### Tests

- Unit test for the visible-range rule; the Mimic e2e test drags on the 10 s view (−4.0 s → −1.0 s) and checks that a selection reaching past it widens the view.

## 2.12.5 — Guest accessibility

Website fixes; the companion is unchanged apart from its version number, which is back in step with the website (it reports 2.12.5).

### Fixed

- **Mimic's "Just mimicked" list:** each Replay button's spoken name is now "Replay <clip> · <who>", matching what is on screen, instead of a shorter label that hid who made the clip. Screen-reader and voice-control users now get the same information as everyone else.
- **Gamer-name dialog:** the dialog role moved from the `<form>` (where it is not allowed) to a wrapper around it. The form still submits with Enter and looks pixel-identical.

### Tests

- Guests only exist over the real Nostr relays, so the weekly live-relay test now also runs axe on the guest's view with Mimic open and fails on any serious or critical finding. A one-off live scan of the gamer-name dialog, the guest board, guest Mimic, the phone layout and guest settings found only the two issues above.

## 2.12.4 — Accessibility in dialogs and player tools

Website only; no companion update needed. The companion in this release is unchanged and still reports itself as 2.12.3.

### Fixed

- **Details view while a sound plays.** The progress bar sat directly in the table row, outside any cell, so screen readers met a row with a stray child (axe rated it critical). It now sits inside the sound's cell, under its button, and each details row is a plain row element instead of an `<article>`.
- **"Seek here" in the player's Edit tools** is now announced as "Seek here: selected position …", so its spoken name starts with the words on the button (voice control users can say what they see).

### Tests

- A second permanent accessibility test scans the icon editor, statistics, My layout, organize, the waveforms view, the details view while playing, the player Edit tools and the Temp Edit copy form, and fails on any serious or critical axe finding.

- **Weekly live-relay check.** New workflow `.github/workflows/live-relay.yml` runs the four real-guest tests over the public Nostr relays every Monday, on demand, and whenever the workflow changes. Those tests cover joining, playback, the counter, revoking invites, uploads, shared layouts, Spotify and Mimic. Normal CI deliberately never depends on public relays, so before this a relay or library change that broke joining would only have shown up in a real session.

## 2.12.3 — Lighter Mimic capture

**Update the Windows companion** to get this; nothing looks different.

### Changed

- Mimic capture now mixes the usual Windows format (32-bit float, 48 kHz) straight from each 10 ms packet into one reused buffer, instead of building stream objects and lists for every packet. Other formats keep the old path. Measured with the new `SoundpadWeb.Harness.exe --capture-bench`, 20 s of real app-loopback capture, three runs each:
  - allocations fell from 54.4 MiB to 7.8 MiB (−86%), and gen-0 garbage collections from 4 to 0, in every run;
  - CPU stayed about 1–2% of one core in both versions, including the bench's own silent tone generator, and the difference was within run-to-run noise (old 0.7 / 2.0 / 1.6%, new 2.0 / 2.1 / 2.1%). So this is a memory improvement, not a CPU one.
- The silent real-capture self-test still transcribes the JFK sample exactly; 79 C# tests and the Mimic e2e tests pass.

### Documentation

- **README rewritten as a user guide.** It covers what Soundpad Web is, hosting, joining, Mimic, features, privacy, troubleshooting (now including the Temp cleanup note and tab sleep), going back to an older version, and a map of the repository. It no longer names a "current version", which had gone stale ("2.9 is the current update" through 2.12); it links the changelog instead. Developer material moved unchanged to the new [docs/DEVELOPERS.md](docs/DEVELOPERS.md): building, testing, publishing a fork, companion options, network notes, where data is kept, and technical limitations. The old "2.3 notes" section moved unchanged to [docs/V2.3.md](docs/V2.3.md). All relative links and anchors were checked.

## 2.12.2 — Everyone is told when Mimic listens

Website update; works with any Mimic-capable companion. The companion is rebuilt as 2.12.2 with no code changes.

### Added

- Because Mimic records other people's voices, every participant now gets a notice when it starts listening, including which source, and when it stops ("Mimic stopped listening."). Anyone who joins while Mimic is on is told immediately ("The host's Mimic is listening to … (the last 30 s, kept on their PC)"). Before this, the only sign was the small header badge.
- Tests: the host flow checks the start and stop notices; the live-relay guest test checks that a guest joining mid-listen is told.

## 2.12.1 — Mimic window code split up

Website maintenance; the companion is rebuilt as 2.12.1 with no code changes.

### Changed

- `MimicPanel.tsx` had grown to 740 lines over a dozen releases. It is now split into focused pieces:
  - the panel itself (303 lines): live view, selection, transcript, preview, clipping and shortcuts;
  - `components/mimic/MimicHostControls.tsx`: source, start/stop, transcripts, F6 and its length;
  - `components/mimic/MimicWave.tsx`: the waveform canvas;
  - `components/mimic/MimicRecent.tsx`: Just mimicked;
  - `mimic/selection.ts`: pure selection, transcript and source helpers, which the unit tests now import directly.
- The only visible difference: the transcript list now appears once the companion confirms transcripts are on, instead of the instant the checkbox is ticked. The checkbox itself still responds immediately.
- Verified: unit 60/60, e2e 29/29, all 4 live-relay guest tests.

## 2.12.0 — Private preview

**Update the Windows companion** for this; with older companions the button is hidden.

### Added

- **Preview** (host only) in the Mimic window: hear the selected moment on your own PC before saving it, as Voice Replay Studio's Play Selection allowed. Until now the only way to hear a selection was **Mimic & play**, which sends it through Soundpad into voice chat.
- How it stays private: the companion hands the selection to the host's own browser tab over the local loopback connection, and the browser plays it on that PC's output, normally your headphones. It never goes through Soundpad or to guests, and it saves nothing. Playing it on a Windows output from the companion was rejected, because the default device can be routed into voice chat on some setups, which is why Voice Replay Studio needed a monitor-device picker.
- Tests: C# (exact 2.5 s, guest refused, nothing saved; 79 total); e2e (the preview plays in the browser and Soundpad plays nothing); the live-relay guest test confirms guests have no Preview.

## 2.11.2 — Hear it backwards

Website update; the companion is rebuilt as 2.11.2 with no code changes.

### Added

- **Reverse** next to each clip in **Just mimicked**: hear what someone just said backwards in one click. It uses Temp's existing reverse, under the same permissions: guests need playback, plus reverse generation unless a reversed version already exists. Covered by the host e2e test, which also checks that the reversed clip plays. The phone-fit and accessibility tests pass with the new button.

### Dependencies (reviewed, deliberately unchanged)

- `npm audit` reports 11 moderate advisories, all from one issue: `minimatch` < 11.0.1 ([GHSA-g3hv-75wg-m249](https://github.com/advisories/GHSA-g3hv-75wg-m249), a denial of service through crafted glob patterns) inside ESLint and typescript-eslint. These are development-only and never shipped. The fix was published on 2026-10-27, and no ESLint or typescript-eslint release accepts it yet; both require `minimatch` ^10. Forcing it would be a major-version override under tools not built for it, using a package younger than this project's two-week rule, to address a DoS that needs untrusted glob patterns. Revisit when ESLint depends on `minimatch` 11 and that release is at least two weeks old. The other outdated packages are major upgrades (TypeScript 7, `@types/node` 26, lucide-react 1.x) or a one-day-old ESLint patch.

## 2.11.1 — Mimic on phones

Website update; the companion is rebuilt as 2.11.1 with no code changes.

### Fixed

- **On phones, the Settings button was pushed off-screen while Mimic was on.** The "Mimic on" badge made the header 44 px wider than a 390 px screen, so neither host nor guests could reach Settings. On narrow screens the badge is now just its pulsing dot, and screen readers still hear "Mimic on".
- **A long clip name in Just mimicked widened the whole Mimic window** on phones, cutting off the waveform and buttons. Names now end in "…" on one line, and the window's layout can no longer grow past its edge.
- **The 3 / 5 / 0 shortcuts stopped working after ticking Transcript or F6,** because focus stays on the checkbox and any input counted as typing. Only text fields and menus block shortcuts now.

### Added

- e2e: a phone-width Mimic test (no page or window overflow, Settings stays on screen) and a keyboard test step with focus on a checkbox. Full e2e 29/29.

## 2.11.0 — Choose how much F6 saves

**Update the Windows companion** for this; with older companions the website hides the choice.

### Added

- **F6 length:** next to the F6 hotkey in the Mimic window, the host picks how much F6 saves: 3, 5, 10, 15 or 30 seconds (default 10). These are the same choices Voice Replay Studio offered. The choice applies immediately, is shown in the status line, and is logged in the companion window.
- Tests: C# (3 s save, invalid length refused, host only); e2e picks 5 s; unit gate `hotkeyLength` (2.11).

### Documentation

- **docs/MIMIC.md rewritten** into one current guide after about fifteen incremental edits since 2.4. It adds a complete file map, the Edit copy rules (including how long copies are fitted into Temp), the guest rate limits, a companion-version table, the full protocol (`trimAudio`, capture `ringId`), and all PC checks in one place. The README's privacy link now points at the renamed section.

## 2.10.4 — Mimic in the companion window

**Update the Windows companion** to get this; the website is unchanged.

### Added

- The companion's console window, its only interface on the host PC, now logs Mimic events with the time:
  - listening started on a source, or stopped (with the buffer discarded);
  - each clip, with who made it, its name and its length ("Alice mimicked "…" (3.0 s), now in Temp"), including clips saved with F6 mid-game;
  - an F6 press that could not save, for example when Mimic was not listening;
  - transcripts and the F6 hotkey turned on or off;
  - hosting ended while listening.

  It is useful on a second monitor while gaming.

### Verified

- Guests cannot flood Temp with clips: Mimic clips and edited copies go through the same per-guest limit as uploads (6 new files per minute), within Temp's 32-file / 256 MiB cap. No change was needed.
- The hotkey test now checks the console lines (78 C# tests).

## 2.10.3 — Accessibility audit

Website update; the companion is rebuilt as 2.10.3 with no code changes.

### Fixed

An automated axe-core audit covered the landing page, board, Temp, Settings, the Mimic window with transcripts, and phone width. It found two serious issues and one moderate one; all are fixed, and the audit now reports nothing.

- **Voice control could not use the tile play buttons.** They show "PLAY FROM START" but were named "Play Airhorn", so saying what you see did not match. They are now named "Play from start: Airhorn".
- **The logo link's name did not match its text** ("Soundpad Web home" against "soundpad web v2.10", and "soundpad v2.10" on phones). It is now named by its visible text plus a screen-reader-only ", home", so it matches at every width.
- **The landing page's footer was outside any landmark.** It is now a `<footer>`.
- A pixel-exact screenshot check confirmed none of this changed how the site looks.

### Added

- Permanent e2e test that fails on any serious or critical axe violation on those six screens. It uses `@axe-core/playwright` 4.14.0, a development dependency only, pinned to a release over two weeks old; `npm audit` is unchanged.

## 2.10.2 — Safe with older companions

Website update; the companion is rebuilt as 2.10.2 with no code changes.

### Fixed

- **The website showed controls an older companion cannot handle.** The website always serves the newest version, so a host who rolls back only the Windows companion runs new website code against an old companion. Each newer feature now appears only when the companion has it. The required versions are in one table (`FEATURES` in `web/src/version.ts`):
  - the Mimic window and the Guest Mimic setting need 2.4;
  - the transcript controls need 2.5;
  - the F6 setting needs 2.7;
  - Edit copy needs 2.8;
  - effects need 2.9.

  Before this, the Guest Mimic setting appeared even for a 2.3 companion. A 2.4 companion would have shown Get transcripts and F6. Edit copy would have appeared and failed with "Unsupported command" on a 2.3 companion with real Soundpad.

### Verified: both mixed-version directions

- **New website with the v2.3.1 companion**, built from its tag: playback and uploads work, no Mimic, Edit copy or Guest Mimic controls appear, and there are no page errors.
- **v2.3.1 website with the new companion:** playback, permissions, the Kick counter and Temp work with no page errors. Saving permissions from the old website switches Guest Mimic off, because it does not know that setting; that is the safe direction.
- Unit tests for the feature gates (60 total); full e2e 27/27.
- **Rolling back is now tested.** The pre-Mimic v2.3.1 companion, built from its tag, starts cleanly on data written by the current version, both mid-session and after End Hosting, including Mimic clips and edited copies. The only side effect is that it drops the `guestMimic` setting, so Guest Mimic is off after a round trip. The new `SoundpadWeb.Harness.exe --make-rollback-fixture <folder> open|ended` writes such a data folder; the steps are in [docs/ROLLBACK.md](docs/ROLLBACK.md).

## 2.10.1 — Fixes from an independent review of Mimic

**Update the Windows companion.** A separate reviewer with no knowledge of how the code was written read all of Mimic and reported ten problems. Each was checked against the code; all ten were real and are fixed.

### Fixed

- **Edit copy failed on sounds longer than about 90 s.** Copies count against Temp's 16 MiB per-sound limit, and the form pre-fills the full length. Long copies are now mixed to mono and then lowered in sample rate (never below 16 kHz) to fit. Anything still too long is refused before decoding, with the longest length that would work. The limit was deliberately not raised: it is recorded in the audio journal, and older companions would reject a journal with larger entries, which would break rolling back.
- **A guest could make the host decode large files with no permission check first.** This applied even to revoked guests or guests without upload permission. Edit copy now checks the session, the guest's permission, their audience and revocation before doing any work, and runs only one copy at a time.
- **Viewing the waveform could punch silent holes into the capture.** Reading the buffer padded silence after just 25 ms without a packet, so a slightly late packet landed after a silence that never happened. Reads now leave the newest 100 ms unpadded, the same tolerance capture itself uses.
- **Turning transcripts off and on during a transcription could duplicate lines or run Whisper twice at once.** Each transcription loop now has a generation, so a replaced loop can never commit. Whisper runs one recognition at a time, and racing Enable calls no longer leave a stray loop running.
- **Closing the companion mid-transcription could free Whisper while it was running.** Shutdown now waits for a recognition in progress, up to 15 s, and skips freeing rather than crash.
- **Low-severity fixes:**
  - A failed start could stop a newer capture.
  - Mimic kept saying "Listening" after the captured app closed; it now says so.
  - A clip from a stale view could be cut from a newly started source. Clips now carry the capture's ID and are refused if it changed.
  - About 8.6 MB of old buffer stayed alive after Stop.
  - A thread-ID race in the F6 hotkey.

### Verified

- New C# test covering the long slow-mo copy (kept at full length within 16 MiB), revoked-guest refusal, stale-capture refusal and replaced-loop refusal (78 total). The silent real-capture self-test still transcribes the JFK sentence exactly. All 4 live-relay guest tests, 27 e2e and 59 unit tests pass.

## 2.10.0 — Mimic clips are shared moments

**Update the Windows companion** for this; with a 2.9 companion the list stays empty and no notices appear.

### Added

- **Everyone hears about a new clip.** When anyone makes a Mimic clip, from the window or with the host's F6 mid-game, every participant gets a short notice such as *Alice mimicked "ask not what your country…"*. Clips that existed before you joined are not announced.
- **Just mimicked is now the session's list**, not just your tab's: the last 5 clips from anyone, with who made them and a **Replay** button each. Clips removed from Temp drop off, and the list clears at End Hosting.
- Tests: the companion records who made each clip, F6 clips included, and clears the list at End Hosting. The live-relay guest test checks that the host sees the notice when a guest clips, and that the clip is in the shared list.

## 2.9.5 — Mimic remembers your source

Website update; the companion is rebuilt as 2.9.5 with no code changes.

### Changed

- The **Listen to** picker preselects the source you last started, matched by name ("App: Discord", not its process id, which changes every launch). If none was saved, it picks Discord when it is running, since that is the usual reason to use Mimic, and otherwise the first source. Saved only in this browser; nothing is sent anywhere.
- Unit tests for the choice order and for stripping process ids.

## 2.9.4 — Replay your recent Mimic clips

Website update; the companion is rebuilt as 2.9.4 with no code changes.

### Added

- **Just mimicked** row in the Mimic window: your last 5 clips from this tab, each with a **Replay** button, so you can fire one again mid-conversation without closing the window and finding it on the board. The list survives closing and reopening the window; it is not saved anywhere, like Temp itself. The buttons are labelled "Replay …" so screen readers can tell them apart from the board's "Play …" buttons.
- e2e: replaying a clip from the row plays it again through Soundpad.

## 2.9.3 — Mimic waveform shared between guests

**Update the Windows companion** to get this; nothing looks different.

### Fixed

- Every Mimic waveform refresh scanned the whole 30 s buffer, 2.9 ms on the owner's PC, while holding the buffer lock that audio capture also needs. Twelve guests polling at the session's rate limit could have used about 42% of a CPU core and stalled capture. The companion now computes the waveform at most every 100 ms and shares it with every viewer, so the cost stays at or below 2.9% of a core however many guests watch. Clips stay exact, because each peek still reports the precise frame its waveform was drawn at. Measured with the new `SoundpadWeb.Harness.exe --peek-bench`.
- Test: two peeks within 100 ms return the same snapshot; a later peek moves on.

## 2.9.2 — Dead CSS removed

Website maintenance; nothing looks or behaves differently. The companion is rebuilt as 2.9.2 with no code changes.

### Changed

- Removed 386 lines of CSS (about 11%) for 14 class names nothing uses any more. These were left over from older Codex layouts: `play-sound`, `sound-icon`, `sound-label`, `edit-icon`, `mini-waveform`, `full-wave-button`, `wave-controls`, `precision-seek`, `spotify-panel`, `copy-invite`, `ai-credit` and `tone-1`…`tone-4`. The stylesheet shrank from 54.3 KB to 48.0 KB.
- Verified with a temporary pixel-exact screenshot guard: 12 screens matched the pre-change baseline with zero differing pixels. They covered the landing page, all five view modes, Temp, Settings in all three themes, the Mimic window and phone width.

## 2.9.1 — Lighter transcripts for gaming hosts

**Update the Windows companion** to get the lighter setting; nothing else changes.

### Changed

- Mimic transcripts use 2 CPU threads (1 on PCs with fewer than 8 cores) instead of up to 4, because hosts are often playing a game at the same time. Benchmarked with the new `SoundpadWeb.Harness.exe --whisper-bench <wav>` on the owner's PC, 11 s of speech took:
  - 1 thread: 4.7 s (2.3x real time), 4.6 s CPU.
  - 2 threads: 2.7 s (4.1x real time), 5.0 s CPU.
  - 4 threads: 1.6 s (6.9x real time), 6.3 s CPU.

  The transcript was identical at every setting. Lines now appear about a second later in long sentences, in exchange for half the busy cores and about 20% less CPU.

### Verified

- Verified the full real chain end to end without any sound: real Windows app-loopback capture, Mimic's buffer, and the real Whisper transcript. New `SoundpadWeb.Harness.exe --mimic-silent-selftest <wav>` plays the file at stream volume 0 and captures its own process, because Windows' per-app capture taps audio before stream volume. On the owner's PC the JFK sentence came through exactly (1,507 packets, peak 783/1000). This was the last untested part of Mimic.

## 2.9.0 — Voice effects

**Update the Windows companion** for effects; the website works with 2.8 companions (without effects).

### Added

- **Voice effects on a copy.** In **Temp**, **Edit copy** (formerly **Trim**) now has an **Effect**: Chipmunk (1.5x faster and higher), Slow-mo (0.7x, deeper) or Echo (taps at 250 ms and 500 ms). Like Trim, it saves a new Temp sound named for the effect and never changes the original. Mimic is about imitating what someone said; this makes it funnier.
- Tests: effect math (speed per channel, echo taps), an edited copy with an effect, and an e2e Chipmunk copy of a Mimic clip.

### Fixed

- **Trim could silently do nothing (2.8.0–2.8.1).** The end field started at the clip's exact length (for example 2.614 s), which is not a multiple of the field's 0.05 s step, so the browser refused to submit and showed nothing. Changing only the start therefore never saved. The form now clamps values itself. The earlier test typed round numbers into both fields, which is why it missed this; the new e2e test uses the default end.

## 2.8.1 — Keyboard waveform, layout fixes, README map

Website update; the companion is rebuilt as 2.8.1 with no code changes.

### Added

- The Mimic waveform is keyboard-operable: focus it, then **Left/Right** move the end of the selection, **Shift+Left/Right** move the start, and **Ctrl** makes 1 s steps. The first press selects the last second. Screen readers hear it as a slider that announces the selected range.
- README: a "What's in this repository" map of the merged project, Mimic privacy notes, Mimic check commands, and a screenshot of the Mimic window.

### Fixed

- Transcript lines in the Mimic window were centered, which pushed their times away from their words; they are now left-aligned.
- The Transcript and F6 checkboxes picked up the text-field styling (38 px tall with padding), leaving uneven gaps between those rows.

## 2.8.0 — Trim

**Update the Windows companion** for Trim; the website hides nothing for older companions, but the Trim button only works with 2.8.

### Added

- **Trim** for any Temp source (Mimic clips, uploads, native sounds prepared in Temp): set start and end in seconds and save a trimmed copy as a new Temp sound. The original is never changed, matching Voice Replay Studio. The host can always trim; guests can when uploads are allowed. Native sources in any format Soundpad Web can decode (WAV, OGG, MP3 and others) can be trimmed.
- Tests: C# trim test (exact 1.00 s copy from a 2 s source, original intact, guest permission, bad ranges), e2e trim of a Mimic clip.

### Changed

- The test harness's mock Soundpad now reports the length of sounds added during a session, like real Soundpad does.

## 2.7.0 — F6 anywhere

**Update the Windows companion** for the hotkey; the website works with 2.6 companions.

### Added

- **F6 hotkey** (host, opt-in in the Mimic window): while Mimic is listening, F6 in any app on the host PC saves the last 10 seconds to Temp, named after its transcript when transcripts are on. This brings Voice Replay Studio's signature F6 into Soundpad Web. A website cannot register a system-wide key, but the companion is a native Windows program and can. Off by default; reports when another app already owns F6; released on End Hosting and exit; never available to guests.
- `SoundpadWeb.Harness.exe --hotkey-probe` checks F6 without pressing anything. On the owner's PC F6 was free and was released cleanly.
- Tests: hotkey C# test (taken key, host-only, not listening, 10 s clip named from the transcript, release on End Hosting) and e2e checks for the host toggle and its absence for guests.

### Changed

- The Mimic window's clip path and the hotkey share one save routine in the companion.

## 2.6.0 — Better live transcripts, selection handles

**Update the Windows companion** for the transcript fix; the website works with 2.5 companions.

### Changed

- Live transcripts now wait for the speaker to pause (0.4 s) instead of cutting every few seconds. Streaming Voice Replay Studio's JFK test recording through Mimic in real time used to give "And so my fellow Americans ask not **to**…", with a word Whisper invented at the cut. It now gives the exact sentence, the same as transcribing the whole file. After 15 s of unbroken speech it still cuts, holding back the unfinished sentence.

### Added

- Edge handles on a Mimic selection, as in Voice Replay Studio: drag either end to adjust it without starting over. The cursor changes over a handle.
- `SoundpadWeb.Harness.exe --mimic-stream-transcript <wav>`: end-to-end live transcription check with the real Whisper engine and no audio device.
- Tests: pause-based chunking (C#), handle dragging (e2e).

## 2.5.1 — Mimic keyboard shortcuts

Website update; the companion is rebuilt as 2.5.1 with no code changes, so a 2.5.0 companion keeps working.

### Added

- Keyboard shortcuts in the Mimic window, the browser version of Voice Replay Studio's F6: **3**, **5** or **0** select the last 3, 5 or 10 seconds and move focus to the clip name, **Enter** mimics, **Shift+Enter** mimics and plays, **Esc** clears the selection before closing the window.

### Fixed

- Clicking **Start listening** dropped keyboard focus out of the Mimic window, so its keys did nothing until you clicked back in. The shortcuts now work as long as the window is open.

### Verified

- The opt-in live-relay guest test ran for the first time against real public relays and passed, including a new check that guests receive the host's transcript as text. All 4 live-relay tests pass.
- New e2e test for the keyboard flow.

## 2.5.0 — Mimic transcripts

**Update both the website (refresh) and the Windows companion** to use transcripts. Everything from 2.4 keeps working with a 2.4 companion. The previous release, `v2.4.0`, stays available; see [docs/ROLLBACK.md](docs/ROLLBACK.md).

### Added

- **Live transcript in the Mimic window.** The companion transcribes the rolling buffer locally with Whisper (`base.en`, the same model and filters as Voice Replay Studio). Lines appear a few seconds after they are spoken; click one to select exactly that audio. The words become the clip's default name.
- Host-only **Get transcripts (148 MB, once)** button. It downloads the model in the background with progress, then turns transcripts on. An existing Voice Replay Studio model on the same PC is reused.
- `SoundpadWeb.exe --transcribe-file <wav> [--download] [--verbose]` diagnostic.
- Tests: transcript chunking and timing (C#), transcript helpers (unit), and an e2e test that clicks a transcript line and clips it.

### Changed

- Mimic capture moved from NAudio's obsolete `WasapiCapture`/`WasapiLoopbackCapture` to its `WasapiRecorder`. One code path now serves both outputs and apps, and the CI warnings are gone.
- The companion embeds Whisper's native libraries (~2 MB) and writes them to `%LOCALAPPDATA%\SoundpadWeb\native\` on first use, so it stays a single EXE. Whisper.net cannot find native libraries inside a single-file app on its own.

### Fixed during development

- A model download that succeeded but failed to load afterwards was deleted, forcing another 148 MB download. Downloads are now kept.

## 2.4.0 — Mimic

Soundpad Web and the separate **Voice Replay Studio** ("Mimic") project are now one repository. **Update both the website (refresh) and the Windows companion** to use Mimic; everything else still works with a 2.3 companion. To go back, see [docs/ROLLBACK.md](docs/ROLLBACK.md). The last pre-Mimic version is `v2.3.1` (tag `backup/pre-mimic`).

### Added

- **Mimic window.** The host chooses an app or output device, and the companion keeps the last 30 seconds in memory. Anyone allowed can select a moment on a live waveform and turn it into a Temp sound, or **Mimic & play** it immediately. See [docs/MIMIC.md](docs/MIMIC.md).
- **Mimic on** badge in every participant's header while capture runs.
- **Guest Mimic** permission (off by default). Only the host can choose or change the source.
- Voice Replay Studio V1.1 source imported unchanged under `mimic/`, with its README, tests and licenses. CI now builds it.
- `SoundpadWeb.Harness.exe --mimic-probe` checks real capture on a PC without saving audio.
- Tests: 5 companion tests, 4 unit tests and 2 e2e tests (one opt-in live-relay) for Mimic.

### Changed

- Companion version 2.4.0. Mimic is the only feature that needs it.
- The list of slow commands (longer timeouts) is shared between the companion bridge and the guest session instead of being copied in both.

### Fixed during development

- A selection near the left edge of the window was always refused as "scrolled out", because the buffer held exactly the visible 30 seconds. The buffer now keeps 15 extra seconds.
- Right after Mimic started, any selection that reached before the start of recording was refused; it is now trimmed to the recorded part.

## Unreleased (2.3.x)

- CI: browser tests retry once on GitHub Actions. A retried test is flagged as flaky in the run's public annotations, so intermittent failures are visible without downloading logs. An intermittent failure seen on the 2.3.1 `main` run did not reproduce in 144 later runs (72 local, 72 on CI).

## 2.3.1

First release maintained by Claude (Claude Code). The companion EXE is rebuilt as 2.3.1 so it matches the release tag, but its code is unchanged: a running 2.3.0 companion keeps working, and there's no need to download the new one.

### Fixed

- The `/` search shortcut no longer fires while the layout, organize-sound or statistics dialogs are open.
- Closing a dialog now returns keyboard focus to the button that opened it.
- Tab no longer escapes dialogs that contain dropdowns (the focus trap now includes `select`, `textarea` and `tabindex` elements).
- Dragging from inside a dialog (for example, while cropping an icon) and releasing over the backdrop no longer closes the dialog.
- The header badge read "V2" while the footer read "v2.3". Both now show the version from `package.json`.
- In the host layout, "All sounds" or a Soundpad category stayed highlighted after switching to My Layout.
- "Copy Invite" before the session finished starting said the clipboard was unavailable; it now says the session is still starting.
- Some notifications showed a raw `Error:` prefix and were styled as info instead of errors.
- Searching in My Layout now ignores leading and trailing spaces, as it already did in the host layout.
- "1 sounds" / "1 matching sounds" pluralization.

### Added

- Escape clears the search box.
- Unit tests for board sorting, personal folders, hidden sounds, search and the companion version gate.
- An end-to-end test for dialog focus handling, the search shortcut and backdrop clicks.

### Changed

- `main.tsx` was split from about 1,100 lines into `Sidebar`, `SettingsDialog`, `Modal` and a pure `arrange.ts` module. Permission toggles are now one table instead of six copy-pasted blocks.
- Hidden-sound filtering uses a `Set` instead of a linear scan for every sound.
- The companion version check and the 12-guest limit are each defined once (`version.ts`, `network/session.ts`).
