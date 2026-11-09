// Builds a GitHub release's title and notes from CHANGELOG.md, so every release update shows its version number
// and what changed (GitHub's generated notes were only a commit list). Used by the release job in
// .github/workflows/build-windows.yml:
//   node scripts/release-notes.mjs v2.15.9 notes.md   -> writes notes.md, prints the title
import { readFileSync, writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

/** The CHANGELOG section for a version ("2.15.9" or "v2.15.9"), as { title, notes }. */
export function releaseNotes(changelog, tag) {
  const version = tag.replace(/^v/, '');
  const lines = changelog.split(/\r?\n/);
  const start = lines.findIndex((l) => l.startsWith(`## ${version} `) || l === `## ${version}`);
  if (start < 0) throw new Error(`CHANGELOG.md has no "## ${version}" section; add one before tagging.`);
  let end = lines.findIndex((l, i) => i > start && l.startsWith('## '));
  if (end < 0) end = lines.length;
  const summary = lines[start].replace(/^## \S+\s*(—\s*)?/, '').trim();
  const title = `Soundpad Web v${version}${summary ? ` — ${summary}` : ''}`;
  const body = lines
    .slice(start + 1, end)
    .join('\n')
    .trim();
  const notes = [
    `# Soundpad Web v${version}${summary ? ` — ${summary}` : ''}`,
    '',
    body,
    '',
    '---',
    '',
    `Download **SoundpadWeb.exe** below (Windows companion, version ${version}); the website updates by itself.`,
    'Every older version stays on the Releases page as a backup; see docs/ROLLBACK.md.',
    '',
  ].join('\n');
  return { title, notes };
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const [tag, output] = process.argv.slice(2);
  if (!tag || !output) throw new Error('Usage: node scripts/release-notes.mjs <tag> <notes-file>');
  const { title, notes } = releaseNotes(readFileSync(new URL('../CHANGELOG.md', import.meta.url), 'utf8'), tag);
  writeFileSync(output, notes);
  console.log(title);
}
