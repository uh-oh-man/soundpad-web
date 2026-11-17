import { describe, it, expect, vi, beforeEach } from 'vitest';

vi.mock('../../web/src/controller', () => ({
  controller: {
    run: vi.fn(() => new Promise(() => {})),
    notify: vi.fn(),
    getSnapshot: () => ({ state: { companionVersion: '2.46.0' } }),
  },
}));
const { guestMayRemoveTemp, goneFromTemp, tempWaitingText } =
  await import('../../web/src/temp-rights');
const { deleteWithUndo, deletedNow, resetPendingDeletes } =
  await import('../../web/src/mimic/pending-delete');
const { tempResponse } = await import('../../web/src/temp-assets');
type Data = ReturnType<typeof tempResponse.parse>;
type Asset = Data['manager']['assets'][number];

const asset = (id: string): Asset => ({
  id,
  name: 'Clip',
  origin: 'upload',
  uploader: 'Ann',
  state: 'temporary',
  normal: null,
  reversed: null,
  variants: [
    {
      id: id + '-v',
      soundId: 's',
      orientation: 'normal',
      state: 'ready',
      ready: true,
      permanent: false,
      legacyApproval: false,
      current: true,
      nativeLabel: 'file',
    },
  ],
  hasTemporary: true,
});
const data = (waiting?: { files: number; bytes: number } | null): Data =>
  tempResponse.parse({
    manager: {
      revision: 1,
      assets: [],
      issues: [],
      blocked: false,
      temporaryBytes: waiting?.bytes ?? 0,
      ...(waiting !== undefined ? { waiting } : {}),
    },
    locations: null,
  });
const permissions = (p: { guestUploads?: boolean; guestMimic?: boolean }) =>
  p as Parameters<typeof guestMayRemoveTemp>[0]['permissions'];

describe('who may remove Temp clips (Temp and Mimic agree)', () => {
  it('offers a guest allowed only Mimic a Remove only on a companion that accepts it (2.46)', () => {
    const mimicOnly = permissions({ guestUploads: false, guestMimic: true });
    // 2.45 refuses such a guest ("The host has not allowed guests to add or remove audio").
    expect(guestMayRemoveTemp({ permissions: mimicOnly, companionVersion: '2.45.0' })).toBe(false);
    expect(guestMayRemoveTemp({ permissions: mimicOnly, companionVersion: '2.46.0' })).toBe(true);
    // Uploads allowed: removing their own uploads has worked since guest Undo.
    const uploads = permissions({ guestUploads: true, guestMimic: false });
    expect(guestMayRemoveTemp({ permissions: uploads, companionVersion: '2.45.0' })).toBe(true);
    const neither = permissions({ guestUploads: false, guestMimic: false });
    expect(guestMayRemoveTemp({ permissions: neither, companionVersion: '2.46.0' })).toBe(false);
  });
});

describe('what the Temp list leaves out', () => {
  beforeEach(() => resetPendingDeletes());
  it('leaves out a clip deleted in Mimic while its Undo notice still shows', () => {
    const clip = asset('11111111-1111-4111-8111-111111111111');
    const other = asset('22222222-2222-4222-8222-222222222222');
    expect(goneFromTemp(clip, new Set(), deletedNow())).toBe(false);
    deleteWithUndo(clip.id, 'Deleted “Clip” from Temp.', () => Promise.resolve());
    // Before this, Temp still listed it: its Remove made the later delete fail, and Keep kept a deleted clip.
    expect(goneFromTemp(clip, new Set(), deletedNow())).toBe(true);
    expect(goneFromTemp(other, new Set(), deletedNow())).toBe(false);
    // A Remove pressed in Temp itself hides it too.
    expect(goneFromTemp(other, new Set([other.id]), deletedNow())).toBe(true);
  });
});

describe('removed files waiting to be freed (2.47)', () => {
  it('says how many removed files still take Temp space and what frees them', () => {
    const waiting = data({ files: 31, bytes: 31 * 1048576 });
    expect(tempWaitingText(waiting, true, '2.47.0')).toBe(
      "Waiting to be freed: 31 removed files · 31 MB. They still use Temp's space; Clear all temporary audio frees them (quitting the companion does too).",
    );
    expect(tempWaitingText(waiting, false, '2.47.0')).toContain(
      "the host's Clear all temporary audio frees them",
    );
    expect(tempWaitingText(data({ files: 1, bytes: 524288 }), true, '2.47.0')).toContain(
      '1 removed file · 0.5 MB',
    );
  });
  it('says nothing when none wait, or on an older companion', () => {
    expect(tempWaitingText(data(null), true, '2.47.0')).toBeUndefined();
    expect(tempWaitingText(data(), true, '2.47.0')).toBeUndefined();
    expect(tempWaitingText(undefined, true, '2.47.0')).toBeUndefined();
    expect(tempWaitingText(data({ files: 3, bytes: 3 }), true, '2.46.0')).toBeUndefined();
  });
});
