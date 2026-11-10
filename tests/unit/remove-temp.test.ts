import { describe, it, expect, vi, beforeEach } from 'vitest';

const run = vi.fn();
const notify = vi.fn();
let snapshot: unknown;
vi.mock('../../web/src/controller', () => ({
  controller: { run: (...a: unknown[]) => run(...a), notify, getSnapshot: () => snapshot },
}));
const { removeNewTempAudio } = await import('../../web/src/remove-temp');

const id = '11111111-1111-4111-8111-111111111111';
const playing = (status: string) => ({
  state: {
    library: { sounds: [{ id: 's1', assetId: id }] },
    playback: { soundId: status === 'STOPPED' ? null : 's1', status },
  },
});
const refuse = () => Promise.reject(new Error('Stop playback before removing temporary audio.'));

describe('Undo removes a just-made Temp source (2.28/2.29)', () => {
  beforeEach(() => {
    run.mockReset();
    notify.mockReset();
    vi.useFakeTimers();
  });
  it('stops a source this page knows is playing, then removes it', async () => {
    snapshot = playing('PLAYING');
    run.mockResolvedValue(undefined);
    await removeNewTempAudio(id);
    expect(run.mock.calls.map((c) => c[0])).toEqual(['stop', 'removeAudio']);
    expect(notify).not.toHaveBeenCalled();
  });
  it('stops and retries when the companion says it plays before this page heard so', async () => {
    snapshot = playing('STOPPED');
    run.mockImplementationOnce(refuse).mockResolvedValue(undefined);
    const done = removeNewTempAudio(id);
    await vi.runAllTimersAsync();
    await done;
    expect(run.mock.calls.map((c) => c[0])).toEqual(['removeAudio', 'stop', 'removeAudio']);
    // The expected refusal is never shown: removals run quietly.
    expect(run.mock.calls.filter((c) => c[0] === 'removeAudio').every((c) => c[2]?.quiet)).toBe(
      true,
    );
    expect(notify).not.toHaveBeenCalled();
  });
  it('reports any other refusal once and gives up', async () => {
    snapshot = playing('STOPPED');
    run.mockRejectedValue(new Error('Host permission required.'));
    await expect(removeNewTempAudio(id)).rejects.toThrow('Host permission required.');
    expect(notify).toHaveBeenCalledWith('Host permission required.', 'error');
    expect(run).toHaveBeenCalledTimes(1);
  });
});
