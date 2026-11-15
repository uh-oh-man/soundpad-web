import { describe, expect, it } from 'vitest';
import {
  ATTACH_QUIET_MS,
  bodies,
  formatBytes,
  kindLabel,
  layoutPayload,
  layoutSource,
  mergeLayouts,
  nextAttach,
  restoreMessage,
  statusLine,
  type BackupStatus,
} from '../../web/src/backups';
import { blankWorkspace } from '../../web/src/workspace';
import { commandSchema, isGuestAllowed, EMPTY, command } from '../../web/src/protocol';

const ID = '20261114T120000Z-1a2b3c4d';
const HOST = 'a'.repeat(64);

describe('backup command bodies', () => {
  it('accept what the page sends and nothing else', () => {
    expect(bodies.backupSettings.parse({ action: 'get' })).toEqual({ action: 'get' });
    expect(() => bodies.backupSettings.parse({ action: 'get', folder: 'C:\\x' })).toThrow();
    expect(
      bodies.backupSettings.parse({
        action: 'set',
        useSuggested: true,
        keepCount: 14,
        afterChangeMinutes: 0,
      }),
    ).toMatchObject({ useSuggested: true });
    for (const bad of [
      { keepCount: 0 },
      { keepCount: 201 },
      { keepDays: 3651 },
      { intervalHours: 0 },
      { afterChangeMinutes: 1441 },
      { keepCount: 1.5 },
    ])
      expect(() => bodies.backupSettings.parse({ action: 'set', ...bad })).toThrow();
    expect(() => bodies.backupNow.parse({ now: true })).toThrow();
    expect(bodies.backupList.parse({ limit: 100, before: ID })).toEqual({ limit: 100, before: ID });
    expect(() => bodies.backupList.parse({ limit: 101 })).toThrow();
    expect(() => bodies.backupDetail.parse({ snapshot: '..\\..\\windows' })).toThrow();
    expect(
      bodies.backupRestore.parse({
        snapshot: ID,
        kind: 'recording',
        ids: ['b'.repeat(32)],
        mode: 'replace',
      }),
    ).toMatchObject({ kind: 'recording' });
    expect(bodies.backupRestore.parse({ kind: 'sentence', undo: 'c'.repeat(32) })).toMatchObject({
      undo: 'c'.repeat(32),
    });
    expect(() => bodies.backupRestore.parse({ snapshot: ID, kind: 'everything' })).toThrow();
    expect(() =>
      bodies.backupRestore.parse({ snapshot: ID, kind: 'clip', ids: ['C:\\file.wav'] }),
    ).toThrow();
    expect(() =>
      bodies.backupRestore.parse({ snapshot: ID, kind: 'clip', path: 'C:\\' }),
    ).toThrow();
    expect(() => bodies.backupAttach.parse({ source: 'guest', workspace: {} })).toThrow();
    expect(bodies.backupLayout.parse({ snapshot: ID, source: 'window' })).toEqual({
      snapshot: ID,
      source: 'window',
    });
  });

  it('are host only: every backup op falls through the guest rules to refused', () => {
    for (const op of [
      'backupSettings',
      'backupNow',
      'backupList',
      'backupDetail',
      'backupRestore',
      'backupAttach',
      'backupLayout',
    ] as const) {
      const c = command(op, { body: {} });
      expect(commandSchema.parse(c).op).toBe(op);
      expect(isGuestAllowed(c, EMPTY)).toBe(false);
    }
  });
});

const status = (
  over: Partial<BackupStatus['status']> = {},
  settings: Partial<BackupStatus['settings']> = {},
): BackupStatus => ({
  settings: {
    folder: 'D:\\Backups',
    keepCount: 14,
    keepDays: 30,
    intervalHours: 24,
    afterChangeMinutes: 10,
    includeNativeFiles: true,
    suggested: 'C:\\Users\\x\\Documents\\Soundpad Web Backups',
    nativeBytes: 0,
    problem: null,
    ...settings,
  },
  status: {
    running: false,
    lastResult: null,
    totalBytes: 0,
    snapshots: 0,
    freeBytes: null,
    ...over,
  },
});

