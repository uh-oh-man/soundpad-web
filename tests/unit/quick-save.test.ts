import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  canQuickSave,
  savedMessage,
  savedSchema,
  unsavedClips,
} from '../../web/src/library/quick-save';
import { noticeState, resetNotices, notify, runNoticeAction } from '../../web/src/notifications';
import { command, EMPTY, isGuestAllowed } from '../../web/src/protocol';
import type { TempAsset } from '../../web/src/temp-assets';

// One-press Save (2.46, from the owner's 2.45 feedback "cant even save voices" and "just save").

const state = (companionVersion: string, guestKeep?: boolean) => ({
  companionVersion,
  permissions: { ...EMPTY.permissions, ...(guestKeep !== undefined && { guestKeep }) },
});

describe('who may Save in one press', () => {
  it('needs a 2.46 companion; a guest also needs guest Keep and a host page that relays it', () => {
    expect(canQuickSave(state('2.45.0'), true, '2.46.0')).toBe(false);
    expect(canQuickSave(state('2.46.0'), true, '')).toBe(true);
    expect(canQuickSave(state('2.46.0', true), false, '2.46.0')).toBe(true);
    expect(canQuickSave(state('2.46.0', false), false, '2.46.0')).toBe(false);
    // An older host page would drop a guest who sent a command it does not know.
    expect(canQuickSave(state('2.46.0', true), false, '2.45.0')).toBe(false);
    expect(canQuickSave(state('2.46.0', true), false, '')).toBe(false);
  });

  it('relays a guest Save only with guest Keep, and never with a folder or category of their own', () => {
    const allowed = { ...EMPTY, permissions: { ...EMPTY.permissions, guestKeep: true } };
    const save = command('libraryKeep', { assetId: crypto.randomUUID() });
    expect(isGuestAllowed(save, allowed)).toBe(true);
    expect(isGuestAllowed(save, EMPTY)).toBe(false);
    expect(
      isGuestAllowed(
        command('libraryKeep', { assetId: crypto.randomUUID(), destinationDirectory: 'D:\\x' }),
        allowed,
      ),
    ).toBe(false);
    expect(
      isGuestAllowed(
        command('libraryKeep', { assetId: crypto.randomUUID(), categoryId: 2, categoryName: 'A' }),
        allowed,
      ),
    ).toBe(false);
  });
});

describe('the Saved notice', () => {
  it('says the name it was saved under and where it went', () => {
    expect(
      savedMessage(
        { title: 'Got him', added: true, category: 'Saved - Soundpad Web [abc]', folder: null },
        'Clip',
      ),
    ).toBe(
      'Saved “Got him” to the voice library and Soundpad’s “Saved - Soundpad Web [abc]” category.',
    );
    // The host is told the folder too; a missing title falls back to the name on the page.
    expect(
      savedMessage(
        { title: '', category: null, folder: 'D:\\Sounds\\Soundpad Web' },
        'Mimic 22.25.01',
      ),
    ).toBe('Saved “Mimic 22.25.01” to the voice library. Its file is in D:\\Sounds\\Soundpad Web.');
    expect(savedMessage({ title: 'Again', added: false }, 'x')).toBe(
      '“Again” is already in the voice library.',
    );
  });

  it('reads what a 2.45 companion answered for a draft (no name, no place) as well as 2.46', () => {
    expect(savedSchema.parse({ recordingId: 'a'.repeat(32) }).title).toBeUndefined();
    const full = savedSchema.parse({
      recordingId: 'a'.repeat(32),
      title: 'Line',
      added: true,
      category: 'Saved',
      folder: null,
      assetId: 'ignored',
    });
    expect(full).toEqual({
      recordingId: 'a'.repeat(32),
      title: 'Line',
      added: true,
      category: 'Saved',
      folder: null,
    });
  });
});

describe('notices with two buttons', () => {
  beforeEach(() => resetNotices());
  afterEach(() => vi.useRealTimers());
  it('keeps both, and Activity runs either one once', () => {
    const undo = vi.fn(),
      rename = vi.fn();
    const id = notify(
      'Saved',
      'success',
      { label: 'Undo', run: undo },
      { label: 'Rename', run: rename },
    );
    const { notices, history } = noticeState();
    expect(notices[0].second?.label).toBe('Rename');
    expect(history[0].second?.label).toBe('Rename');
    expect(runNoticeAction(id, 'second')).toBe(true);
    expect(rename).toHaveBeenCalledOnce();
    // Done: neither runs again.
    expect(runNoticeAction(id)).toBe(false);
    expect(undo).not.toHaveBeenCalled();
    // A second button alone is never shown.
    notify('Lonely', 'info', undefined, { label: 'Rename', run: rename });
    expect(noticeState().notices.at(-1)?.second).toBeUndefined();
  });
});

describe('Voices lists what can still be saved', () => {
  const variant = (over: Partial<TempAsset['variants'][number]> = {}) => ({
    id: crypto.randomUUID(),
    soundId: 's',
    orientation: 'normal',
    state: 'ready',
    ready: true,
    permanent: false,
    legacyApproval: false,
    current: true,
    nativeLabel: 'label',
    ...over,
  });
  const asset = (name: string, over: Partial<TempAsset> = {}, v = variant()): TempAsset => ({
    id: crypto.randomUUID(),
    name,
    origin: 'upload',
    uploader: 'Host',
    state: 'temporary',
    normal: v,
    reversed: null,
    variants: [v],
    hasTemporary: true,
    ...over,
  });
  it('shows the newest Temp clips first, not kept ones, native sources or clips still being made', () => {
    const list = [
      asset('Oldest'),
      asset('Kept', { state: 'permanent', hasTemporary: false }, variant({ permanent: true })),
      asset('Reversed sound', { origin: 'native' }),
      asset('Being made', {}, variant({ state: 'preparing', nativeLabel: null })),
      asset('Newest'),
    ];
    expect(unsavedClips(list).map((a) => a.name)).toEqual(['Newest', 'Oldest']);
    expect(unsavedClips(list, 1).map((a) => a.name)).toEqual(['Newest']);
  });
});
