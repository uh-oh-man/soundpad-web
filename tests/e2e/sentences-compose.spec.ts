/**
 * Sentences, 2.45 (WP3): the palette's Add to sentence at the caret (nothing plays), reusable named
 * phrase blocks said in their voice, a template's voice slot, and the host seeing and clearing a
 * guest's unkept sentences. Mock Soundpad and the transcript fixture only. The harness may still
 * report 2.44, so the host page is told its companion is 2.45 (the version the gate names).
 */
import type { Page } from '@playwright/test';
import { test, expect, host, joinGuest, sendCommand, useLocalGuests } from './helpers/harness';
import { closeSection, section, setMimicTranscription } from './helpers/shell';

async function companionVersion(page: Page, version: string) {
  await page.routeWebSocket(/ws:\/\/127\.0\.0\.1:\d+\/bridge/, (socket) => {
    const server = socket.connectToServer();
    socket.onMessage((message) => server.send(message));
    server.onMessage((message) => {
      const frame = JSON.parse(String(message));
      if (frame.state?.companionVersion) frame.state.companionVersion = version;
      socket.send(JSON.stringify(frame));
    });
  });
}

/** Kept recordings whose words (from the transcript fixture) are "Fixture tone N"; their ids. */
async function recordingsWithWords(page: Page, count: number) {
  await page.getByRole('button', { name: 'Mimic', exact: true }).click();
  const dialog = page.getByRole('main', { name: 'Mimic', exact: true });
  await dialog.getByRole('button', { name: 'Start listening' }).click();
  await expect(dialog.getByRole('status').first()).toContainText('Listening to Fixture');
  await setMimicTranscription(dialog);
  await expect(
    dialog
      .getByRole('list', { name: 'Transcript' })
      .getByRole('button', { name: 'Fixture', exact: true })
      .first(),
  ).toBeVisible({ timeout: 15000 });
  await closeSection(page, 'Mimic');
  const ids: string[] = [];
  for (let i = 1; i <= count; i++) {
    const draft = (await sendCommand(page, 'draftCreate', {
      action: 'recent',
      name: `Take ${i}`,
    })) as { data?: { id: string }; error?: string };
    expect(draft.error).toBeUndefined();
    const kept = (await sendCommand(page, 'draftKeep', {
      draftId: draft.data!.id,
      action: 'library',
    })) as { data?: { recordingId: string }; error?: string };
    expect(kept.error).toBeUndefined();
    ids.push(kept.data!.recordingId);
  }
  await expect
    .poll(
      async () =>
        (
          (await sendCommand(page, 'librarySearch', { query: 'fixture tone' })) as {
            data?: { total: number };
          }
        ).data?.total ?? 0,
      { timeout: 30000 },
    )
    .toBeGreaterThanOrEqual(count);
  return ids;
}
const plays = (list: string[]) => list.filter((x) => /DoPlaySound/.test(x));

test('Add to sentence puts the palette word at the caret without playing it; a phrase block and a voice slot are said by their voice', async ({
  page,
  harness,
}) => {
  await host(page, harness);
  const [first] = await recordingsWithWords(page, 2);
  const voice = (await sendCommand(page, 'libraryVoice', { action: 'create', name: 'Ann' })) as {
    data?: { voice: { id: string } };
  };
  const ann = voice.data!.voice.id;
  expect(
    (await sendCommand(page, 'libraryEdit', { recordingId: first, voiceId: ann })).error,
  ).toBeUndefined();
  await page.getByRole('button', { name: 'Sentences', exact: true }).click();
  const sentences = section(page, 'Sentences');
  await sentences.getByLabel('New sentence').fill('tone');
  await sentences.getByRole('button', { name: 'New sentence' }).click();
  const builder = sentences.getByRole('region', { name: /^Sentence / });
  const text = builder.getByLabel('Sentence text');
  await expect(text).toHaveValue('tone');
  // The caret goes before "tone".
  await text.click();
  await text.press('ControlOrMeta+Home');
  await builder.getByRole('button', { name: 'Browse recorded words' }).click();
  await expect(
    sentences.getByRole('status').filter({ hasText: 'Add to sentence goes' }),
  ).toHaveText(/into “tone”, at the caret you left in its text/);
  const before = harness.plays.length;
  await sentences.getByLabel('Search the palette').fill('fixture');
  await sentences
    .getByRole('list', { name: 'Palette' })
    .getByRole('button', { name: /^Add “Fixture” to sentence$/ })
    .first()
    .click();
  await expect(page.getByText(/^Added “Fixture” to “Fixture tone”\.$/)).toBeVisible();
  // Nothing played: no Soundpad play and no private preview.
  expect(plays(harness.plays.slice(before))).toEqual([]);
  await sentences.getByRole('button', { name: 'Open it' }).click();
  await expect(text).toHaveValue('Fixture tone');
  const timeline = builder.getByRole('group', { name: 'Timeline' });
  await timeline.getByRole('button', { name: /^“Fixture”/ }).click();
  await expect(
    builder.getByRole('region', { name: 'Piece “Fixture”' }).getByText('Your choice'),
  ).toBeVisible();

  // A phrase block: made, renamed, added (after the last add, in Ann's voice), then removed.
  await sentences.getByRole('tab', { name: 'Sets' }).click();
  const blocks = sentences.getByRole('region', { name: 'Phrase blocks' });
  await blocks.getByLabel('New phrase block name').fill('Greeting');
  await blocks.getByLabel('New phrase block words').fill('fixture tone');
  await blocks.getByRole('form', { name: 'New phrase block' }).getByLabel('Said by').selectOption({
    label: 'Said by: Ann',
  });
  await blocks.getByRole('button', { name: 'Add phrase block' }).click();
  const list = blocks.getByRole('list', { name: 'Phrase blocks list' });
  await expect(list.getByText('Greeting', { exact: true })).toBeVisible();
  await expect(list.getByText('“fixture tone” · Ann')).toBeVisible();
  await list.getByRole('button', { name: 'Edit' }).click();
  await list.getByLabel('Phrase block name').fill('Hello');
  await list.getByRole('button', { name: 'Save' }).click();
  await expect(list.getByText('Hello', { exact: true })).toBeVisible();
  await expect(list.getByText('Greeting', { exact: true })).toBeHidden();
  const beforeBlock = harness.plays.length;
  await list.getByRole('button', { name: 'Add to sentence' }).click();
  await expect(page.getByText(/^Added “Hello” to /)).toBeVisible();
  expect(plays(harness.plays.slice(beforeBlock))).toEqual([]);
  // A template with a voice slot, filled with Ann: its words are hers.
  await sentences.getByLabel('Template name').fill('Duet');
  await sentences.getByLabel('Template text').fill('{who:voice} fixture tone');
  await sentences.getByRole('button', { name: 'Add template' }).click();
  const templates = sentences.getByRole('list', { name: 'Templates' });
  await templates.getByLabel('Duet: voice for who').selectOption({ label: 'who: Ann' });
  await templates.getByRole('button', { name: 'Use template' }).click();
  await expect(text).toHaveValue('fixture tone');
  await timeline.getByRole('button').first().click();
  await expect(builder.getByText(/^Said by Ann only/)).toBeVisible();
  // The block's words in the first sentence are Ann's too.
  await builder.getByRole('button', { name: 'Back to sentences' }).click();
  await sentences
    .getByRole('list', { name: 'Sentences in progress' })
    .getByRole('button', { name: /^Fixture/ })
    .click();
  await expect(text).toHaveValue('Fixture fixture tone tone');
  await timeline.getByRole('button', { name: /^“fixture tone”/ }).click();
  await expect(builder.getByText(/^Said by Ann only/)).toBeVisible();
  // Removing the block leaves the sentence as it is.
  await sentences.getByRole('tab', { name: 'Sets' }).click();
  await list.getByRole('button', { name: 'Remove phrase block Hello' }).click();
  await expect(list.getByRole('listitem')).toHaveCount(0);
});

