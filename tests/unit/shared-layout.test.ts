import { expect, it } from 'vitest';
import { organization, parseOrganization } from '../../web/src/shared-layout';
import { blankWorkspace } from '../../web/src/workspace';
const host = 'a'.repeat(64),
  sound = 'b'.repeat(64);
it('shares organization while keeping cues, zoom, view and host credentials out', () => {
  const workspace = blankWorkspace(host);
  workspace.favorites = [sound, sound];
  workspace.zoom[sound] = 8;
  workspace.cues[sound] = [{ id: crypto.randomUUID(), name: 'Private cue', at: 100 }];
  const raw = JSON.stringify(organization(workspace));
  expect(raw).not.toMatch(/cues|zoom|hostId|secret|view/);
  expect(parseOrganization(raw, host).favorites).toEqual([sound]);
});
it('rejects cyclic trees, excessive data and unexpected shared fields', () => {
  const id = crypto.randomUUID(),
    w = blankWorkspace(host);
  w.folders = [{ id, name: 'Cycle', parent: id, soundIds: [] }];
  expect(() => parseOrganization(JSON.stringify(organization(w)), host)).toThrow();
  expect(() =>
    parseOrganization(
      JSON.stringify({ ...organization(blankWorkspace(host)), token: 'fake' }),
      host,
    ),
  ).toThrow();
  expect(() => parseOrganization(' '.repeat(2 * 1024 * 1024 + 1), host)).toThrow();
});
