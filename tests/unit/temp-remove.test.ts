import { describe, it, expect, vi, beforeEach } from 'vitest';

const run = vi.fn();
const notify = vi.fn();
let companionVersion = '2.46.0';
vi.mock('../../web/src/controller', () => ({
  controller: {
    run: (...a: unknown[]) => run(...a),
    notify: (...a: unknown[]) => notify(...a),
    getSnapshot: () => ({ state: { companionVersion } }),
  },
}));
const { removeAllMyTemp, removeTempSource } = await import('../../web/src/temp-remove');
const { guestOwns, removedFromTemp, resetTempAssets, tempAssetsSnapshot } =
  await import('../../web/src/temp-assets');
type Asset = Parameters<typeof removedFromTemp>[0];

const a = '11111111-1111-4111-8111-111111111111';
const b = '22222222-2222-4222-8222-222222222222';
const hidden = () => [...tempAssetsSnapshot().hidden];
/** The notice's Undo button. */
const undo = () => (notify.mock.calls.at(-1)![2] as { label: string; run: () => void }).run;
const flush = () => new Promise((r) => setTimeout(r, 0));

const variant = (state: string, extra: Partial<Asset['variants'][number]> = {}) => ({
  id: crypto.randomUUID(),
  soundId: 's',
  orientation: 'normal',
  state,
  ready: state === 'ready',
  permanent: false,
  legacyApproval: false,
  current: true,
  nativeLabel: 'file',
  ...extra,
});
const asset = (state: string, variants: Asset['variants'], uploader = 'Ann'): Asset => ({
  id: a,
  name: 'Clip',
  origin: 'upload',
  uploader,
  state,
  normal: null,
  reversed: null,
  variants,
  hasTemporary: true,
});

describe('a removed Temp source is off the list (2.46)', () => {
  it('counts a source whose files only wait to be cleaned up as removed, so nothing shows "pending"', () => {
    // The real Soundpad keeps a removed file until the Temp category goes: the companion reports it as
    // "cleanup-pending" with its variant pending too. That is not a Temp item any more.
    expect(removedFromTemp(asset('cleanup-pending', [variant('cleanup-pending')]))).toBe(true);
    expect(removedFromTemp(asset('removed', []))).toBe(true);
    // A reverse of a library sound: its native original stays, and is not Temp audio.
    expect(
      removedFromTemp(
        asset('cleanup-pending', [
          variant('ready', { nativeLabel: null }),
          variant('cleanup-pending', { orientation: 'reversed' }),
        ]),
      ),
    ).toBe(true);
  });
  it('keeps a source that still has Temp audio or a kept copy', () => {
    expect(removedFromTemp(asset('temporary', [variant('ready')]))).toBe(false);
    // An older file being replaced while the new one is made, or a kept copy, stays on the list.
    expect(
      removedFromTemp(
        asset('cleanup-pending', [variant('cleanup-pending'), variant('processing')]),
      ),
    ).toBe(false);
    expect(
      removedFromTemp(
        asset('cleanup-pending', [
          variant('cleanup-pending'),
          variant('ready', { permanent: true }),
        ]),
      ),
    ).toBe(false);
  });
  it("trusts the companion's list of a guest's own sources, and their name before 2.46", () => {
    const clip = asset('temporary', [variant('ready')], 'Ann (2)');
    const data = (mine?: string[] | null) =>
      ({ manager: { assets: [clip] }, mine }) as unknown as Parameters<typeof guestOwns>[0];
    expect(guestOwns(data([a]), clip, 'Ann')).toBe(true);
    expect(guestOwns(data([]), clip, 'Ann (2)')).toBe(false);
    expect(guestOwns(data(undefined), clip, 'Ann (2)')).toBe(true);
    expect(guestOwns(data(null), clip, 'Ann')).toBe(false);
  });
});

