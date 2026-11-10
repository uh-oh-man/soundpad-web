# Maintainer notes

Claude (Claude Code) is the main editor of this repository as of 2.3.1. The owner is @uh-oh-man. Versions up to 2.3.0 were written by OpenAI Codex. Record every user-visible change in `CHANGELOG.md`, and keep the maintainer note at the top of `README.md` accurate. The README is the user guide: don't name a "current version" in it (it went stale before); link the changelog. Developer material belongs in `docs/DEVELOPERS.md`.

## Reports: read first, every time

`reports/` holds every report about this app, on this PC only (it is in `.gitignore`; never commit it). Before editing anything, read `reports/bugs/` and `reports/features/` and fix open bugs first. File every new report the owner gives in chat, and every new GitHub issue, as its own file in `bugs/`, `features/` or `spam/` (format in `reports/README.md`). When one is fixed or built, set its status to the version and move it to `done/`.

## Testing on the owner's real Soundpad

The owner allows tests on their real Soundpad and companion data without asking first (2026-11-08), as long as each test leaves their library as it found it (no lasting changes to their sounds or categories) and anything it adds is removed again. Back up `%LOCALAPPDATA%\SoundpadWeb\state.json` and `managed-audio\journal.json` to the scratchpad first, and say afterwards what was run and what it changed.

## Layout

- `web/src`: React website (Vite). `main.tsx` holds the app shell. `controller.ts` owns connection state. `arrange.ts` is the pure sound sorting and filtering logic. `components/` holds UI pieces. The Mimic window is `components/MimicPanel.tsx` plus `components/mimic/`, with pure helpers in `mimic/selection.ts`. `version.ts` has the `FEATURES` gate table.
- `companion/`: .NET 10 Windows companion (`src`), its xUnit tests (`tests`) and the mock-Soundpad harness used by e2e tests (`harness`). Mimic capture lives in `MimicCapture.cs` / `BridgeMimic.cs` and transcripts in `MimicTranscript.cs`; `VoiceReplayLibrary.cs` reads Voice Replay Studio's library read-only (never write to it; the harness uses `harness/VoiceReplayFixture.cs`); `harness/MimicFixture.cs` and `harness/TranscriptFixture.cs` are their test stand-ins (the harness always uses them, so e2e never touches real audio or Whisper).
- Whisper's native DLLs are embedded resources written to `<data>\native\whisper-1.9.1\runtimes\win-x64\`. Whisper.net probes `<folder of RuntimeOptions.LibraryPath>\runtimes\win-x64`, so keep that layout. Check single-file loading with `SoundpadWeb.exe --transcribe-file <wav> --verbose` on a published EXE; the Whisper model is at `%LOCALAPPDATA%\SoundpadWeb\models\` on the owner's PC.
- `mimic/`: Voice Replay Studio V1.1 desktop app (WPF, NAudio 2.2.1, Whisper.net), imported unchanged in 2.4.0. It shares no code with the companion; the companion's Mimic is a port onto NAudio 3.3.
- `docs/MIMIC.md` describes Mimic; `docs/ROLLBACK.md` lists restore points and must gain a row for every release.
- CSS is layered: `tokens.css` → `styles.css` → `v2.css` → `polish.css`. Later files override earlier ones, and about 36 selectors are defined in more than one file. Check every file before changing a selector. For CSS-only cleanups, use a temporary Playwright spec with `toHaveScreenshot` (`maxDiffPixels: 0`, `animations: 'disabled'`): record a baseline with `--update-snapshots` on the unchanged code, confirm a second run passes, change the CSS, rerun, then delete the spec and snapshots. Exact-pixel snapshots are machine-specific, so don't commit them (2.9.2 used this method).

## Checks

```powershell
npm run lint; npm run typecheck; npm test
npx playwright install chromium   # once per machine
npm run test:e2e                  # needs the prebuilt harness in companion/harness/bin
```

