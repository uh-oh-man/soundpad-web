import { DefaultArtifactClient } from '@actions/artifact';
import { spawn } from 'node:child_process';
import { access } from 'node:fs/promises';
import { execPath } from 'node:process';
import { setTimeout as delay } from 'node:timers/promises';

const dir = 'artifacts/second-network';
const invitation = dir + '/invite.sealed';
const test = spawn(
  execPath,
  [
    'node_modules/@playwright/test/cli.js',
    'test',
    'tests/e2e/second-network.spec.ts',
    '--retries=0',
  ],
  { stdio: 'inherit', windowsHide: true },
);
const result = new Promise((resolve) => {
  test.once('exit', (code) => resolve(code ?? 1));
  test.once('error', () => resolve(1));
});

try {
  const deadline = Date.now() + 90_000;
  while (test.exitCode === null && Date.now() < deadline) {
    if (
      await access(invitation).then(
        () => true,
        () => false,
      )
    )
      break;
    await delay(500);
  }
  await access(invitation);
  // An Actions step ends its children. Publish within the same step that owns the host.
  await new DefaultArtifactClient().uploadArtifact('second-network-invitation', [invitation], dir, {
    retentionDays: 1,
  });
  if ((await result) !== 0) throw new Error('The guest playback check failed.');
  await access(dir + '/result.json');
} catch (error) {
  test.kill();
  throw error;
}
