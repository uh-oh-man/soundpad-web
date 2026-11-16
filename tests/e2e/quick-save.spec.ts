/**
 * One-press Save (2.46, from the owner's 2.45 feedback: "cant even save voices" in Voices, and "when saving a
 * voice line or word, you should be able to instantly save it, and have it in the libary. no second prompt").
 * Until 2.46 Voices had no way to save anything, and a clip in Just mimicked or Temp went into the library only
 * through Keep, which left Mimic for the Soundboard and asked for a folder and a category first. Now Save, on a
 * selection, a Just mimicked clip, a Temp clip or in Voices itself, puts it in the voice library at once (kept
 * where Keep puts files), under its words or its own name; the notice says where it went, with Undo and Rename.
 * The harness starts with no voice library and no Keep folder, as on the owner's PC.
 */
import type { Page } from '@playwright/test';
import {
  test,
  expect,
  host,
  joinGuest,
  sendCommand,
  useLocalGuests,
  TEMP_READY,
} from './helpers/harness';
import { closeSection, section, setMimicTranscription } from './helpers/shell';

const notices = (page: Page) => page.locator('.notification-stack');
const saved = (page: Page) =>
  notices(page)
    .getByRole('status')
    .filter({ hasText: /^Saved “.+” to the voice library and Soundpad’s “Saved - Soundpad Web/ });

async function startListening(page: Page) {
  await page.getByRole('button', { name: 'Mimic', exact: true }).click();
  const dialog = page.getByRole('main', { name: 'Mimic', exact: true });
  await dialog.getByRole('button', { name: 'Start listening' }).click();
  await expect(dialog.getByRole('status').first()).toContainText('Listening to Fixture');
  return dialog;
}
async function recordings(page: Page) {
  const reply = (await sendCommand(page, 'libraryList', {})) as {
    data: { items: { id: string; title: string; byName: string }[] };
  };
  return reply.data.items;
}

