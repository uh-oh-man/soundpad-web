import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { LocalBridge } from '../../web/src/network/local';
import { command, Disconnected, EMPTY, type Command } from '../../web/src/protocol';

/** The companion's socket, answered by hand. No real socket is opened. */
class FakeSocket {
  static OPEN = 1;
  static last: FakeSocket;
  readyState = FakeSocket.OPEN;
  frames: { actor: string; command: Command }[] = [];
  onopen?: () => void;
  onmessage?: (e: { data: string }) => void;
  onerror?: () => void;
  onclose?: () => void;
  constructor(public url: string) {
    FakeSocket.last = this;
  }
  send(text: string) {
    const frame = JSON.parse(text);
    if (frame.command) this.frames.push(frame);
  }
  close() {
    this.readyState = 3;
    this.onclose?.();
  }
  answer(id: string, data?: unknown) {
    this.onmessage?.({ data: JSON.stringify({ type: 'ack', id, ok: true, data }) });
  }
}
let bridge: LocalBridge;
let socket: FakeSocket;
beforeEach(() => {
  vi.stubGlobal('WebSocket', FakeSocket);
  bridge = new LocalBridge(
    { port: 1, token: 'a'.repeat(64) },
    () => {},
    () => {},
  );
  socket = FakeSocket.last;
  socket.onmessage?.({ data: JSON.stringify({ type: 'state', state: EMPTY }) });
});
afterEach(() => {
  bridge.close();
  vi.unstubAllGlobals();
});

describe('host tab to companion (2.35)', () => {
  it('never makes a Play or Stop wait behind unanswered slow work', async () => {
    const failed = (c: Command, actor = 'host') =>
      bridge.send(c, actor).catch((e: Error) => e.message);
    // The host's own and every relayed guest's slow commands share 20 places.
    const slow = Array.from({ length: 20 }, (_, i) =>
      failed(command(i % 2 ? 'trimAudio' : 'waveform'), i % 3 ? 'guest' : 'host'),
    );
    expect(socket.frames).toHaveLength(20);
    await expect(bridge.send(command('statistics'))).rejects.toThrow('Too many pending commands');
    expect(socket.frames).toHaveLength(20); // refused here, never sent
    // Playback and transport commands skip that cap, for the host and for a relayed guest.
    const ops = ['play', 'startAt', 'region', 'stop', 'seek', 'pause', 'audioPlay', 'reverse'];
    const clicks = ops.map((op, i) =>
      bridge.send(command(op as Command['op']), i % 2 ? 'guest' : 'host'),
    );
    const panic = bridge.send(command('panic', { action: 'enable' }));
    expect(socket.frames.slice(20).map((f) => f.command.op)).toEqual([...ops, 'panic']);
    for (const frame of socket.frames.slice(20))
      socket.answer(frame.command.id, { superseded: true, reason: 'newer' });
    // A superseded acknowledgement is a success, with its data passed on.
    expect(await clicks[0]).toEqual({ superseded: true, reason: 'newer' });
    await Promise.all([...clicks, panic]);
    // Answered playback did not free a place for slow work, and answered slow work does.
    await expect(bridge.send(command('statistics'))).rejects.toThrow('Too many pending commands');
    socket.answer(socket.frames[0].command.id);
    const next = bridge.send(command('statistics'));
    expect(socket.frames.at(-1)!.command.op).toBe('statistics');
    socket.answer(socket.frames.at(-1)!.command.id, { totals: {} });
    expect(await next).toEqual({ totals: {} });
    // Losing the companion ends every wait, and says it was the connection.
    const click = bridge.send(command('play')).catch((e: Error) => e);
    socket.close();
    expect(await click).toBeInstanceOf(Disconnected);
    expect((await Promise.all(slow)).slice(1)).toEqual(Array(19).fill('Companion disconnected.'));
    await expect(bridge.send(command('play'))).rejects.toBeInstanceOf(Disconnected);
  });
});
