# Setup, Soundpad diagnosis, shortcuts and host-wide hotkeys (2.44)

Stage H, work package 4. Gates: `setup` and `hostHotkeys` (both `[2, 44]` in `web/src/version.ts`). The
browser shortcuts need no gate: they never reach the companion.

## Guided setup

- **Who sees it.** A host page on a real companion (never the example, never a guest) with
  `supports(companionVersion, 'setup')` and the snapshot's `setupDone === false`.
  - A first run gets the **Set up Soundpad Web** dialog (`components/setup/SetupGuide.tsx`, through `Modal`).
  - An install that already holds kept data gets the dismissible **Check your setup** card on the board
    (`SetupCard.tsx`) instead. "Kept data" is decided once by the companion at startup (`SetupStore.Existing`):
    a Kick count or web statistics in `state.json`, a permanent kept clip in the managed-audio journal, the voice
    library file, `keep-settings.json` or `ui.json`.
  - Settings → Setup → **Run setup again** marks setup as not done (`setupSet reset`) and opens the dialog.
- **Steps** (`guide-state.ts` holds the step reducer and the fix texts):
  1. *Soundpad*: live status from the snapshot, the fix for each problem, and Retry (`refresh`).
  2. *Capture output*: outputs only. **Remember this output** sends `mimicSources {action: 'remember', sourceId}`,
     which saves the existing `mimic-settings.json` fields (`sourceId`, `sourceName`) without starting capture or
     touching any device or routing.
  3. *Invitations*: the link (new every launch), Copy Invite (`aria-disabled` with "Start hosting first" until
     there is one), Replace invite explained, `SessionAccess` for admission and places (this session only, as
     before), and the main permission switches.
  4. *Shortcuts (optional)*: the browser shortcut list and the host-wide hotkey pickers.
  5. *Launch*: Soundpad Web does not start with Windows; closing the window quits it; a backup folder is set in
     Settings → Backups.
- **Finish** sends `setupSet {action: 'done'}`. Closing the dialog early shows it again at the next launch.

## Soundpad diagnosis

`SoundpadDiagnosis.cs` classifies a failed Soundpad request; the snapshot carries `soundpadProblem` while
Soundpad is unavailable (null as soon as it answers again) and `companionElevated`. The `error` text is unchanged.

| Problem | Cause | Fix shown |
|---|---|---|
| `accessDenied` | `UnauthorizedAccessException` on the pipe | "Soundpad and Soundpad Web run with different rights (one as administrator). Start both the same way." (+ "Soundpad Web is running as administrator" when elevated) |
| `oldVersion` | R-404 on categories | "Update Soundpad." |
| `notRunning` | anything else, and no process named Soundpad | "Open Soundpad." |
| `noAnswer` | anything else while Soundpad runs | "Soundpad is open but did not answer. Restart Soundpad." |

Before 2.44 an `UnauthorizedAccessException` escaped `Poll` (it was in neither catch list); 2.44 catches it and
`TimeoutException` in `RefreshInner` and in the playback sample. Whether Soundpad runs is asked only when it
matters, at most every 2 s, by listing process names (read-only); tests and the harness inject the answer
(`Bridge.UseSoundpadRunning`). The connection banner (`SoundpadFix.tsx`) shows the fix and Retry.

## Browser shortcuts (Settings → Keyboard)

`web/src/shortcuts.ts`: Search (`/`, by key), Play/Pause (Space, by code), Panic (Shift+Escape, host on a 2.35+
companion) and Stop (new, unbound). Stored per browser in `localStorage` `soundpad-web:shortcuts` as
`{version: 1, bindings}` the `prefs.ts` way (storage that throws → this tab only; damaged JSON → defaults; ids it
does not know are kept; other tabs follow the `storage` event). The companion window and Chrome keep separate sets.

- The `main.tsx` hooks (`useSpaceToggle`, `usePanicShortcut`, `useSearchShortcut`, the new `useStopShortcut`)
  keep their skip logic and ask `matchesShortcut(id, event)`. A binding without Shift also takes the key with
  Shift held (as `/` and Space always did), unless another binding is that exact chord, so the defaults behave
  exactly as in 2.43.
- Refused: a key another shortcut has; Tab, bare Enter or Escape; Ctrl/Meta + C V X A Z Y F L R T N W; F5, F11,
  F12; Alt+F4; bare digits, letters, `+` and `-` (the area that uses them is named).
- Warned: a key equal to a host-wide hotkey ("the companion takes this key on this PC; this page never receives it").
- `ShortcutRecorder.tsx` listens on the window in the capture phase, so no shortcut, dialog or panel acts on the
  key being recorded; plain Escape cancels and Tab is never taken.
- `aria-keyshortcuts` is set on the Panic and Stop buttons and the Soundboard search; the Panic title names the
  current key. (The Voices and Sentences searches are not WP4 files and keep no `aria-keyshortcuts`.)

## Host-wide hotkeys (Settings → Setup, and the guide)

`HotkeyChord.cs`, `HostHotkeys.cs`, `hotkeys.json`. Capture (start or stop listening on the remembered source),
Panic and Stop playback, **all off** until the host records a key.

