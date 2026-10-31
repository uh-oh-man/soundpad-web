# Changelog

Notable changes to Soundpad Web. Versions follow the website's `package.json`; the Windows companion is versioned separately and only needs updating when a release says so.

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
