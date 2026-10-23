// UX13: with a deploy-time track the timer music plays from an <audio> element at the shared position;
// without one (or when the route fails) it falls back to the generated arpeggio. The track here is a
// silent WAV written at test time; no real audio is ever committed.
import { writeFile } from 'node:fs/promises';
import { expect, test } from '../_harness/fixtures.js';
import { ALICE, BOB, ROOM, fakeAudio, openAs, asPerson, personPage, servePeople, snapshot } from './_stages.js';

const TRACK_S = 20;
const DURATION_MS = 300_000;
const TOLERANCE_S = 0.35;

/** A mono 8-bit 8 kHz WAV of silence, `seconds` long. */
function silentWav(seconds) {
  const rate = 8000;
  const samples = rate * seconds;
  const wav = Buffer.alloc(44 + samples, 128);
  wav.write('RIFF', 0);
  wav.writeUInt32LE(36 + samples, 4);
  wav.write('WAVEfmt ', 8);
  wav.writeUInt32LE(16, 16);
  wav.writeUInt16LE(1, 20);
  wav.writeUInt16LE(1, 22);
  wav.writeUInt32LE(rate, 24);
  wav.writeUInt32LE(rate, 28);
  wav.writeUInt16LE(1, 32);
  wav.writeUInt16LE(8, 34);
  wav.write('data', 36);
  wav.writeUInt32LE(samples, 40);
  return wav;
}

// The recording AudioContext of the other timer specs, plus the element source the track needs. The
// element is kept on window so the spec can read where it is playing.
const mediaAudio = {
  fn: () => {
    window.AudioContext.prototype.createMediaElementSource = function createMediaElementSource(element) {
      window.__track = element;
      return { connect: (target) => target, disconnect() {} };
    };
  },
};

function running(fake, remainingMs, fields = {}) {
  return { state: 'running', durationMs: DURATION_MS, alarm: false, music: true, endsAt: fake.now() + remainingMs, ...fields };
}

async function unlock(page) {
  await page.keyboard.press('Shift');
}

/** How far the element is from the position the page's own countdown implies, the short way round. */
function drift(page) {
  return page.evaluate(([durationMs]) => {
    const track = window.__track;
    const deadline = Number(document.querySelector('.retro-timer-display')?.dataset.deadline);
    if (!track || !(track.duration > 0) || !deadline) {
      return null;
    }
    const target = ((durationMs - (deadline - Date.now())) / 1000) % track.duration;
    let d = track.currentTime - target;
    if (d > track.duration / 2) d -= track.duration;
    if (d < -track.duration / 2) d += track.duration;
    return d;
  }, [DURATION_MS]);
}

async function withTrack(fake, testInfo) {
  const file = testInfo.outputPath('track.wav');
  await writeFile(file, silentWav(TRACK_S));
  fake.musicFile = file;
}

test('two tabs play the track at the shared position, and pausing stops it', async ({ page, fake, browser }, testInfo) => {
  await withTrack(fake, testInfo);
  // 239 s left of 300 s: 61 s in, which is 1 s into the 20 s loop.
  servePeople(fake, { alice: ALICE, bob: BOB }, snapshot({ timer: running(fake, 239_000) }));
  await asPerson(page, fake, 'alice', { init: [fakeAudio, mediaAudio] });
  const bob = await personPage(browser, fake, 'bob', { init: [fakeAudio, mediaAudio] });
  await openAs(page);
  await openAs(bob);
  await unlock(page);
  await unlock(bob);

  for (const p of [page, bob]) {
    await expect(p.locator('.retro-timer')).toHaveAttribute('data-music-source', 'track');
    await expect.poll(() => p.evaluate(() => window.__track?.readyState ?? 0), { timeout: 10_000 }).toBeGreaterThanOrEqual(3);
    expect(await p.evaluate(() => Math.round(window.__track.duration))).toBe(TRACK_S);
    await expect.poll(async () => Math.abs((await drift(p)) ?? Infinity), { timeout: 10_000 }).toBeLessThan(TOLERANCE_S);
  }
  const before = await page.evaluate(() => window.__track.currentTime);
  await page.waitForTimeout(1000);
  expect(await page.evaluate(() => window.__track.currentTime)).toBeGreaterThan(before + 0.5);
  expect(Math.abs(await drift(page))).toBeLessThan(TOLERANCE_S);
  expect(Math.abs(await drift(bob))).toBeLessThan(TOLERANCE_S);
  expect(page.cspViolations).toEqual([]);
  expect(bob.cspViolations).toEqual([]);
  expect(await page.evaluate(() => window.__tones.filter((t) => t.type === 'triangle').length)).toBe(0);
  expect(fake.requests.filter((r) => r.path === '/media/timer-music').length).toBeGreaterThan(0);

  fake.tx(ROOM, [{ k: 'retro.timer', d: { state: 'paused', durationMs: DURATION_MS, alarm: false, music: true, remainingMs: 200_000 } }]);
  for (const p of [page, bob]) {
    await expect(p.locator('.retro-timer')).not.toHaveAttribute('data-music-source');
    await expect.poll(() => p.evaluate(() => window.__track.paused)).toBe(true);
  }

  // Resuming at 100 s in (0 s into the loop) seeks the element there.
  fake.tx(ROOM, [{ k: 'retro.timer', d: running(fake, 200_000) }]);
  await expect.poll(async () => Math.abs((await drift(page)) ?? Infinity), { timeout: 10_000 }).toBeLessThan(TOLERANCE_S);
  await bob.context().close();
});

test('the generated music plays when the boot names a track the server cannot serve', async ({ page, fake }) => {
  servePeople(fake, { alice: ALICE }, snapshot({ timer: running(fake, 239_000) }));
  const boot = fake.boot;
  fake.boot = (path, req) => ({ ...boot(path, req), timerMusic: true });
  await asPerson(page, fake, 'alice', { init: [fakeAudio, mediaAudio] });
  await openAs(page);
  await unlock(page);

  await expect(page.locator('.retro-timer')).toHaveAttribute('data-music-source', 'generated', { timeout: 10_000 });
  await expect.poll(() => page.evaluate(() => window.__tones.filter((t) => t.type === 'triangle').length)).toBeGreaterThan(0);
  expect(fake.requests.some((r) => r.path === '/media/timer-music')).toBe(true);
});

test('without a track the timer never asks for one', async ({ page, fake }) => {
  servePeople(fake, { alice: ALICE }, snapshot({ timer: running(fake, 239_000) }));
  await asPerson(page, fake, 'alice', { init: [fakeAudio, mediaAudio] });
  await openAs(page);
  await unlock(page);

  await expect(page.locator('.retro-timer')).toHaveAttribute('data-music-source', 'generated');
  await page.waitForTimeout(500);
  expect(fake.requests.some((r) => r.path === '/media/timer-music')).toBe(false);
  expect(await page.evaluate(() => window.__track)).toBeUndefined();
});
