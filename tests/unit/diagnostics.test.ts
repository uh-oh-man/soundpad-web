// 2.37: what a guest is told when it cannot connect, from made-up candidate lists and relay states.
import { describe, expect, it } from 'vitest';
import {
  candidateSummary,
  diagnose,
  HOST_NOT_FOUND_MS,
  judgeIce,
  NO_WEBRTC,
  parseCandidate,
  type Candidate,
  type DiagnosisInput,
} from '../../web/src/network/diagnostics';

const host = (port: number): Candidate => ({
  type: 'host',
  protocol: 'udp',
  address: 'abc.local',
  port,
});
const srflx = (port: number, base = 0): Candidate => ({
  type: 'srflx',
  protocol: 'udp',
  address: '203.0.113.7',
  port,
  relatedAddress: '0.0.0.0',
  relatedPort: base,
});
const unreachable = (url: string) => ({ errorCode: 701, url });
const base: DiagnosisInput = {
  webrtc: true,
  relayState: 'ready',
  relays: [{ state: 'ready' }, { state: 'ready' }, { state: 'failed' }],
  relaysChecked: true,
  elapsedMs: 5000,
  answered: false,
};

describe('candidate lines', () => {
  it('reads the type, address, port and base of a candidate', () => {
    expect(
      parseCandidate(
        'candidate:842163049 1 udp 1677729535 203.0.113.7 61001 typ srflx raddr 0.0.0.0 rport 0 generation 0',
      ),
    ).toEqual(srflx(61001));
    expect(parseCandidate('a=candidate:1 1 tcp 1 abc.local 9 typ host tcptype active')).toEqual({
      ...host(9),
      protocol: 'tcp',
    });
    expect(parseCandidate('not a candidate')).toBeUndefined();
    expect(candidateSummary([host(1), srflx(2), srflx(3)])).toBe('host 1, srflx 2');
    expect(candidateSummary([])).toBe('none');
  });
});

describe('network verdict from gathered candidates', () => {
  it('sees an ordinary network', () => {
    expect(judgeIce({ candidates: [host(5000), srflx(61001)], errors: [] })).toBe('ok');
  });
  it('sees UDP blocked: no public address and every STUN server unreachable', () => {
    expect(
      judgeIce({
        candidates: [host(5000)],
        errors: [
          unreachable('stun:stun.l.google.com:19302'),
          unreachable('stun:stun.cloudflare.com:3478'),
        ],
      }),
    ).toBe('udp-blocked');
    // No candidates at all: WebRTC is blocked by an extension or a VPN add-on.
    expect(judgeIce({ candidates: [], errors: [] })).toBe('udp-blocked');
    expect(judgeIce({ candidates: [host(5000)], errors: [] })).toBe('unknown');
  });
  it('sees symmetric NAT: one base, a different public port per STUN server', () => {
    expect(judgeIce({ candidates: [host(5000), srflx(61001), srflx(61007)], errors: [] })).toBe(
      'symmetric-nat',
    );
    // Two local sockets that each keep one public port are fine.
    expect(judgeIce({ candidates: [srflx(61001, 5000), srflx(61007, 5002)], errors: [] })).toBe(
      'ok',
    );
  });
});

describe('the cause a guest is shown', () => {
  it('names a missing WebRTC first, and adds when the relay cannot help either', () => {
    expect(diagnose({ ...base, webrtc: false })?.text).toBe(NO_WEBRTC);
    const down = diagnose({
      ...base,
      webrtc: false,
      relayState: 'failed',
      relays: [{ state: 'closed' }, { state: 'failed' }],
    });
    expect(down?.cause).toBe('no-webrtc');
    expect(down?.text).toContain('0 of 2 public relays answered');
  });
  it('says no relay is reachable when none passed its check', () => {
    const d = diagnose({ ...base, relayState: 'failed', relays: [{ state: 'closed' }] });
    expect(d?.cause).toBe('no-relays');
    expect(d?.title).toBe('No relay reachable');
    expect(d?.text).toMatch(/^No relay reachable\. 0 of 1 public relay answered/);
    // Not before the relays were checked.
    expect(diagnose({ ...base, relaysChecked: false, relays: [{ state: 'connecting' }] })).toBe(
      undefined,
    );
  });
  it('says the host was not found once the relays work and nobody answered for a while', () => {
    expect(diagnose(base)).toBeUndefined(); // too early to say
    const d = diagnose({ ...base, elapsedMs: HOST_NOT_FOUND_MS });
    expect(d?.cause).toBe('host-not-found');
    expect(d?.text).toContain('2 of 3 public relays answered');
    // A host that answered (the lobby, say) is found.
    expect(diagnose({ ...base, elapsedMs: HOST_NOT_FOUND_MS, answered: true })).toBeUndefined();
  });
  it('names a blocked or strict network while the relay has not got through either', () => {
    const late = { ...base, elapsedMs: HOST_NOT_FOUND_MS, relayState: 'connecting' as const };
    expect(diagnose({ ...late, ice: 'udp-blocked' })?.cause).toBe('udp-blocked');
    expect(diagnose({ ...late, ice: 'symmetric-nat' })?.title).toBe('Strict network');
    expect(diagnose({ ...late, ice: 'ok' })).toBeUndefined();
  });
});
