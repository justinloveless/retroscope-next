import { readFileSync, readdirSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { expect, test } from '../_harness/fixtures.js';
import { apply, snapshot, sortItems, toWire } from '../../../../web/src/retro/model.js';

const TRANSCRIPTS = fileURLToPath(new URL('../../../golden/transcripts/', import.meta.url));
const SORT_GOLDEN = fileURLToPath(new URL('../../../golden/retro-sort.json', import.meta.url));
const files = readdirSync(TRANSCRIPTS).filter((f) => f.startsWith('retro-') && f.endsWith('.json'));
const load = (file) => JSON.parse(readFileSync(TRANSCRIPTS + file, 'utf8'));

// An idle timer's endsAt is a one-shot expiry signal, not state (U8 decision, golden transcripts).
function canonicalTimer(timer) {
  return timer.state === 'idle' ? { state: 'idle', durationMs: timer.durationMs, alarm: timer.alarm, music: timer.music } : timer;
}

function fold(frames) {
  let state = null;
  for (const frame of frames) {
    if (frame.t === 'snap') {
      state = snapshot(frame.d, frame.o);
    } else if (frame.t === 'tx') {
      state = frame.ev.reduce(apply, state);
    }
  }
  return state;
}

test.describe('model.js against the C# goldens', () => {
  test.skip(() => test.info().project.name !== 'chromium', 'pure Node checks; once is enough');

  test('there are retro transcripts to replay', () => {
    expect(files.length).toBeGreaterThanOrEqual(6);
  });

  for (const file of files) {
    test(`folding ${file} reaches the fresh snapshot`, () => {
      const transcript = load(file);
      const expected = structuredClone(transcript.expectedViewerState);
      expected.d.timer = canonicalTimer(expected.d.timer);
      expect(toWire(fold(transcript.frames))).toEqual(expected);
    });
  }

  test('every retro-sort.json case sorts like the original', () => {
    const { cases } = JSON.parse(readFileSync(SORT_GOLDEN, 'utf8'));
    expect(cases).toHaveLength(2000);
    for (const [index, c] of cases.entries()) {
      const items = c.items.map((item) => ({ ...item, createdAt: Date.parse(item.created_at) }));
      for (const [key, want] of Object.entries(c.expected)) {
        expect(sortItems(items, key).map((item) => item.id), `case ${index} ${key}`).toEqual(want);
      }
    }
  });

  test('a vote without voters clears them and added events are upserts', () => {
    const transcript = load('retro-stages-bob.json');
    let state = fold(transcript.frames);
    const [cardId] = state.cards.keys();
    state = apply(state, { k: 'retro.vote', d: { cardId, count: 2, voters: ['a', 'b'] } });
    state = apply(state, { k: 'retro.vote', d: { cardId, count: 2 } });
    expect(state.votes.get(cardId)).toEqual({ cardId, count: 2 });
    const card = state.cards.get(cardId);
    state = apply(state, { k: 'retro.card.added', d: { card: { ...card, body: 'again' } } });
    expect([...state.cards.values()].filter((c) => c.id === cardId)).toHaveLength(1);
    expect(state.cards.get(cardId).body).toBe('again');
  });
});

test('a golden transcript replayed to the page shows its final board', async ({ page, fake }) => {
  const file = 'retro-quick-board-alice.json';
  const transcript = load(file);
  const { d, o } = transcript.expectedViewerState;
  const me = d.presence.find((p) => p.pid === transcript.viewer.pid);
  fake.me = me;
  fake.boot = { build: 'fake', theme: 'light', me, room: { r: transcript.room } };
  fake.replayTranscript(TRANSCRIPTS + file);
  await page.goto(`/retro/${d.code}`);

  await expect(page.locator('.retro-title')).toHaveText(d.meta.title);
  await expect(page.locator('.retro-column-title')).toHaveText(d.columns.map((c) => c.title));
  for (const column of d.columns) {
    const bodies = page.locator(`.retro-column[data-id="${column.id}"] .retro-card-body`);
    const expected = d.cards.filter((c) => c.columnId === column.id);
    await expect(bodies).toHaveCount(expected.length);
  }
  for (const id of o.mineCards) {
    await expect(page.locator(`.retro-card[data-id="${id}"]`)).toHaveClass(/retro-card-mine/);
  }
  for (const vote of d.votes) {
    await expect(page.locator(`.retro-card[data-id="${vote.cardId}"] .retro-vote-count`)).toHaveText(String(vote.count));
  }
});
