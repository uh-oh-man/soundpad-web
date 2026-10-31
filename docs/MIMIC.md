# Mimic

Mimic turns something that was *just said* into a sound. The host's companion keeps the last 30 seconds of one app or output device in memory. Anyone allowed to use Mimic can open the **Mimic** window, see that audio as a live waveform with an optional transcript, select a moment, and play it back through Soundpad.

Mimic came from a separate desktop project, **Voice Replay Studio**: a rolling "instant replay" recorder with a clip editor, Whisper transcription and Soundpad import. Since 2.4.0 both live in this repository.

| Part | Where |
| --- | --- |
| Capture and the rolling buffer | `companion/src/MimicCapture.cs` |
| Live transcript (Whisper) | `companion/src/MimicTranscript.cs` |
| F6 hotkey | `companion/src/MimicHotkey.cs` |
| Commands, permissions, clips, recent list | `companion/src/BridgeMimic.cs` |
| Edit copy (trim and voice effects) | `companion/src/BridgeTrim.cs` |
| The Mimic window | `web/src/components/MimicPanel.tsx`, with `components/mimic/` (`MimicHostControls`, `MimicWave`, `MimicRecent`) |
| Pure selection, transcript and source helpers | `web/src/mimic/selection.ts` |
| Edit copy form | `web/src/components/TempManager.tsx` |
| Test stand-ins (tone source, speech, hotkey) | `companion/harness/MimicFixture.cs`, `TranscriptFixture.cs`, `HotkeyFixture.cs` |
| Voice Replay Studio V1.1 desktop app | `mimic/`, imported unchanged apart from a pointer at the top of its README |

The companion's capture is a port of Voice Replay Studio's `CaptureService` onto NAudio 3.3 (`WasapiRecorder`, with built-in process loopback). The transcript uses the same Whisper `base.en` model and filters. The desktop app keeps its own code on NAudio 2.2.1 and Whisper.net.

## Using it

### Host

