import { ackSchema, snapshotSchema, type Command, type Snapshot } from '../protocol';
export type BridgeAddress = { port: number; token: string };
export class LocalBridge {
  private socket?: WebSocket;
  private stopped = false;
  private pending = new Map<
    string,
    { resolve: () => void; reject: (e: Error) => void; timer: ReturnType<typeof setTimeout> }
  >();
  constructor(
    private address: BridgeAddress,
    private state: (s: Snapshot) => void,
    private status: (s: string) => void,
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
      ws.send(JSON.stringify({ token: this.address.token }));
    };
    ws.onmessage = (e) => {
      try {
        const data = JSON.parse(e.data);
        if (data.type === 'state') {
          const s = snapshotSchema.parse(data.state);
          clearTimeout(timeout);
          this.state(s);
        } else {
          const ack = ackSchema.parse(data);
          const p = this.pending.get(ack.id);
          if (p) {
            clearTimeout(p.timer);
            this.pending.delete(ack.id);
            if (ack.ok) p.resolve();
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
        setTimeout(() => this.connect(), 2500);
      }
    };
  }
  send(command: Command, actor = 'host') {
    return new Promise<void>((resolve, reject) => {
      if (this.socket?.readyState !== WebSocket.OPEN) {
        reject(Error('Companion is unavailable.'));
        return;
      }
      if (this.pending.size >= 20) {
        reject(Error('Too many pending commands.'));
        return;
      }
      const timer = setTimeout(() => {
        this.pending.delete(command.id);
        reject(Error('Command timed out. Playback is never retried automatically.'));
      }, 12000);
      this.pending.set(command.id, { resolve, reject, timer });
      this.socket.send(JSON.stringify({ actor, command }));
    });
  }
  close() {
    this.stopped = true;
    this.socket?.close();
  }
}
