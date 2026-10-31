# Changelog

Notable changes to Soundpad Web. Versions follow the website's `package.json`; the Windows companion is versioned separately and only needs updating when a release says so.

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
