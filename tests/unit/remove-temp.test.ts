import { describe, it, expect, vi, beforeEach } from 'vitest';

const run = vi.fn();
const notify = vi.fn();
let snapshot: unknown;
vi.mock('../../web/src/controller', () => ({
  controller: { run: (...a: unknown[]) => run(...a), notify, getSnapshot: () => snapshot },
}));
const { removeNewTempAudio } = await import('../../web/src/remove-temp');

const id = '11111111-1111-4111-8111-111111111111';
/** What the page last heard: nothing playing, this source ('s1') playing, or another sound ('s2'). */
const heard = (soundId: string | null, companionVersion = '2.29.3') => ({
  state: {
    companionVersion,
    library: {
      sounds: [
        { id: 's1', assetId: id },
        { id: 's2', assetId: null },
      ],
    },
    playback: { soundId, status: soundId ? 'PLAYING' : 'STOPPED' },
  },
});
const refusal = new Error('Stop playback before removing temporary audio.');
const ops = () => run.mock.calls.map((c) => c[0]);

describe('Undo removes a just-made Temp source (2.28/2.29)', () => {
  beforeEach(() => {
    run.mockReset();
    notify.mockReset();
    vi.useFakeTimers();
  });
  it('stops the source when this page knows it is playing, then removes it', async () => {
    snapshot = heard('s1');
    run.mockResolvedValue(undefined);
    await removeNewTempAudio(id);
    expect(ops()).toEqual(['stop', 'removeAudio']);
    expect(notify).not.toHaveBeenCalled();
  });
  it('waits for a late playback report, stops the source and retries, with no error shown', async () => {
    snapshot = heard(null);
    run
      .mockImplementationOnce(() => {
        // The companion refuses; the page hears a moment later that the new source is playing.
        setTimeout(() => (snapshot = heard('s1')), 300);
        return Promise.reject(refusal);
      })
      .mockImplementationOnce(() => {
        snapshot = heard(null);
        return Promise.resolve();
      })
      .mockResolvedValue(undefined);
    const done = removeNewTempAudio(id);
    await vi.runAllTimersAsync();
    await done;
    expect(ops()).toEqual(['removeAudio', 'stop', 'removeAudio']);
    expect(run.mock.calls.filter((c) => c[0] === 'removeAudio').every((c) => c[2]?.quiet)).toBe(
      true,
    );
    expect(notify).not.toHaveBeenCalled();
  });
  it('never stops a different sound: it shows the refusal instead (2.29.3)', async () => {
    snapshot = heard('s2');
    run.mockRejectedValueOnce(refusal);
    const done = removeNewTempAudio(id);
    const outcome = expect(done).rejects.toThrow('Stop playback');
    await vi.runAllTimersAsync();
    await outcome;
    expect(ops()).toEqual(['removeAudio']);
    expect(notify).toHaveBeenCalledWith(refusal.message, 'error');
  });
  it('with companion 2.30+, lets the companion decide in one request (no guessing here)', async () => {
    snapshot = heard(null, '2.30.0');
    run.mockResolvedValue(undefined);
    await removeNewTempAudio(id);
    expect(run.mock.calls).toEqual([['removeAudio', { assetId: id, action: 'stopIfPlaying' }]]);
  });
  it('reports any other refusal once and gives up', async () => {
    snapshot = heard(null);
    run.mockRejectedValue(new Error('Host permission required.'));
    await expect(removeNewTempAudio(id)).rejects.toThrow('Host permission required.');
    expect(notify).toHaveBeenCalledWith('Host permission required.', 'error');
    expect(run).toHaveBeenCalledTimes(1);
  });
});