describe('the backup status line', () => {
  const now = new Date(2026, 10, 14, 15, 0).getTime();
  const at = new Date(2026, 10, 14, 14, 2).getTime();
  it('says what happened last, in plain words', () => {
    expect(statusLine(status({}, { folder: null }), now)).toBe(
      'Backups are off. Choose a folder to turn them on.',
    );
    expect(statusLine(status(), now)).toBe('No backup yet.');
    expect(statusLine(status({ running: true }), now)).toBe('Backing up…');
    expect(
      statusLine(
        status({
          lastResult: {
            atUtcMs: at,
            ok: true,
            bytes: 1.2 * 1024 ** 3,
            snapshots: 9,
            message: 'Backed up.',
          },
          freeBytes: 38 * 1024 ** 3,
        }),
        now,
      ),
    ).toBe('Last backup 14:02, 1.2 GB in 9 backups, 38.0 GB free');
    expect(
      statusLine(
        status({
          lastResult: {
            atUtcMs: at,
            ok: false,
            bytes: 0,
            snapshots: 0,
            message: 'Not enough free space for the backup.',
          },
        }),
        now,
      ),
    ).toBe('The last backup (14:02) did not finish: Not enough free space for the backup.');
    expect(
      statusLine(
        status({
          lastResult: {
            atUtcMs: new Date(2026, 10, 13, 9, 5).getTime(),
            ok: true,
            bytes: 2048,
            snapshots: 1,
            message: '',
          },
        }),
        now,
      ),
    ).toBe('Last backup 13 Nov 09:05, 2 KB in 1 backup');
    expect(statusLine(status({}, { problem: 'damaged' }), now)).toBe(
      'Backups are off: the settings file could not be read.',
    );
  });
  it('names sizes, kinds and restore results', () => {
    expect([formatBytes(12), formatBytes(4096), formatBytes(5 * 1024 ** 2)]).toEqual([
      '12 bytes',
      '4 KB',
      '5.0 MB',
    ]);
    expect(
      ['scheduled', 'manual', 'pre-restore', 'pre-update', 'pre-organize', 'x'].map(kindLabel),
    ).toEqual([
      'Scheduled',
      'Manual',
      'Before restore',
      'Before update',
      'Before organizing',
      'Backup',
    ]);
    const r = { restored: 3, skipped: 0, conflicts: [], attention: [], warnings: [] };
    expect(restoreMessage('recording', r)).toBe('Restored 3 recordings.');
    expect(restoreMessage('clip', { ...r, restored: 1, skipped: 1, conflicts: ['x'] })).toBe(
      'Restored 1 kept clip. 1 already here. 1 left as they are.',
    );
  });
});

describe('the layout attach decision', () => {
  it('sends once on load, then 30 s after the layout last changed, and never over 2 MiB', () => {
    let state = nextAttach({ since: 0 }, null, 0).state;
    let r = nextAttach(state, 'A', 1000);
    expect(r.send).toBe(true); // on load
    state = r.state;
    expect(nextAttach(state, 'A', 50000).send).toBe(false); // unchanged since it was sent
    r = nextAttach(state, 'B', 60000);
    expect(r.send).toBe(false); // just changed
    state = r.state;
    r = nextAttach(state, 'C', 70000); // changed again: the quiet time starts over
    state = r.state;
    expect(nextAttach(state, 'C', 70000 + ATTACH_QUIET_MS - 1).send).toBe(false);
    r = nextAttach(state, 'C', 70000 + ATTACH_QUIET_MS);
    expect(r.send).toBe(true);
    expect(nextAttach(r.state, 'C', 200000).send).toBe(false);
    const w = blankWorkspace(HOST);
    expect(layoutPayload(w)).toBe(JSON.stringify(w));
    expect(layoutPayload({ ...w, hidden: ['x'.repeat(2 * 1024 * 1024)] })).toBeNull();
    expect(layoutPayload(null)).toBeNull();
  });
  it('knows the window from a browser', () => {
    expect(layoutSource('window')).toBe('window');
    expect(layoutSource('tab')).toBe('browser');
    expect(layoutSource('view')).toBe('browser');
  });
  it('merges a restored layout without removing anything', () => {
    const a = crypto.randomUUID(),
      b = crypto.randomUUID();
    const current = {
      ...blankWorkspace(HOST),
      folders: [{ id: a, name: 'Mine', parent: null, soundIds: ['1'] }],
      favorites: ['1'],
      order: ['1', '2'],
    };
    const restored = {
      ...blankWorkspace(HOST),
      folders: [
        { id: a, name: 'Old', parent: null, soundIds: [] },
        { id: b, name: 'Backed up', parent: null, soundIds: ['3'] },
      ],
      favorites: ['3'],
      hidden: ['4'],
      order: ['3', '1'],
    };
    const merged = mergeLayouts(current, restored);
    expect(merged.folders.map((f) => f.name)).toEqual(['Mine', 'Backed up']);
    expect(merged.favorites).toEqual(['1', '3']);
    expect(merged.hidden).toEqual(['4']);
    expect(merged.order).toEqual(['1', '2', '3']);
  });
});