test("the host sees a guest's unkept sentence, opens it read-only and clears it", async ({
  page,
  harness,
}) => {
  await useLocalGuests(page);
  await host(page, harness);
  const guest = await joinGuest(page, 'Fixture Guest');
  await guest.getByRole('button', { name: 'Sentences', exact: true }).click();
  const theirs = section(guest, 'Sentences');
  await theirs.getByLabel('New sentence').fill('hello there');
  await theirs.getByRole('button', { name: 'New sentence' }).click();
  await expect(theirs.getByRole('region', { name: /^Sentence / })).toBeVisible();
  await theirs.getByRole('button', { name: 'Back to sentences' }).click();
  await expect(
    theirs.getByRole('list', { name: 'Sentences in progress' }).getByText('hello there').first(),
  ).toBeVisible();

  await page.getByRole('button', { name: 'Sentences', exact: true }).click();
  const mine = section(page, 'Sentences');
  const guests = mine.getByRole('region', { name: "Guests' sentences in progress" });
  await expect(guests.getByText('Fixture Guest', { exact: true })).toBeVisible();
  await expect(guests.getByText(/^1 unkept/)).toBeVisible();
  // The host's own list does not mix them in.
  await expect(mine.getByRole('list', { name: 'Sentences in progress', exact: true })).toHaveCount(
    0,
  );
  await guests.getByRole('button', { name: 'Show' }).click();
  await guests.getByRole('button', { name: /^hello there/ }).click();
  const builder = mine.getByRole('region', { name: /^Sentence / });
  await expect(builder.getByLabel('Sentence text')).toHaveValue('hello there');
  await expect(builder.getByLabel('Sentence text')).toHaveAttribute('readonly', '');
  await expect(
    builder.getByRole('button', { name: "Discard Fixture Guest's sentence" }),
  ).toBeVisible();
  await builder.getByRole('button', { name: 'Back to sentences' }).click();
  // Clearing asks once more.
  await guests.getByRole('button', { name: "Clear Fixture Guest's" }).click();
  await expect(
    guests.getByText(/Clear 1 unkept sentence of Fixture Guest\? This cannot be undone\./),
  ).toBeVisible();
  await guests.getByRole('button', { name: 'Clear', exact: true }).click();
  await expect(page.getByText('Cleared 1 unkept sentence of Fixture Guest.')).toBeVisible();
  await expect(guests.getByText('No guest has an unkept sentence.')).toBeVisible();
  // The guest's list follows.
  await expect(theirs.getByText(/^None\. Unfinished sentences stay here/)).toBeVisible();
});

test('a 2.44 companion shows none of it', async ({ page, harness }) => {
  await companionVersion(page, '2.44.0');
  await host(page, harness);
  await page.getByRole('button', { name: 'Sentences', exact: true }).click();
  const sentences = section(page, 'Sentences');
  await expect(sentences.getByRole('heading', { name: 'Kept sentences' })).toBeVisible();
  await expect(
    sentences.getByRole('region', { name: "Guests' sentences in progress" }),
  ).toHaveCount(0);
  await sentences.getByRole('tab', { name: 'Palette' }).click();
  await expect(sentences.getByText(/Add to sentence/)).toHaveCount(0);
  await sentences.getByRole('tab', { name: 'Sets' }).click();
  await expect(sentences.getByRole('region', { name: 'Phrase blocks' })).toHaveCount(0);
});