test('one-press Save: a selection, a Just mimicked clip and a clip in Voices go into the library with no prompt; Undo and Rename', async ({
  page,
  harness,
}) => {
  await host(page, harness);
  const dialog = await startListening(page);
  await setMimicTranscription(dialog);
  await expect(
    dialog
      .getByRole('list', { name: 'Transcript' })
      .getByRole('button', { name: 'Fixture', exact: true })
      .first(),
  ).toBeVisible({ timeout: 15000 });

  // 1. A selection (a word of the transcript): Save names it from its words and keeps it at once. No draft
  // stays open, and nothing asks for a name, a folder or a category.
  await dialog
    .getByRole('list', { name: 'Transcript' })
    .getByRole('button', { name: 'Fixture', exact: true })
    .first()
    .click();
  await dialog.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(saved(page).first()).toBeVisible({ timeout: TEMP_READY });
  await expect(saved(page).first()).toContainText(/^Saved “Fixture/);
  await expect(dialog.getByRole('region', { name: 'Draft' })).toHaveCount(0);
  await expect(page.getByRole('region', { name: 'Confirm permanent audio save' })).toHaveCount(0);
  await expect.poll(async () => (await recordings(page)).length).toBe(1);

  // 2. A Just mimicked clip: Save there, and Mimic stays open (Keep used to leave it for Temp's form).
  await dialog.getByRole('button', { name: 'Last 5 s', exact: true }).click();
  await dialog.getByRole('button', { name: /Mimic & play/ }).click();
  const recent = dialog.getByRole('region', { name: 'Just mimicked' });
  // The clip Save made in step 1 is listed too; the new one comes first once it is there.
  const saveButtons = recent.getByRole('button', { name: /^Save .+ to the voice library$/ });
  await expect(saveButtons).toHaveCount(2, { timeout: TEMP_READY });
  const saveRecent = saveButtons.first();
  await saveRecent.click();
  await expect(saveRecent).toContainText('Saved', { timeout: TEMP_READY });
  await expect.poll(async () => (await recordings(page)).length).toBe(2);
  await expect(dialog).toBeVisible();
  await expect(page.getByRole('region', { name: 'Confirm permanent audio save' })).toHaveCount(0);
  // Undo takes it out of the library again; the kept sound stays in Soundpad.
  await notices(page)
    .getByRole('status')
    .filter({ hasText: /^Saved/ })
    .last()
    .getByRole('button', { name: 'Undo' })
    .click();
  await expect(notices(page)).toContainText('Taken out of the voice library.');
  await expect.poll(async () => (await recordings(page)).length).toBe(1);

  // 3. A clip left in Temp: Voices lists it with Save.
  await page.waitForTimeout(1200); // another stretch of the history
  await dialog.getByRole('button', { name: 'Last 3 s', exact: true }).click();
  await dialog.getByRole('button', { name: /Mimic & play/ }).click();
  await closeSection(page, 'Mimic');
  await page.getByRole('button', { name: 'Voices', exact: true }).click();
  const voices = section(page, 'Voices');
  const unsaved = voices.getByRole('region', { name: 'In Temp, not saved yet' });
  // Only that one: the saved clips are kept for good, not in Temp any more.
  await expect(unsaved.getByRole('listitem')).toHaveCount(1, { timeout: TEMP_READY });
  const first = unsaved.getByRole('listitem').first();
  const name = (await first.locator('.voices-title').innerText()).trim();
  await first.getByRole('button', { name: `Save ${name} to the voice library` }).click();
  const notice = saved(page).filter({ hasText: `Saved “${name}”` });
  await expect(notice).toBeVisible({ timeout: TEMP_READY });
  // The host is told the folder too: Keep's default, as nothing was chosen.
  await expect(notice).toContainText('Its file is in');
  await expect(voices.getByRole('list', { name: 'Recordings' })).toContainText(name);
  await expect(
    unsaved.getByRole('button', { name: `Save ${name} to the voice library` }),
  ).toHaveCount(0);
  // Rename opens it with its title ready to type.
  await notice.getByRole('button', { name: 'Rename' }).click();
  const title = voices.getByLabel('Title');
  await expect(title).toBeFocused();
  await title.fill('Best line');
  await voices.getByRole('button', { name: 'Rename', exact: true }).click();
  await expect
    .poll(async () => (await recordings(page)).map((r) => r.title))
    .toContain('Best line');
});

test('one-press Save: a guest saves a Temp clip from Voices into the host’s library, where the host chose, and is never told the folder', async ({
  page,
  harness,
}) => {
  await useLocalGuests(page);
  await host(page, harness);
  await page.getByRole('button', { name: 'Access settings' }).click();
  await page.getByLabel('Allow guest Mimic').check();
  await page.keyboard.press('Escape');
  const dialog = await startListening(page);
  await page.waitForTimeout(3500);
  await dialog.getByRole('button', { name: 'Last 3 s', exact: true }).press('Enter');
  await dialog.getByRole('button', { name: 'Capture', exact: true }).press('Enter');
  const draft = dialog.getByRole('region', { name: 'Draft' });
  await draft.getByRole('button', { name: 'Keep in Temp', exact: true }).press('Enter');
  await expect(
    dialog.getByRole('region', { name: 'Just mimicked' }).getByRole('button', { name: /^Save / }),
  ).toBeVisible({ timeout: TEMP_READY });
  await closeSection(page, 'Mimic');
  const guest = await joinGuest(page);
  await guest.getByRole('button', { name: 'Voices', exact: true }).click();
  const voices = section(guest, 'Voices');
  const unsaved = voices.getByRole('region', { name: 'In Temp, not saved yet' });
  await expect(unsaved.getByRole('listitem')).toHaveCount(1, { timeout: 15000 });
  await unsaved.getByRole('button', { name: /^Save .+ to the voice library$/ }).click();
  const notice = saved(guest);
  await expect(notice).toBeVisible({ timeout: TEMP_READY });
  await expect(notice).not.toContainText('Its file is in');
  await expect
    .poll(async () => (await recordings(page)).map((r) => r.byName))
    .toEqual(['Fixture Guest']);
});
