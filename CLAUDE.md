# Maintainer notes

Claude (Claude Code) is the main editor of this repository as of 2.3.1. The owner is @uh-oh-man. Versions up to 2.3.0 were written by OpenAI Codex. Record every user-visible change in `CHANGELOG.md`, and keep the maintainer note at the top of `README.md` accurate.

## Layout

- `web/src`: React website (Vite). `main.tsx` holds the app shell. `controller.ts` owns connection state. `arrange.ts` is the pure sound sorting and filtering logic. `components/` holds UI pieces.
- `companion/`: .NET 10 Windows companion (`src`), its xUnit tests (`tests`) and the mock-Soundpad harness used by e2e tests (`harness`).
- CSS is layered: `tokens.css` → `styles.css` → `v2.css` → `polish.css`. Later files override earlier ones, and about 36 selectors are defined in more than one file. Check every file before changing a selector.

## Checks

```powershell
npm run lint; npm run typecheck; npm test
npx playwright install chromium   # once per machine
npm run test:e2e                  # needs the prebuilt harness in companion/harness/bin
```

- E2E tests start `companion/harness/bin/Release/net10.0-windows10.0.19041.0/win-x64/SoundpadWeb.Harness.exe`. Building or rebuilding it needs the .NET 10 SDK.
- With the current harness, the expected e2e result is all passing plus 4 skipped (the opt-in `LIVE_RELAY` Nostr tests).
- The e2e suite runs against the Vite dev server in the working tree. Don't edit `web/src` while it runs, because hot reload will affect the results.

## Conventions

- Bump `package.json` (and the two matching `package-lock.json` entries) for each release. The header and footer read the version from there through the `__APP_VERSION__` define in `vite.config.ts`.
- Bump `REQUIRED_COMPANION` in `web/src/version.ts` only when the website depends on a new companion feature.
- Work on a branch, then open a PR to `main`.
