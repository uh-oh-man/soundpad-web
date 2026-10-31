# Maintainer notes

Claude (Claude Code) is the main editor of this repository as of 2.3.1. The owner is @uh-oh-man. Versions up to 2.3.0 were written by OpenAI Codex. Record every user-visible change in `CHANGELOG.md`, and keep the maintainer note at the top of `README.md` accurate. The README is the user guide: don't name a "current version" in it (it went stale before); link the changelog. Developer material belongs in `docs/DEVELOPERS.md`.

## Layout

- `web/src`: React website (Vite). `main.tsx` holds the app shell. `controller.ts` owns connection state. `arrange.ts` is the pure sound sorting and filtering logic. `components/` holds UI pieces. The Mimic window is `components/MimicPanel.tsx` plus `components/mimic/`, with pure helpers in `mimic/selection.ts`. `version.ts` has the `FEATURES` gate table.
- `companion/`: .NET 10 Windows companion (`src`), its xUnit tests (`tests`) and the mock-Soundpad harness used by e2e tests (`harness`). Mimic capture lives in `MimicCapture.cs` / `BridgeMimic.cs` and transcripts in `MimicTranscript.cs`; `harness/MimicFixture.cs` and `harness/TranscriptFixture.cs` are their test stand-ins (the harness always uses them, so e2e never touches real audio or Whisper).
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
- No global .NET SDK is installed on the owner's PC. Use the one bundled with their original Mimic folder: `..\Mimic\.tools\dotnet\dotnet.exe` (10.0.402), e.g. `test companion/tests/SoundpadWeb.Tests.csproj -c Release` and `build companion/harness/SoundpadWeb.Harness.csproj -c Release`.
- With the current harness, the expected e2e result is all passing plus 4 skipped (the opt-in `LIVE_RELAY` Nostr tests).
- The e2e suite runs against the Vite dev server in the working tree. Don't edit `web/src` while it runs, because hot reload will affect the results.

## Conventions

- Bump `package.json` (and the two matching `package-lock.json` entries) and `companion/SoundpadWeb.Companion.csproj` `<Version>` together for each release, tag `vX.Y.Z`, and add the release to `docs/ROLLBACK.md`. The header and footer read the version from there through the `__APP_VERSION__` define in `vite.config.ts`.
- Bump `REQUIRED_COMPANION` in `web/src/version.ts` only when the website depends on a new companion feature.
- Every new companion-dependent control must be gated with `supports(state.companionVersion, feature)`; add the feature and its first companion version to `FEATURES` in `web/src/version.ts`. The website is always newest, so hosts on older companions see it.
- Dependencies: only adopt releases at least two weeks old, and don't force major-version overrides under tools. Open item (2026-10-31): moderate `minimatch` < 11.0.1 advisory inside ESLint/typescript-eslint (dev-only). Update once ESLint depends on `minimatch` 11 and that release is two weeks old; see CHANGELOG 2.11.2.
- Never loosen validation of persisted data (`state.json`, the managed-audio journal, e.g. the 16 MiB staged-input check): older companions would reject the newer data and rolling back would break. After changing anything persisted, rerun the rollback test in `docs/ROLLBACK.md` (harness `--make-rollback-fixture`) against the v2.3.1 companion.
- Work on a branch. When a requested feature is done and verified, merge it to `main` and push the release tag yourself (the owner asked for this); confirm the Pages and release runs succeed.
