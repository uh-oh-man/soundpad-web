/** Splitting app messages into relay-sized chunks and putting them back together with bounded memory. */
import { Reader, Writer, concat, fromUtf8, utf8 } from './bytes';
import type { Bytes } from './bytes';

export type Chunk = {
  /** Counts up per sender; groups the chunks of one message and orders superseding messages. */
  messageId: number;
  /**
   * Control messages: position in the sender's stream to this recipient (1, 2, 3…), so the
   * receiver can hand them over in the order they were sent. 0 for bulk messages, which are unordered.
   */
  order: number;
  index: number;
  count: number;
  binary: boolean;
  /** Supersede key, or empty. A newer message with the same key makes older ones obsolete. */
  key: string;
  /** Length of the whole message in bytes. */
  total: number;
  data: Bytes;
};

/** Bytes a chunk header adds in front of the data, not counting the key. */
export const CHUNK_HEADER_BYTES = 18;
export const MAX_KEY_BYTES = 64;

export function encodeChunk(c: Chunk): Bytes {
  return new Writer()
    .u32(c.messageId)
    .u32(c.order)
    .u16(c.index)
    .u16(c.count)
    .u8(c.binary ? 1 : 0)
    .short(utf8(c.key))
    .u32(c.total)
    .bytes(c.data)
    .done();
}

/** Throws `RangeError` on a malformed chunk. */
export function decodeChunk(r: Reader): Chunk {
  const messageId = r.u32();
  const order = r.u32();
  const index = r.u16();
  const count = r.u16();
  const kind = r.u8();
  const key = fromUtf8(r.short());
  const total = r.u32();
  const data = r.rest();
  if (!messageId || !count || index >= count || kind > 1 || data.length > total)
    throw RangeError('Malformed chunk');
  if (count === 1 && data.length !== total) throw RangeError('Malformed chunk');
  return { messageId, order, index, count, binary: kind === 1, key, total, data };
}

type Partial = {
  owner: string;
  first: Chunk;
  parts: (Bytes | undefined)[];
  have: number;
  bytes: number;
  lastAt: number;
};

export type ReassemblyLimits = {
  /** Largest message accepted. */
  maxMessageBytes: number;
  /** Total declared size of all unfinished messages, across every sender. */
  maxPendingBytes: number;
  /** Unfinished messages kept per sender. */
  maxPendingPerOwner: number;
  /** An unfinished message is dropped when no chunk has arrived for this long. */
  timeoutMs: number;
};

/**
 * Collects chunks until a message is whole. Memory is bounded: when a limit would be exceeded the
 * unfinished message that has waited longest is dropped, and a stalled one is dropped after the timeout.
 */
export class Reassembler {
  private limits: ReassemblyLimits;
  private pending = new Map<string, Partial>();
  private pendingBytes = 0;
  constructor(limits: ReassemblyLimits) {
    this.limits = limits;
  }

  /** Number of unfinished messages (for tests and diagnostics). */
  get size() {
    return this.pending.size;
  }

  private remove(id: string) {
    const partial = this.pending.get(id);
    if (!partial) return;
    this.pending.delete(id);
    this.pendingBytes -= partial.first.total;
  }

  private stalest(owner?: string) {
    let found: [string, Partial] | undefined;
    for (const entry of this.pending)
      if ((!owner || entry[1].owner === owner) && (!found || entry[1].lastAt < found[1].lastAt))
        found = entry;
    return found?.[0];
  }

  /**
   * Adds one chunk from `owner` (a string naming the sending peer). Returns the whole message once
   * its last missing chunk arrives, otherwise `undefined`. Inconsistent or oversized input is dropped.
   */
  add(owner: string, chunk: Chunk, now: number): Bytes | undefined {
    if (chunk.total > this.limits.maxMessageBytes) return undefined;
    if (chunk.count === 1) return chunk.data;
    const id = `${owner}/${chunk.messageId}`;
    let partial = this.pending.get(id);
    if (!partial) {
      if (chunk.total > this.limits.maxPendingBytes) return undefined;
      let mine = 0;
      for (const p of this.pending.values()) if (p.owner === owner) mine++;
      if (mine >= this.limits.maxPendingPerOwner) this.remove(this.stalest(owner)!);
      while (this.pendingBytes + chunk.total > this.limits.maxPendingBytes)
        this.remove(this.stalest()!);
      partial = { owner, first: chunk, parts: [], have: 0, bytes: 0, lastAt: now };
      this.pending.set(id, partial);
      this.pendingBytes += chunk.total;
    }
    const f = partial.first;
    const consistent =
      chunk.count === f.count &&
      chunk.total === f.total &&
      chunk.binary === f.binary &&
      chunk.key === f.key &&
      chunk.order === f.order;
    if (!consistent || partial.bytes + chunk.data.length > f.total) {
      this.remove(id);
      return undefined;
    }
    if (partial.parts[chunk.index]) return undefined;
    partial.parts[chunk.index] = chunk.data;
    partial.have++;
    partial.bytes += chunk.data.length;
    partial.lastAt = now;
    if (partial.have < f.count) return undefined;
    this.remove(id);
    return partial.bytes === f.total ? concat(...(partial.parts as Bytes[])) : undefined;
  }

  /** Drops this sender's unfinished messages with the given key that are older than `messageId`. */
  dropOlder(owner: string, key: string, messageId: number) {
    for (const [id, p] of this.pending)
      if (p.owner === owner && p.first.key === key && p.first.messageId < messageId)
        this.remove(id);
  }

  dropOwner(owner: string) {
    for (const [id, p] of this.pending) if (p.owner === owner) this.remove(id);
  }

  /** When the last chunk arrived for this sender's unfinished control message at `order`, if any. */
  progress(owner: string, order: number) {
    for (const p of this.pending.values())
      if (p.owner === owner && p.first.order === order) return p.lastAt;
    return undefined;
  }

  /** Drops unfinished messages that have stalled. */
  expire(now: number) {
    for (const [id, p] of this.pending) if (now - p.lastAt > this.limits.timeoutMs) this.remove(id);
  }
}
