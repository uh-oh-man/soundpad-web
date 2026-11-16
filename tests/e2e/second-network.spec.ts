import { constants, createPublicKey, publicEncrypt } from 'node:crypto';
import { mkdir, rename, writeFile } from 'node:fs/promises';
import { expect, host, invite, test } from './helpers/harness';

const key = process.env.SOUNDPAD_PROBE_PUBLIC_KEY;
const peer = process.env.SOUNDPAD_PROBE_PEER;

test.skip(!key || !peer, 'Requires an independently launched guest and its public key.');

test('a guest on a second network plays a sound on the mock Windows host', async ({
  page,
  harness,
}) => {
  test.setTimeout(7 * 60 * 1000);
  await host(page, harness);
  const invitation = new URL(await invite(page));
  const sealed = publicEncrypt(
    {
      key: createPublicKey({ key: Buffer.from(key!, 'base64'), format: 'der', type: 'spki' }),
      padding: constants.RSA_PKCS1_OAEP_PADDING,
      oaepHash: 'sha256',
    },
    Buffer.from(invitation.hash),
  );
  const dir = 'artifacts/second-network';
  await mkdir(dir, { recursive: true });
  await writeFile(dir + '/invite.tmp', sealed.toString('base64'));
  await rename(dir + '/invite.tmp', dir + '/invite.sealed');
  await expect(page.locator('.board-heading')).toContainText(peer!, { timeout: 300000 });
  await expect.poll(() => harness.plays, { timeout: 60000 }).toContain('DoPlaySound(1)');
  await writeFile(dir + '/result.json', JSON.stringify({ peer, command: 'DoPlaySound(1)' }));
});