1. Click **Mimic** above the soundboard.
2. Under **Listen to**, choose an **App**, meaning that program and its child processes. For Discord this includes the child process that plays voice. You can also choose an **Output**: everything that device plays. The window remembers your last choice by name and otherwise preselects Discord if it is running.
3. Click **Start listening**. A pulsing **Mimic on** badge appears in everyone's header.
4. To let guests clip, turn on **Session & access → Guest Mimic**. It is off by default.
5. Optional: tick **Transcript**. The first time, **Get transcripts (148 MB, once)** downloads the speech model; see [Transcripts](#transcripts).
6. Optional: tick **F6 hotkey** and choose how much it saves (3, 5, 10, 15 or 30 seconds; default 10). While Mimic is listening, **F6 in any app** saves that much to Temp, named after its transcript when transcripts are on, just like Voice Replay Studio's F6. It is off by default so it never takes F6 from a game. If another app already owns F6, for example Voice Replay Studio itself, the window says so.
7. **Stop listening**, **End hosting** or closing the companion stops capture, discards the buffer and the transcript, and releases F6.

The companion's console window logs these events with the time, including who made each clip and anything F6 saved.

### Anyone allowed

1. Open **Mimic**, or click the **Mimic on** badge.
2. Select a moment:
   - drag across the waveform, then drag either edge handle to adjust that end;
   - click a transcript line, which selects exactly that audio with a little padding;
   - use **Last 3 s / 5 s / 10 s**;
   - or use the keyboard: **3**, **5** or **0** select the last 3, 5 or 10 seconds and focus the name field. On the focused waveform, **Left/Right** move the end, **Shift+Left/Right** the start, and **Ctrl** makes 1-second steps.

   A selection freezes the view so the moment stays put.
3. The host can press **Preview** to hear the selection on their own PC only. It plays in the host's browser tab, never through Soundpad or to guests, and saves nothing.
4. Optionally type a name; otherwise the transcript's words, or the time, are used. Then press **Mimic it** (or **Enter**) to add the clip to Temp, or **Mimic & play** (**Shift+Enter**) to also play it through Soundpad. **Esc** clears the selection; press it again to close the window.

Everyone gets a short notice when anyone makes a clip, F6 included. **Just mimicked** lists the session's last 5 clips, with who made them, a **Replay** button and a **Reverse** button to hear it backwards.

### Afterwards: Temp and Edit copy

A clip is a normal Temp sound, so reverse, **Keep permanently** (with host confirmation) and cleanup at End Hosting all apply.

**Edit copy** works on any Temp source, not only Mimic clips:
1. Open **Temp**.
2. Press **Edit copy** and set the start and end in seconds.
3. Optionally pick an **Effect**: Chipmunk (1.5× faster and higher), Slow-mo (0.7×, deeper) or Echo.
4. Press **Save copy**.

Like Voice Replay Studio's trim, it saves a new Temp sound and never changes the original. Guests can edit copies when the host allows uploads. Temp holds at most 16 MiB per sound, so long copies are made mono and then given a lower sample rate (never below 16 kHz) to fit. Anything still too long is refused before any work is done, with the longest length that would fit.

## Transcripts

- **Off until the host turns it on.** The first time, **Get transcripts** downloads Whisper's English `base.en` model (~148 MB) to `%LOCALAPPDATA%\SoundpadWeb\models\` in the background, with progress shown. If Voice Replay Studio already downloaded the model on this PC, that copy is reused.
- **Recognition runs on the host PC.** Audio is never uploaded. People allowed to use Mimic receive only the text of the last 30 seconds, with timings.
- **Lines appear when the speaker pauses** (0.4 s), so no sentence is split; splitting made Whisper guess missing words. After 15 s of unbroken speech it cuts anyway and holds back the unfinished sentence. Silent stretches are skipped, which saves CPU and avoids words invented from silence.
- **Light on a gaming PC.** It uses 2 CPU threads on PCs with 8+ cores, otherwise 1. On the owner's PC, 11 s of speech took 2.7 s with 2 threads (4× faster than real time), with the same transcript as with 4 threads.
- **English only**, with the same confidence filters as Voice Replay Studio.
- Turning transcripts off, stopping, ending hosting and quitting all discard the text. Only a clip's name is kept.

## Privacy and safety

- **Off until the host starts it.** Capture never starts on launch and is never restored after a restart.
- **No microphones.** Only output devices and app loopback are offered, so Mimic cannot listen to the host's room.
- **Everyone can see it.** Everyone gets a notice when Mimic starts (with the source) and stops, anyone joining while it is on is told immediately, every participant's header shows **Mimic on** while it runs, and every new clip is announced.
- **Audio stays on the host PC.** Guests receive 300 peak levels for the waveform and, if the host enables it, the transcript text. Clips are cut and imported on the host.
- **Separate permissions.** Clipping needs **Guest Mimic**, playing needs **Remote playback**, and Edit copy needs uploads. Only the host chooses the source, transcripts and F6.
- **Bounded.** The buffer is 45 s in memory: the 30 s window plus 15 s so a guest's selection survives the round trip. Each guest can add at most 6 new files per minute, the same limit as uploads, within Temp's 32 files / 256 MiB. Edit copies run one at a time, and the guest's permissions are checked before any decoding.
- **Light on the host, however many guests.** The waveform is computed at most every 100 ms and shared by everyone: at most about 3% of a CPU core on the owner's PC.
- **Be fair to the people you record.** Mimic captures other people's voices. Tell your session it is on; the badge exists for that reason.

## Companion versions

The website always serves the newest version and hides anything the host's companion cannot do (`FEATURES` in `web/src/version.ts`):

| Feature | Companion |
| --- | --- |
| Mimic window, Guest Mimic | 2.4 |
| Transcripts | 2.5 |
| F6 hotkey | 2.7 |
| Edit copy (trim) | 2.8 |
| Voice effects | 2.9 |
| Shared recent clips and notices | 2.10 |
| F6 length choice | 2.11 |
| Private preview (host) | 2.12 |

## Protocol

- **Host-only ops:**
  - `mimicSources`;
  - `mimicStart` with `sourceId`;
  - `mimicStop`;
  - `mimicTranscribe` with `action` `enable`, `disable` or `download`;
  - `mimicHotkey` with `enable`, `disable` or `length`, and `positionMs` set to the F6 length (3000–30000);
  - `mimicPreview` with the same selection fields as `mimicClip`; returns the selection as base64 WAV to the host's own tab.
- **Host, or guests with `guestMimic`:** `mimicPeek` and `mimicClip`. A clip sends `mimicEnd`, `positionMs`/`endMs`, an optional `name`, and the capture's `sourceId`.
- **Host, or guests with uploads:** `trimAudio`, with `assetId`, `positionMs`/`endMs`, `action` (`none`, `chipmunk`, `slowmo` or `echo`) and an optional `name`.
- **New permission:** `guestMimic`, default `false`.
- **The `mimic` snapshot object:**
  - `active`, `source`, `status`, `windowMs`;
  - `transcribing`, `transcriptModel`, `transcriptStatus`;
  - `hotkey`, `hotkeyStatus`, `hotkeySeconds`;
  - `recent`: the last 5 clips as `assetId`, `name`, `by` and `at`.
- **`mimicPeek` returns:**
  - `end`, an absolute frame counter for the window's right edge;
  - `levels`, `rate` and `windowMs`;
  - `transcript`, as lines with `startMs`/`endMs` relative to the window start;
  - `ringId`, which identifies the capture.

A clip sends `end` and `ringId` back with offsets inside the window, so the companion cuts exactly the samples the person saw even though capture kept running. If those samples were overwritten, or Mimic was restarted since, the clip is refused rather than shifted to other audio. Voice Replay Studio used the same rule.

## Testing and checking a PC

Automated:
- **C#** (`companion/tests/MimicTests.cs`):
  - the ring buffer;
  - permissions;
  - exact clip lengths;
  - transcript timing and chunking;
  - F6;
  - Edit copy and effects;
  - review fixes.
- **Unit** (`tests/unit/mimic.test.ts`): selection math, the source choice and protocol validation.
- **e2e** (`tests/e2e/app.spec.ts`, "Mimic…" tests): the full host flow, transcripts, keyboard, Edit copy and accessibility, against test stand-ins.
- **Live relay** (opt-in `LIVE_RELAY=1`): a real guest browser over public relays.

On a real PC (the harness is `companion\harness\bin\Release\net10.0-windows10.0.19041.0\win-x64\SoundpadWeb.Harness.exe`):
- `SoundpadWeb.Harness.exe --mimic-probe [source]`: listens to a device or app for 2 s and prints packet counts and the peak level. Nothing is saved.
- `SoundpadWeb.Harness.exe --mimic-silent-selftest <wav>`: tests the **whole real chain without sound**. It plays the WAV at stream volume 0 while Mimic's real app capture listens to the harness itself, and real Whisper transcribes the result. Windows' per-app capture taps audio before stream volume. On the owner's PC the JFK sample came through word for word.
- `SoundpadWeb.Harness.exe --mimic-stream-transcript <wav>`: real-time streaming into the buffer with real Whisper, without any device.
- `SoundpadWeb.Harness.exe --whisper-bench <wav>` and `--peek-bench`: the CPU measurements quoted above.
- `SoundpadWeb.Harness.exe --hotkey-probe`: checks whether F6 is free, without pressing anything.
- `SoundpadWeb.exe --transcribe-file <wav> [--download] [--verbose]`: checks the model and Whisper's native libraries in the real single-file EXE.
- Rolling back: [docs/ROLLBACK.md](ROLLBACK.md).

## What stays in the desktop app

Every Voice Replay Studio feature that fits a shared soundboard is in the browser version: rolling capture, the live transcript, F6, selection handles, trim and more. Only its personal clip library with re-transcription stays desktop-only (`mimic/`). Soundpad Web keeps clips in Soundpad's Temp and permanent categories instead, and because clips are named after their transcript, the normal sound search finds them by their words.
