import { describe, it, expect } from 'vitest';
// @ts-expect-error -- plain ESM script without type declarations
import { releaseNotes } from '../../scripts/release-notes.mjs';

const changelog = `# Changelog

Intro.

## 2.15.9 — Reverse works again

**Update the Windows companion.**

- Fixed things.

## 2.15.8 — Older

- Old.
`;

describe('release notes', () => {
  it('titles and heads the release with its version and changelog title', () => {
    const { title, notes } = releaseNotes(changelog, 'v2.15.9');
    expect(title).toBe('Soundpad Web v2.15.9 — Reverse works again');
    expect(notes.startsWith('# Soundpad Web v2.15.9 — Reverse works again\n')).toBe(true);
    expect(notes).toContain('- Fixed things.');
    expect(notes).not.toContain('Older');
    expect(notes).toContain('version 2.15.9');
  });
  it('takes the last section up to the end of the file', () => {
    expect(releaseNotes(changelog, '2.15.8').notes).toContain('- Old.');
  });
  it('refuses a version with no changelog section', () => {
    expect(() => releaseNotes(changelog, 'v9.9.9')).toThrow(/no "## 9.9.9" section/);
  });
});