- E2E tests start `companion/harness/bin/Release/net10.0-windows10.0.19041.0/win-x64/SoundpadWeb.Harness.exe`. Rebuild it after any companion change.
- No global .NET SDK is installed on the owner's PC. Use the one bundled with their original Mimic folder: `..\Mimic (Voice Replay Studio)\.tools\dotnet\dotnet.exe` (10.0.402; the folder moved to `A OF THE I\Apps` in November 2026), e.g. `test companion/tests/SoundpadWeb.Tests.csproj -c Release` and `build companion/harness/SoundpadWeb.Harness.csproj -c Release`.
- With the current harness, the expected e2e result is all passing plus 5 skipped (the four opt-in `LIVE_RELAY` Nostr tests and the `DEPLOYED_URL` check).
- WebKit (Safari's engine) also runs in CI: `npx playwright install webkit`, then `$env:TEST_BROWSER='webkit'; npx playwright test --output test-results/webkit`. Expect 8 skipped (2.23): besides those 5, the loopback HTTPS test, the no-WebRTC guest test and the Edit copy Loop preview test are skipped, because hosts never use Safari and headless WebKit often refuses to play audio. Playwright's Windows WebKit has no WebRTC, so the live guest tests can't run in it. Playwright's Firefox build is blocked from starting on the owner's PC (`spawn UNKNOWN` / permission denied, likely Smart App Control). Don't change that security setting to run it.
- The e2e suite runs against the Vite dev server in the working tree. Don't edit `web/src` while it runs, because hot reload will affect the results.

## Conventions

- Bump `package.json` (and the two matching `package-lock.json` entries) and `companion/SoundpadWeb.Companion.csproj` `<Version>` together for each release, tag `vX.Y.Z`, and add the release to `docs/ROLLBACK.md`. The header and footer read the version from there through the `__APP_VERSION__` define in `vite.config.ts`.
- Bump `REQUIRED_COMPANION` in `web/src/version.ts` only when the website depends on a new companion feature.
- Never set `disabled` on a control just because its own command is running: Chrome drops keyboard focus from a control that becomes disabled. Use `aria-disabled` and ignore presses while busy (2.13.1–2.13.3). The slow-connection e2e test covers this.
- Every new companion-dependent control must be gated with `supports(state.companionVersion, feature)`; add the feature and its first companion version to `FEATURES` in `web/src/version.ts`, and its name to `FEATURE_NAMES` there (Settings lists what an update adds). The website is always newest, so hosts on older companions see it.
- Dependencies: only adopt releases at least two weeks old, and don't force major-version overrides under tools. Open item (2026-10-31): moderate `minimatch` < 11.0.1 advisory inside ESLint/typescript-eslint (dev-only). Update once ESLint depends on `minimatch` 11 and that release is two weeks old; see CHANGELOG 2.11.2. Checked again for 2.13.5: `minimatch` 11.0.1 was published 2026-10-27, and ESLint 10.14.0 (2026-10-30) still depends on `^10.2.6`.
- Soundpad reads commands in the Windows ANSI code page, not UTF-8 (2.15.9): send every command through `SoundpadPipe` (it encodes with `SoundpadText`), keep app-made names ASCII, and keep `MockSoundpad` decoding code page 1252 so tests see what the real Soundpad stores.
- Soundpad removes a category's sounds along with the category (measured 2.16). The companion uses that only for its own Temp category at session end, and only when every entry in it is a retired temporary file of its own (`RemoveTempCategory`).
- Never loosen validation of persisted data (`state.json`, the managed-audio journal, e.g. the 16 MiB staged-input check): older companions would reject the newer data and rolling back would break. After changing anything persisted, rerun the rollback test in `docs/ROLLBACK.md` (harness `--make-rollback-fixture`) against the v2.3.1 companion.
- Work on a branch. When a requested feature is done and verified, merge it to `main` and push the release tag yourself (the owner asked for this); confirm the Pages and release runs succeed.
- Push to `main` first and wait for its CI run to pass, then push the `vX.Y.Z` tag. Never tag first: a tag whose CI fails gets no release and no EXE, and public tags are never moved or deleted. v2.14.3 and v2.15.4 are source-only for this reason.