describe('Remove takes the source off the list at once (2.46)', () => {
  beforeEach(() => {
    run.mockReset();
    notify.mockReset();
    resetTempAssets();
    companionVersion = '2.46.0';
  });
  it('hides it before the companion answers, and offers Undo', async () => {
    let answer!: (v: unknown) => void;
    run.mockReturnValueOnce(new Promise((r) => (answer = r)));
    const done = removeTempSource({ id: a, name: 'Clip' });
    expect(hidden()).toEqual([a]);
    expect(run).toHaveBeenCalledWith(
      'removeAudio',
      { assetId: a, action: 'stopIfPlaying' },
      { quiet: true },
    );
    answer(undefined);
    expect(await done).toBe(true);
    expect(hidden()).toEqual([a]);
    expect(notify.mock.calls.at(-1)!.slice(0, 2)).toEqual(['Removed “Clip” from Temp.', 'success']);
    // Undo brings it back.
    run.mockResolvedValueOnce({ restored: 1, assetIds: [a] });
    undo()();
    await flush();
    expect(run).toHaveBeenLastCalledWith('tempRestore', { assetId: a }, { quiet: true });
    expect(hidden()).toEqual([]);
    expect(notify).toHaveBeenLastCalledWith('“Clip” is back in Temp.', 'success');
  });
  it('shows it again with the reason when the companion refuses', async () => {
    run.mockRejectedValueOnce(
      new Error('Guests can remove only temporary audio they added themselves.'),
    );
    expect(await removeTempSource({ id: a, name: 'Clip' })).toBe(false);
    expect(hidden()).toEqual([]);
    expect(notify).toHaveBeenLastCalledWith(
      'Could not remove “Clip”: Guests can remove only temporary audio they added themselves.',
      'error',
    );
  });
  it('offers no Undo before 2.46', async () => {
    companionVersion = '2.45.0';
    run.mockResolvedValueOnce(undefined);
    await removeTempSource({ id: a, name: 'Clip' });
    expect(notify.mock.calls.at(-1)![2]).toBeUndefined();
  });
  it("removes all of a guest's clips in one command, with Undo of all", async () => {
    run.mockResolvedValueOnce({ removed: 2, assetIds: [a, b] });
    expect(await removeAllMyTemp([a])).toBe(true);
    expect(run).toHaveBeenCalledWith(
      'tempRemoveMine',
      { action: 'stopIfPlaying' },
      { quiet: true },
    );
    expect(hidden().sort()).toEqual([a, b]);
    expect(notify.mock.calls.at(-1)!.slice(0, 2)).toEqual([
      'Removed your 2 clips from Temp.',
      'success',
    ]);
    run.mockResolvedValueOnce({ restored: 2, assetIds: [a, b] });
    undo()();
    await flush();
    expect(run).toHaveBeenLastCalledWith('tempRestore', { assetIds: [a, b] }, { quiet: true });
    expect(hidden()).toEqual([]);
    expect(notify).toHaveBeenLastCalledWith('Your 2 clips are back in Temp.', 'success');
  });
  it('puts every clip back when removing all is refused', async () => {
    run.mockRejectedValueOnce(new Error('The host has not allowed guests to add or remove audio.'));
    expect(await removeAllMyTemp([a, b])).toBe(false);
    expect(hidden()).toEqual([]);
    expect(notify).toHaveBeenLastCalledWith(
      'Could not remove your clips: The host has not allowed guests to add or remove audio.',
      'error',
    );
  });
  it('says so when Undo comes too late', async () => {
    run.mockResolvedValueOnce(undefined);
    await removeTempSource({ id: a, name: 'Clip' });
    run.mockRejectedValueOnce(
      new Error("It is already gone from Soundpad, so it can't come back."),
    );
    undo()();
    await flush();
    expect(hidden()).toEqual([a]);
    expect(notify).toHaveBeenLastCalledWith(
      "Could not bring it back: It is already gone from Soundpad, so it can't come back.",
      'error',
    );
  });
});
