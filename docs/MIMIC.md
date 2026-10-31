# Mimic

Mimic turns something that was *just said* into a sound. The host's companion keeps the last 30 seconds of one app or output device in memory. Anyone allowed to use Mimic can open the **Mimic** window, see that audio as a live waveform, select a moment, and play it back through Soundpad.

Mimic came from a separate desktop project, **Voice Replay Studio** (a rolling "instant replay" recorder with a clip editor, Whisper transcription and Soundpad import). As of 2.4.0 both projects live in this repository:

| Part | Where | What it is |
| --- | --- | --- |
| Mimic in Soundpad Web | `companion/src/MimicCapture.cs`, `companion/src/BridgeMimic.cs`, `web/src/components/MimicPanel.tsx` | The browser feature: host chooses a source, guests clip from it. |
| Voice Replay Studio | `mimic/` | The original Windows desktop app (V1.1), unchanged, with its own README and tests. |

The capture engine in the companion is a port of Voice Replay Studio's `CaptureService`, rewritten for the companion's NAudio 3.3. NAudio 3.3 includes process-loopback activation, so the hand-written COM interop is no longer needed. The desktop app keeps its own copy on NAudio 2.2.1 and Whisper.net.

## Using it

**Host**

1. Click **Mimic** above the soundboard.
2. Under **Listen to**, choose an **Output** (everything that device plays) or an **App** (that program and its child processes, e.g. Discord).
3. Click **Start listening**. A pulsing **Mimic on** badge appears in everyone's header.
4. To let guests clip, turn on **Session & access → Guest Mimic**. It is off by default.
5. Optional (2.7.0): turn on **F6 hotkey**. While Mimic is listening, pressing **F6 in any app** on the host PC saves the last 10 seconds to Temp, named after its transcript when transcripts are on, just like Voice Replay Studio's F6. It is off by default so it never takes F6 from a game; if another app already owns F6 (for example Voice Replay Studio itself), the window says so. End hosting and quitting release the key.
6. **Stop listening**, **End hosting**, or closing the companion all stop capture and drop the buffer.

**Anyone allowed**

1. Open **Mimic** (or click the **Mimic on** badge).
2. Drag across the waveform, or use **Last 3 s / 5 s / 10 s**. A selection freezes the view so the moment stays put. Drag either edge handle of a selection to adjust just that end (2.6.0). From the keyboard (2.8.1), focus the waveform: Left/Right move the end, Shift+Left/Right the start, Ctrl for 1-second steps.
3. Optionally name it, then **Mimic it** (adds it to Temp) or **Mimic & play** (adds it and plays it through Soundpad).

Keyboard (2.5.1): **3**, **5** or **0** select the last 3, 5 or 10 seconds and move focus to the name field, **Enter** mimics, **Shift+Enter** mimics and plays, **Esc** clears the selection (press it again to close the window). This is the browser counterpart of Voice Replay Studio's F6 key; a website cannot register system-wide hotkeys.

The clip becomes a normal Temp sound, so everything Temp already does applies: reverse, **Keep permanently** with host confirmation, cleanup at End Hosting, and (2.8.0) **Trim**. Trim works on any Temp source, not only Mimic clips: open **Temp**, press **Trim**, set the start and end in seconds, and **Save trimmed copy**. Like Voice Replay Studio's trim, it saves a new version and leaves the original untouched. Guests can trim when the host allows uploads.

## Transcripts (2.5.0)

Mimic can show what was said, line by line, under the waveform. Click a line to select exactly that audio, slightly padded so no word is clipped. The words become the clip's name unless you type one.

- **Host turns it on** in the Mimic window. The first time, **Get transcripts (148 MB, once)** downloads Whisper's English `base.en` model to `%LOCALAPPDATA%\SoundpadWeb\models\`. If Voice Replay Studio already downloaded it on this PC, that copy is reused and nothing is downloaded.
- **Recognition runs on the host PC.** Audio is never uploaded. People allowed to use Mimic receive only the text of the last 30 seconds, with timings.
- **Appears when the speaker pauses.** The companion waits for a pause of at least 0.4 seconds before transcribing what was said (2.6.0). Cutting mid-sentence made Whisper guess the missing words. After 15 seconds of unbroken speech it cuts anyway, holding back the unfinished sentence for the next chunk. Silent chunks are skipped, which saves CPU and stops Whisper inventing words from silence.
- **English only**, using the same model and confidence filters as Voice Replay Studio. On the owner's PC the model transcribed 11 seconds of speech in 2.2 seconds.
- Turning transcripts off, **Stop listening**, **End hosting** and quitting all discard the text. Nothing is saved except a clip's name.

