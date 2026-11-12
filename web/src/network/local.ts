import {
  ackSchema,
  controlOps,
  Disconnected,
  parseSnapshot,
  type Command,
  type Snapshot,
  type Participant,
  slowOps,
} from '../protocol';
export type BridgeAddress = { port: number; token: string };
export class LocalBridge {
  private socket?: WebSocket;
  private stopped = false;
  private audience?: string;
  setAudience(value: string) {
    this.audience = value;
  }
  private latest?: Snapshot;
  private retry?: ReturnType<typeof setTimeout>;
  private pending = new Map<
    string,
    {
      resolve: (data: unknown) => void;
      reject: (e: Error) => void;
      timer: ReturnType<typeof setTimeout>;
      control: boolean;
      owner: string;
    }
  >();
  /** Unanswered commands other than playback and transport ones; only these count toward the cap. */
  private waiting = 0;
  /** Unanswered control commands per sender, so a guest's backlog never crowds out the host's Stop (2.35). */
  private controls = new Map<string, number>();
  private settle(id: string) {
    const p = this.pending.get(id);
    if (!p) return undefined;
    clearTimeout(p.timer);
    this.pending.delete(id);
    if (!p.control) this.waiting--;
    else this.controls.set(p.owner, (this.controls.get(p.owner) ?? 1) - 1);
    return p;
  }
  constructor(
    private address: BridgeAddress,
    private state: (s: Snapshot) => void,
    private status: (s: string) => void,
    private waveform?: (data: unknown) => void,
  ) {
    this.connect();
  }
  private connect() {
    if (this.stopped) return;
    this.status('Connecting to companion');
    const ws = new WebSocket(`ws://127.0.0.1:${this.address.port}/bridge`);
    this.socket = ws;
    const timeout = setTimeout(() => ws.close(), 8000);
    ws.onopen = () => {
      ws.send(JSON.stringify({ token: this.address.token, protocol: 2 }));
    };
    ws.onmessage = (e) => {
      try {
        const data = JSON.parse(e.data);
        if (data.type === 'waveform') {
          this.waveform?.(data.data);
          return;
        }
        if (data.type === 'state' || data.type === 'tick') {
          const s = parseSnapshot(
            data.state,
            data.type === 'tick' ? this.latest?.library : undefined,
          );
          if (this.latest && s.revision < this.latest.revision) return;
          this.latest = s;
          clearTimeout(timeout);
          this.state(s);
        } else {
          const ack = ackSchema.parse(data);
          const p = this.settle(ack.id);
          if (p) {
            if (ack.ok) p.resolve(ack.data);
            else p.reject(Error(ack.error || 'Command rejected.'));
          }
        }
      } catch {
        this.status('Companion protocol mismatch. Update both components.');
        ws.close();
      }
    };
    ws.onerror = () =>
      this.status(
        'Cannot reach companion. Keep the EXE running and allow this browser’s local-network prompt. Chrome or Edge is recommended.',
      );
    ws.onclose = () => {
      clearTimeout(timeout);
      for (const p of this.pending.values()) {
        clearTimeout(p.timer);
        p.reject(new Disconnected('Companion disconnected.'));
      }
      this.pending.clear();
      this.waiting = 0;
      this.controls.clear();
      if (!this.stopped) {
        this.status('Companion disconnected. Reconnecting…');
        this.retry = setTimeout(() => this.connect(), 2500);
      }
    };
  }
  send(command: Command, actor = 'host', participant?: Participant, requester?: string) {
    return new Promise<unknown>((resolve, reject) => {
      if (this.socket?.readyState !== WebSocket.OPEN) {
        reject(new Disconnected('Companion is unavailable.'));
        return;
      }
      // Slow work (the host's own and every guest's) shares 20 places. A Play or Stop must never wait
      // for one (2.35): control commands are answered quickly and skip the cap, up to a bound that
      // only a runaway sender reaches.
      const control = controlOps.includes(command.op);
      const owner = actor === 'host' ? 'host' : (requester ?? 'guest');
      if (
        control
          ? (this.controls.get(owner) ?? 0) >= (owner === 'host' ? 512 : 64)
          : this.waiting >= 20
      ) {
        reject(Error('Too many pending commands.'));
        return;
      }
      const timer = setTimeout(
        () => {
          this.settle(command.id);
          reject(Error('Command timed out. Playback is never retried automatically.'));
        },
        // Removing hundreds of duplicate categories one by one can take minutes (2.15.9).
        command.op === 'cleanupCategories'
          ? 600000
          : slowOps.includes(command.op)
            ? 90000
            : command.op === 'waveform'
              ? 45000
              : 12000,
      );
      // A repeated id replaces its earlier request, so the count above stays exact.
      this.settle(command.id)?.reject(Error('Command sent again.'));
      this.pending.set(command.id, { resolve, reject, timer, control, owner });
      if (!control) this.waiting++;
      else this.controls.set(owner, (this.controls.get(owner) ?? 0) + 1);
      this.socket.send(
        JSON.stringify({ actor, command, participant, requester, audience: this.audience }),
      );
    });
  }
  async upload(bytes: Uint8Array, name: string, by: string, host = false, owner = 'host') {
    const response = await fetch(`http://127.0.0.1:${this.address.port}/session-audio`, {
      method: 'POST',
      headers: {
        Authorization: `Bearer ${this.address.token}`,
        'Content-Type': 'application/octet-stream',
        'X-Audio-Name': encodeURIComponent(name),
        'X-Audio-By': encodeURIComponent(by),
        'X-Audio-Actor': host ? 'host' : 'guest',
        'X-Audio-Owner': owner,
        'X-Audio-Audience': this.audience ?? '',
      },
      body: bytes as Uint8Array<ArrayBuffer>,
    });
    if (!response.ok) throw Error(`Upload rejected (${response.status}).`);
    const result = ackSchema.omit({ id: true }).parse(await response.json());
    if (!result.ok) throw Error(result.error || 'Audio import failed.');
    return result.data;
  }
  close() {
    this.stopped = true;
    clearTimeout(this.retry);
    this.socket?.close();
  }
}
