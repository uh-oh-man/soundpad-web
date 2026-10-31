import {
  ackSchema,
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
    }
  >();
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
          const p = this.pending.get(ack.id);
          if (p) {
            clearTimeout(p.timer);
            this.pending.delete(ack.id);
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
        p.reject(Error('Companion disconnected.'));
      }
      this.pending.clear();
      if (!this.stopped) {
        this.status('Companion disconnected. Reconnecting…');
        this.retry = setTimeout(() => this.connect(), 2500);
      }
    };
  }
  send(command: Command, actor = 'host', participant?: Participant, requester?: string) {
    return new Promise<unknown>((resolve, reject) => {
      if (this.socket?.readyState !== WebSocket.OPEN) {
        reject(Error('Companion is unavailable.'));
        return;
      }
      if (this.pending.size >= 20) {
        reject(Error('Too many pending commands.'));
        return;
      }
      const timer = setTimeout(
        () => {
          this.pending.delete(command.id);
          reject(Error('Command timed out. Playback is never retried automatically.'));
        },
        slowOps.includes(command.op) ? 90000 : command.op === 'waveform' ? 45000 : 12000,
      );
      this.pending.set(command.id, { resolve, reject, timer });
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