`SoundpadWeb.exe --transcribe-file <file.wav> [--download] [--verbose]` transcribes a local WAV with the same engine, which is useful to check the model and native libraries on a PC. `SoundpadWeb.Harness.exe --hotkey-probe` registers and releases F6 to check whether another app owns it (it presses nothing). `SoundpadWeb.Harness.exe --mimic-stream-transcript <file.wav>` streams a WAV into Mimic's buffer in real-time 10 ms packets with the real Whisper engine and prints the lines the Mimic window would show; no audio device is used. Streaming the JFK sample gives the same sentence, word for word, as transcribing the whole file.

## Privacy and safety rules

- **Off until the host starts it.** Capture never starts on launch and is never restored after a restart.
- **No microphones.** Only output devices and app loopback are offered. Mimic cannot listen to the host's room.
- **Everyone can see it.** While capture runs, every participant's header shows **Mimic on** with the source name.
- **Audio stays on the host PC.** Guests receive only 300 peak levels for the waveform, plus transcript text when the host turns transcripts on. A clip is cut on the companion and imported locally, like any Temp upload.
- **Memory only, bounded.** The buffer holds 45 seconds (the 30 s window plus 15 s of grace so a selection survives the round trip from a guest). Nothing is written to disk unless someone clips, and clips follow the existing Temp limits.
- **Separate permission.** Guest clipping needs **Guest Mimic**; playing the result also needs **Remote playback**. Only the host can choose or change the source.
- **Be fair to the people you record.** Mimic captures other people's voices. Tell your session it is on; the badge exists for that reason.

## Protocol

New command ops: `mimicSources`, `mimicStart`, `mimicStop`, `mimicTranscribe` (`action`: `enable`, `disable` or `download`), `mimicHotkey` (`enable` or `disable`; all host only), `mimicPeek` and `mimicClip` (host, or guests with `guestMimic`). New fields: `sourceId`, `mimicEnd`, `name`. New permission: `guestMimic` (default `false`). Snapshots gain an optional `mimic` object (`active`, `source`, `status`, `windowMs`, from 2.5.0 `transcribing`, `transcriptModel`, `transcriptStatus`, and from 2.7.0 `hotkey`, `hotkeyStatus`). `mimicPeek` also returns `transcript`: lines with `startMs`/`endMs` relative to the window start.

`mimicPeek` returns `end`, an absolute frame counter for the right edge of the window. `mimicClip` sends that `end` back with `positionMs`/`endMs` offsets inside the window. The companion therefore cuts exactly the samples the person saw, even though capture kept running. If those samples have been overwritten, the clip is refused rather than shifted to newer audio, the same rule Voice Replay Studio used.

Requires companion 2.4. Older companions ignore the new permission, and the website hides Mimic for them.

## Testing

- `companion/tests/MimicTests.cs`: ring buffer addressing and expiry, permissions, exact clip length, silence and pre-start trimming.
- `tests/unit/mimic.test.ts`: selection math and protocol validation.
- `tests/e2e/app.spec.ts` ("Mimic: …"): the full host flow against `MimicFixture`, a test-only source that plays a tone every 2 seconds. An opt-in `LIVE_RELAY=1` test covers a real guest.
- `SoundpadWeb.Harness.exe --mimic-probe [source]` listens to a real device or app for 2 seconds and prints only frame counts and the peak level, to check capture on a given PC.

## Not yet included

Every Voice Replay Studio feature that fits a shared soundboard is now in the browser version: rolling capture, live transcript, F6 hotkey, selection handles and trim. What stays desktop-only is its personal clip library with retranscription, since Soundpad Web keeps clips in Soundpad's Temp and permanent categories instead. Saved Mimic clips are named after their transcript, so the normal sound search finds them by their words. They remain available in the desktop app under `mimic/`.
