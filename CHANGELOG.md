# Changelog

Notable changes to Soundpad Web. Versions follow the website's `package.json`; the Windows companion is versioned separately and only needs updating when a release says so.

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