- **Chords:** `Ctrl+Alt+Shift+Win+<Key>`, keys F1–F24 except F12, A–Z, 0–9, the keypad (`Num0`…`Num9`, `NumAdd`,
  `NumSubtract`, `NumMultiply`, `NumDivide`, `NumDecimal`), Pause, ScrollLock, Insert, Home, End, PageUp,
  PageDown. Ctrl, Alt or Win is needed except for F-keys, Pause and ScrollLock. Refused: Alt+F4 and Win+D/E/R/L/G/I/X.
  Registered as `MOD_*` | `MOD_NOREPEAT` with the virtual key, one thread per key (`WindowsHotkey(modifiers, vk, name)`).
- **Conflicts:** one key for two actions; a key equal to the bare Mimic F-key (selected, not only enabled); the
  reverse in `BridgeMimic.cs` (`mimicHotkey key` and `enable` refuse a host-wide key). A stored key equal to the
  Mimic key at startup stays off (`conflict`) and is registered once the Mimic key moves.
- **Changing a key** registers the new one before releasing the old. A key another app owns (Win32 1409) keeps
  the old one, leaves the file unchanged for that action, and shows `taken`. `hotkeys.json` is written only with
  what succeeded.
- **Lifetime:** registered in `SetupStarted()` (after startup cleanup, so also after a crash-recovered start and,
  with WP2, after an update resume), kept through End hosting (F6 keeps its own End-hosting release), released on
  quit (`SetupDispose`). `--no-hotkeys` (read from `Environment.GetCommandLineArgs()`) registers nothing and shows
  `disabled`, for smoke runs.
- **Presses** run on the thread pool, never on the hotkey's thread, with a 300 ms repeat guard. Capture: running →
  the `mimicStop` path; otherwise the remembered source (by id, or an app by its name without the process id) →
  the `mimicStart` path, where the output-only check (`RequireOutput`) applies; nothing remembered → "Choose an
  output in Setup or Mimic first." Panic and Stop: synthesized host `panic {action: enable}` and `stop` commands
  through `Execute`, so they behave exactly like the page's buttons (Resume stays in the page). Each press is logged.
- **Tests never register a real key:** the test assembly (module initializer) and the harness set
  `HostHotkeys.ForbidWindowsHotkeys`, so the Windows factory refuses; every test bridge uses fixtures.

## Protocol

All host only; the companion refuses guests and relayed commands (`actor != "host"`). Details travel in `body`.

| Op | Body | Reply data |
|---|---|---|
| `setupGet` | none | `{done, dismissed, existing, soundpad: {available, problem, error, elevated}, outputs: [{id, name}], remembered}` |
| `setupSet` | `{action: 'done' \| 'reset' \| 'dismiss'}` | `{done, dismissed}` |
| `hotkeysGet` | none | `{bindings: {capture, panic, stop}, status: {capture, panic, stop}: {state, message, last}, mimicKey, disabled, problem}` |
| `hotkeysSet` | `{capture?, panic?, stop?: chord \| null}` | as `hotkeysGet` |
| `mimicSources` | `action: 'remember', sourceId` (command fields) | `{remembered}` |

`state` is `off`, `on`, `taken`, `invalid`, `conflict` or `disabled`. Snapshot (stripped for guests by
`session.ts`): `soundpadProblem`, `companionElevated`, `hotkeys` (a revision; pages fetch `hotkeysGet`) and
`setupDone`. 2.44 also strips the existing `mimic.hotkeyStatus` for guests.

## Persisted files

- `setup.json`: `{version: 1, completedAt, completedBy, dismissed}`, at most 4 KiB.
- `hotkeys.json`: `{version: 1, capture, panic, stop}`, each a chord string or null, at most 4 KiB.

Both are strict (a file that is too large, not JSON, of another version or with an invalid value means "not done"
or "all off" and is left untouched until the host changes the setting), keep unknown fields, and are written with
`.tmp` + `Flush(true)` + Move. `mimic-settings.json` keeps its format. Older companions never read the new files
(checked: v2.3.1 and 2.43.0 start normally beside them and leave them byte for byte as they were).

## Harness and e2e

- Without `--first-run`, `HarnessSetup.BeforeBridge` writes a finished `setup.json` (when none exists), so other
  browser tests never see the guide.
- Host-wide hotkeys are fixtures; `Ctrl+Alt+F9` plays a key another app owns.
- Verbs: `soundpad-offline` / `soundpad-online` (the mock answers R-500 to everything and Soundpad "is not
  running", or comes back) and `press-hotkey capture|panic|stop`.
- The harness reports the companion's own version (2.44.0 since the 2.44 release bump). `tests/e2e/setup.spec.ts`
  still raises any snapshot below 2.44 to 2.44.0, so its tests don't depend on the harness's version; a newer
  version is left as it is.

## Not proven

- `RegisterHotKey` in games, VRChat, fullscreen apps and with anti-cheat (key swallowing, focus).
- The elevated-Soundpad diagnosis on real hardware (that the pipe refuses with `UnauthorizedAccessException`).
- Real output enumeration on the owner's PC (fixture only).
