# Backups and restore (2.44)

Soundpad Web backs up what you keep to a folder you choose, and restores single items or the whole library from
there. This is the developer and maintainer description; the user guide is in the README.

Everything here is host only. The companion refuses every backup command from a guest or a relayed command, and the
snapshot's `backups` revision is removed before a snapshot reaches a guest (`network/session.ts`). Guests never see the
section, the list or any path.

## What is backed up

| Kept by the companion | In a backup |
|---|---|
| `state.json` | The whole file: counter, permissions, statistics, activity, icon names, Spotify setting, identity, receipts |
| `library\voice-library.v1.json`, `voice-words.v1.jsonl` | The voice library and each recording's words (the words file compacted) |
| `library\sentences.v1.json` | Sentences, named pieces and performance sets |
| `managed-audio\journal.json` | The kept-audio journal |
| `keep-settings.json`, `mimic-settings.json`, `ui.json` | As they are |
| `backup-workspace.v1.json` | The host window's and host browser's layouts (see Layouts) |
| Kept clips (permanent files the journal records) | Their audio, checked against the journal's hash |
| Recordings' files that are Soundpad's (or Voice Replay Studio's) | Their audio, when "Include Soundpad files used by the voice library" is on (default on) |
| Kept sentence versions | Their audio, checked against the version's hash |
| Icons | Their PNG files |

Not backed up: Temp and anything of the current session, the recording history, drafts, `guest-kept.json` (it
counts this install's guests' kept bytes and is held in memory), the audit trail, and the voice library's `.bak` copy.

## Where

`<folder>\Soundpad Web Backups\<appId[..6]>-<hostId[..8]>\`, where the folder is the one the host chose (when the
chosen folder is already called `Soundpad Web Backups`, it is used as it is). The app id is the kept-audio journal's;
before that journal exists, a part of the host id stands in. Each install writes only its own folder. The list and
restore read every install's folder there, so a backup from before a Windows reinstall (another identity) can be
restored too.

```
blobs\<aa>\<sha256>.<ext>                 audio and icons, content-addressed, written once, shared by every snapshot
snapshots\<yyyyMMddTHHmmssZ>-<id8>\       one backup
    data\…                                the small files, at their paths in the data folder
    manifest.v1.json                      written last
snapshots\.partial-<id8>\                 a backup being written
```

`manifest.v1.json`: `{format:1, minReader:[2,44], id, kind, createdUtcMs, companionVersion, appId, hostId, fuzzy,
warnings[], files[{role, path, sha256, bytes}], blobs[{role, sha256, ext, bytes, original, assetId?, variantId?,
recordingId?, sentenceId?, version?, native, modified?}], bytes, newBytes}`. `kind` is `scheduled`, `manual`,
`pre-restore`, `pre-update` or `pre-organize`. A manifest asking for a newer reader, a damaged one, a damaged small
file (its hash) and a damaged blob (its size, then its hash while it is copied back) are refused.

Blob extensions: `wav png jsonl json mp3 ogg flac m4a aac wma opus` (Soundpad files of other kinds are listed as a
warning and left out).

## How a run works

1. One run at a time. The folder is checked again (rules below) and free space must be the estimate plus 10%.
2. Capture, each under its own lock and in this order: sentences (the sentences lock), the voice library (its lock),
   the journal (the audio gate, background lane), `state.json` (the native gate, background lane: a play waiting goes
   first), then the small settings files.
3. Copy the files the capture names into the blob store on the backup thread (one below-normal thread, outside every
   lock), in 1 MiB chunks, hashing as it goes; a 5 ms pause follows each chunk while Soundpad plays. A blob already
   stored is not copied again: kept clips and sentence audio by their recorded hash, Soundpad files and icons by the
   size and modified time the newest snapshots recorded. A kept file whose content no longer matches its hash, a file
   that is missing, and a Soundpad file that changed twice while it was copied are left out with a warning.
4. If the library, the sentences or the journal changed meanwhile (or an icon was replaced), capture and copy again, up
   to three more times; then the snapshot is marked `fuzzy`.
5. Write the small files into `.partial-<id8>\data\`, the manifest last (flushed), then rename the folder. A crash
   before the rename leaves a partial folder that the next run removes; earlier snapshots are never touched.
6. Retention, then clean-up (below). Save the result in `backup-settings.v1.json`; the snapshot's `backups` revision rises.

Runs never happen during shutdown (the companion's closing cancels a run) and never block Play or Stop. Measured on the
owner's PC (xUnit `BackupNeverDelaysPlayOrStop`, mock Soundpad, a 200 MB library file): a play and its stop took a
median 93 ms with nothing else running and a median 70 ms while the copy ran, and a copy thread that waited for a play
to finish never deadlocked. These are fixture numbers, not real-Soundpad ones.

When: 60 s after the companion starts, every `intervalHours` (default 24; an hour after a run that failed),
`afterChangeMinutes` (default 10; 0 is off)
after the library, sentences or kept audio last changed, on Back up now, and before a restore, an update or an
organization change (`CheckpointH(kind)`, which says "Backups are not set up." when there is no folder, so callers
carry on).

## Retention and clean-up

Kept: the newest `keepCount` (default 14), everything newer than `keepDays` (default 30), the newest of every ISO week,
and every "Before restore" backup for at least 7 days, and every backup a restore is reading from until it has
finished (so its own "Before restore" backup never removes it). Snapshots outside that are removed (each is renamed to
`.partial-<id8>` first, so a run cut short leaves a folder the next run sweeps, never a snapshot without its manifest),
then every blob no remaining manifest refers to. Only names the app makes are ever deleted (`^[a-f0-9]{64}\.(ext)$` under `blobs\<aa>\`, snapshot
and partial folder names, `.tmp-<guid>` copies); links and junctions are never followed; and while any snapshot folder
has a manifest this companion cannot read (damaged, or from a newer version), no blob is removed at all.

## The folder rules

The chosen folder must be fully qualified and local (not UNC), on a drive that exists, not a whole drive, not a link
or junction (nor inside one), writable (a probe file is written and removed), and neither inside nor around the data
folder, Soundpad's own folder (`%APPDATA%\Leppsoft`, only ever compared with) or the Keep folder's `Soundpad Web`
folder. "Use suggested folder" asks the companion for `Documents\Soundpad Web Backups`; the page never builds a path.

## Restore

Every restore first reads and checks what it needs with the stores' own validators, then makes a "Before restore"
backup. For a single item that backup is skipped with a warning when it cannot be made; the whole library refuses
without it.

- **Clip** (a kept file): the blob goes back to the file's exact path only where nothing is there now (verified copy,
  never over a file; a different file there is a conflict). A source the journal lost is put back with
  `ManagedAudioStore.MergePermanent` (only its permanent variants and its reference to a Soundpad original, within the
  journal's limits). Its Soundpad entry is then checked; nothing is imported. If Soundpad no longer lists it, the
  result says "Add `<file>` to Soundpad again (Saved category)" with the path.
- **Recording** and **voice**: `VoiceLibrary.RestoreRecordings` / `RestoreVoices` through the library's normal change
  path: one audit group, so Undo in the voice library takes it back. A recording that is gone comes back with its
  words and, when its voice is gone too, that voice. One that is here is skipped, or with `mode: "replace"` takes the
  backup's title, voice, tags, favourite, corrected text and review mark. A recording's missing file (a kept clip or a
  Soundpad file in the backup) comes back first, as for clips.
- **Sentence**: merged with all its versions through the sentences file's normal save (its limits and the guests'
  share apply). A version's audio goes back only to its exact name, and an existing file must match its hash (a
  different one keeps the sentence out). Sentences have no audit trail: the result carries an Undo token that takes
  the restored sentences back while they are unchanged (in memory, until the companion quits).
- **Whole library**: refused while anything plays ("Stop playback first"; the companion does not stop it), while a Keep
  or promotion is under way, and while Temp is being written. Guests' library and sentence changes are refused while it
  runs ("The host is restoring the library"). It replaces the voice library (the replaced file becomes its `.bak`; a
  damaged one is kept as `voice-library.v1.damaged-<utc>.json`), its words and the sentences, puts missing kept audio,
  recording files, sentence audio and icons back, merges kept sources the journal lost (never session or Temp records,
  and the session stays as it is), replaces the state's counter, statistics, activity, icons, permissions and Spotify
  setting (never its receipts) through the normal durable save, and writes `keep-settings.json`, `mimic-settings.json`
  and `ui.json` when each is a JSON object its loader takes (a damaged copy is left out with a warning). Earlier
  voice-library changes can no longer be undone afterwards. It marks itself busy before its "Before restore" backup:
  from then on an update and an Organize apply are refused, and it is refused while either runs.
- **Files** go back only as audio (wav, mp3, ogg, flac, m4a, aac, wma, opus) of the type the backup's copy is.
- **Identity**: equal, nothing special. Different and this install keeps nothing yet: the backup's identity is adopted
  only when the host ticks "Use this backup's identity (guests' saved layouts move with it)". Different with data: the
  library, sentences, counter and settings are restored, statistics and icons (kept per sound id) are not, and the
  result says so.
- **Layouts**: each host page (the companion window's, and a host browser's) sends its layout with `backupAttach`
  once when it loads and then 30 s after it last changed (at most 2 MiB). The companion keeps both in
  `backup-workspace.v1.json` (`{format:1, pages:{window?, browser?}}`, at most 5 MiB; a damaged one is set aside as
  `backup-workspace.v1.damaged-<utc>.json` and started again). `backupLayout` returns one from a backup; the page checks
  it with `parseWorkspace` and, after the host chose Merge or Replace, applies it with Undo (in memory).

## Unreadable `state.json`

The startup error keeps its words and, when a backup has a copy, adds where it is and the offline command:

```
SoundpadWeb.exe --restore-state "<snapshot folder>" [--data-dir <folder>]
```

It checks the manifest, the copy's hash and the state's rules, refuses while another companion holds
`companion.lock`, keeps the current file as `state.before-restore-<utc>.json`, writes the copy durably, puts back icons
the state names that are missing, prints one line and exits 0 or 1. No window, Soundpad pipe or network.

## Commands

All host only; `body` is checked by `web/src/backups.ts` (zod) and strictly by `CommandBody` in the companion (unknown
fields refused).

| Op | Body | Answer |
|---|---|---|
| `backupSettings` | `{action:'get'}` or `{action:'set', folder?, useSuggested?, keepCount 1–200, keepDays 1–3650, intervalHours 1–168, afterChangeMinutes 0–1440, includeNativeFiles}` | `{settings:{folder, keepCount, keepDays, intervalHours, afterChangeMinutes, includeNativeFiles, suggested, nativeBytes, problem}, status:{running, lastResult, totalBytes, snapshots, freeBytes}}` |
| `backupNow` | `{}` | `{started}`; the revision rises when it starts and ends |
| `backupList` | `{limit≤100, before?}` | `{snapshots:[{id, kind, createdUtcMs, bytes, fuzzy, warnings, other}]}` (`kind: 'unreadable'` for a damaged one; `other`: another install's) |
| `backupDetail` | `{snapshot}` | `{snapshot, kind, createdUtcMs, fuzzy, warnings, clips[{id, name, file, path, status}], recordings[], voices[], sentences[], layouts[], library:{recordings, voices, sentences, clips, icons, sounds, counter, identity}}`; statuses `same`, `missing now`, `changed since`, `not here` |
| `backupRestore` | `{snapshot, kind:'clip'\|'recording'\|'voice'\|'sentence'\|'library', ids?, mode?:'skip'\|'replace', adoptIdentity?}` or `{kind:'sentence', undo}` | `{restored, skipped, conflicts[], attention[{file, path, message}], warnings[], auditGroup?, undoToken?}` |
| `backupAttach` | `{source:'window'\|'browser', workspace}` | `{}` |
| `backupLayout` | `{snapshot, source}` | `{workspace, savedUtcMs}` |

## Files and rolling back

New in the data folder: `backup-settings.v1.json` (`{format:1, folder, keepCount, keepDays, intervalHours,
afterChangeMinutes, includeNativeFiles, lastResult}`, at most 64 KiB, every known field required and in range, unknown
fields kept; a damaged one means backups are off and it is left untouched, and settings cannot be saved over it) and
`backup-workspace.v1.json`. An offline restore may leave `state.before-restore-*.json`. Backups themselves live outside
the data folder. Older companions ignore all of them, and a library a 2.44 restore writes is in the 2.41–2.43 formats
(state schema 2, journal schema 3, library and sentences version 1).

## Tests

- xUnit `BackupTests` and `RestoreTests` (temporary folders, a fake clock, the `BackupFiles` fault seam, a made-up
  Soundpad folder).
- Harness `--backup-roundtrip <temp folder>`: backs up a kept clip and its recording, a kept sentence version, an icon
  and a layout; deletes the kept file, the sentence audio and the icon and damages the voice library; a new companion
  restores the whole library; every file must come back byte for byte. Prints `ROUNDTRIP_OK <snapshot>` and exits 0.
- Harness `--backup-hold-ms N`: every run waits N ms first (the busy-button e2e). Every harness uses a made-up
  Soundpad folder and a "suggested" folder beside its own temporary data folder.
- `--make-rollback-fixture` also writes both new files, a kept clip in the voice library, one backup (in
  `<fixture>-backups`) and a recording removed and restored from it.
- e2e `tests/e2e/backups.spec.ts` (Chromium and WebKit).
- Vitest `tests/unit/backups.test.ts`.

## Not verified

- Backups to network, removable or cloud-synced drives (allowed, not tested).
- A restore after a Windows reinstall or on another PC (argued from the identity rules, not run).
- How long a backup takes, and its disk load, on the owner's real library.
- Anything against the real Soundpad: a restored clip's Soundpad entry check ran against the mock only.
