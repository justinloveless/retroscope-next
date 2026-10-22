// The players seated around the issue (docs/followups/UX1.md): only on the left and right arcs, each turned
// toward the stage centre, a card sliding in on a lock and flipping on the reveal, no vote value in the
// page before the reveal, a "+N" chip for a crowded table, one scrolling row on a phone, and the hand
// peeking from the bottom until it is pointed at or focused.
import { expect, test } from '../_harness/fixtures.js';
import { BOB, ME, R1, ROOM, handCard, lastCommand, openTable, revealed, seat, setupPoker, snapshot } from './_poker.js';

const person = (n, name) => ({ pid: `0a000000-0000-4000-8000-0000000001${String(n).padStart(2, '0')}`, kind: 'user', name });
const CROWD = ['Ana', 'Ben', 'Cleo', 'Dev', 'Eli', 'Fay', 'Gil', 'Hal', 'Ivy', 'Jo', 'Kai'].map((name, i) => person(i + 1, name));

/** A table of `count` players: me and `count - 1` others, all present. */
function table(count) {
  const others = CROWD.slice(0, count - 1);
  return snapshot({ roster: [ME, ...others], presence: [ME, ...others] });
}

/** Each seat's centre (relative to the stage), side and turn, read from the computed layout. */
async function seatGeometry(page) {
  return page.locator('.nt-table').evaluate((stage) => {
    const s = stage.getBoundingClientRect();
    const header = stage.querySelector('.nt-ticket').getBoundingClientRect();
    return {
      width: s.width,
      height: s.height,
      header: header.bottom - s.top,
      dock: stage.querySelector('.nt-dock').getBoundingClientRect().top - s.top,
      seats: [...stage.querySelectorAll('.nt-seat:not([hidden])')].map((seat) => {
        const r = seat.getBoundingClientRect();
        return {
          x: r.left + r.width / 2 - s.left,
          y: r.top + r.height / 2 - s.top,
          side: seat.dataset.side,
          angle: parseFloat(getComputedStyle(seat.querySelector('.nt-seat-body')).rotate),
        };
      }),
    };
  });
}

const desktopOnly = (testInfo) => test.skip(testInfo.project.name === 'mobile-webkit', 'the ring is a desktop layout');

for (const viewport of [{ width: 1440, height: 900 }, { width: 1920, height: 1080 }, { width: 1280, height: 800 }]) {
  test.describe(`${viewport.width}×${viewport.height}`, () => {
    test.use({ viewport });

    test('seats sit only on the left and right arcs, each turned toward the stage centre', async ({ page, fake }, testInfo) => {
      desktopOnly(testInfo);
      setupPoker(fake, { snap: table(9) });
      await openTable(page);
      await expect(page.locator('.nt-seats.is-ring > .nt-seat')).toHaveCount(8);
      // Seats glide to their places; read them once they stand still.
      let geometry;
      await expect.poll(async () => {
        const before = JSON.stringify(await seatGeometry(page));
        await page.waitForTimeout(150);
        geometry = await seatGeometry(page);
        return JSON.stringify(geometry) === before;
      }).toBe(true);

      const { width, height, header, dock, seats } = geometry;
      expect(seats.filter((s) => s.side === 'left')).toHaveLength(4);
      expect(seats.filter((s) => s.side === 'right')).toHaveLength(4);
      for (const s of seats) {
        expect(s.side).toBe(s.x < width / 2 ? 'left' : 'right');
        expect(s.x < width / 3 || s.x > (2 * width) / 3).toBe(true);
        expect(s.y).toBeGreaterThan(header);
        expect(s.y).toBeLessThan(dock);
        const facing = [Math.sin((s.angle * Math.PI) / 180), -Math.cos((s.angle * Math.PI) / 180)];
        const toward = [width / 2 - s.x, height / 2 - s.y];
        const off = Math.abs(Math.atan2(facing[0] * toward[1] - facing[1] * toward[0], facing[0] * toward[0] + facing[1] * toward[1]));
        expect(off * (180 / Math.PI)).toBeLessThan(1.5);
      }
      // The issue keeps clear of the ring: its side padding is the ring's depth.
      const padding = await page.locator('.nt-issue').evaluate((el) => parseFloat(getComputedStyle(el).paddingLeft));
      for (const s of seats.filter((x) => x.side === 'left')) {
        expect(s.x).toBeLessThan(padding);
      }
    });
  });
}

test('a peer’s lock slides a face-down card into their seat along its axis', async ({ page, fake }, testInfo) => {
  desktopOnly(testInfo);
  setupPoker(fake);
  await openTable(page);
  fake.tx(ROOM, [{ k: 'poker.vote.state', d: { roundId: R1, pid: BOB.pid, status: 'locked' } }]);
  const card = seat(page, BOB).locator('.nt-seat-body .nt-seat-slot .nt-card[data-card="back"]');
  await expect(card).toHaveClass(/nt-slide-in/);
  await expect(card).toHaveCSS('animation-name', 'nt-card-slide-in');
  // The card rides inside the rotated seat body, so the slide runs along the seat's own axis.
  await expect.poll(async () => Math.abs(parseFloat(await seat(page, BOB).locator('.nt-seat-body').evaluate((el) => getComputedStyle(el).rotate)))).toBeGreaterThan(30);
  await expect(page.locator('[aria-live="polite"]').filter({ hasText: 'Bob locked in' })).toHaveCount(1);
});

test('the reveal flips each seat to its vote; before it no vote value is in the page', async ({ page, fake }) => {
  const others = Object.fromEntries(CROWD.slice(0, 4).map((p, i) => [p.pid, { points: [3, 5, 8, 13][i], locked: true, abstained: false }]));
  const votes = setupPoker(fake, { snap: table(5), others });
  await openTable(page);
  fake.tx(ROOM, CROWD.slice(0, 4).map((p) => ({ k: 'poker.vote.state', d: { roundId: R1, pid: p.pid, status: 'locked' } })));
  await expect(page.locator('.nt-seat[data-status="locked"]')).toHaveCount(4);

  const leaks = () => page.evaluate(() => {
    const seats = document.querySelector('.nt-seats');
    return {
      faces: [...seats.querySelectorAll('[data-card]')].map((c) => c.dataset.card).filter((c) => c !== 'back'),
      text: seats.textContent.match(/\d/g) ?? [],
      labels: [...seats.querySelectorAll('[aria-label]')].map((s) => s.getAttribute('aria-label')).filter((l) => /\d|voted/.test(l)),
      results: document.querySelectorAll('.nt-stack').length,
    };
  });
  expect(await leaks()).toEqual({ faces: [], text: [], labels: [], results: 0 });

  votes.set(ME.pid, { points: 5, locked: true, abstained: false });
  fake.tx(ROOM, [revealed(R1, votes, BOB.pid, 5)]);
  await expect(page.locator('.nt-seat .nt-flip.is-flipped')).toHaveCount(4);
  await expect(seat(page, CROWD[3]).locator('.nt-flip-front .nt-card')).toHaveAttribute('data-card', '13');
  await expect(seat(page, CROWD[3])).toHaveAttribute('aria-label', 'Dev, voted 13');
});

test('a crowded table shrinks its seats and puts the rest behind a "+N" chip', async ({ page, fake }, testInfo) => {
  desktopOnly(testInfo);
  await page.setViewportSize({ width: 1024, height: 640 });
  setupPoker(fake, { snap: table(12) });
  await openTable(page);
  const chip = page.locator('.nt-more-button');
  await expect(chip).toBeVisible();
  await expect(page.locator('.nt-seats')).toHaveAttribute('data-size', 'avatar');
  const shown = await page.locator('.nt-seat:not([hidden])').count();
  expect(shown).toBeGreaterThan(2);
  await expect(chip).toHaveText(`+${11 - shown}`);

  await chip.click();
  const list = page.getByRole('dialog', { name: 'More players' });
  await expect(list.locator('.nt-more-item')).toHaveCount(11 - shown);
  await expect(list.locator('.nt-more-item').last()).toContainText('Kai');
});

test.describe('on a phone', () => {
  test.use({ viewport: { width: 390, height: 844 } });

  test('the seats are one scrolling row under the round header, and a lock slides into it', async ({ page, fake }) => {
    setupPoker(fake, { snap: table(10) });
    await openTable(page);
    const row = page.locator('.nt-seats.is-row');
    await expect(row.locator('.nt-seat')).toHaveCount(9);
    const tops = await row.locator('.nt-seat').evaluateAll((seats) => seats.map((s) => Math.round(s.getBoundingClientRect().top)));
    expect(new Set(tops).size).toBe(1);
    const header = await page.locator('.nt-ticket').boundingBox();
    expect(tops[0]).toBeGreaterThanOrEqual(header.y + header.height);
    expect(await row.evaluate((el) => el.scrollWidth > el.clientWidth)).toBe(true);

    fake.tx(ROOM, [{ k: 'poker.vote.state', d: { roundId: R1, pid: CROWD[0].pid, status: 'locked' } }]);
    await expect(seat(page, CROWD[0]).locator('.nt-card.nt-slide-in')).toBeVisible();
  });
});

test('the hand peeks from the bottom and rises when focused; arrows move between cards', async ({ page, fake }) => {
  setupPoker(fake);
  await openTable(page);
  await page.mouse.move(0, 0);
  const card = handCard(page, 5);
  // How much of the card shows: the button is the visible strip, the face inside it the whole card.
  const showing = () => card.evaluate((button) => button.clientHeight / button.querySelector('.nt-card').offsetHeight);
  await expect.poll(showing).toBeLessThan(0.4);
  expect(await showing()).toBeGreaterThan(0.3);

  await card.focus();
  await expect.poll(showing).toBe(1);
  await page.keyboard.press('ArrowRight');
  await expect(handCard(page, 8)).toBeFocused();
  await expect(page).not.toHaveURL(/round=2/);
  await page.keyboard.press('Enter');
  expect((await lastCommand(fake, 'poker.vote.set')).d).toEqual({ roundId: R1, points: 8 });
  await page.keyboard.press('l');
  expect((await lastCommand(fake, 'poker.lock.set')).d).toEqual({ roundId: R1, locked: true });
  await expect(handCard(page, 8).locator('.nt-hand-locked')).toBeVisible();
  // My locked pick stays raised a little more than the rest of the resting fan.
  await page.locator('.nt-issue').focus();
  await expect.poll(async () => (await handCard(page, 8).boundingBox()).y < (await handCard(page, 5).boundingBox()).y - 8).toBe(true);
});

test('a point description shows as a tooltip on the focused card', async ({ page, fake }) => {
  setupPoker(fake, { snap: snapshot({ pointDescriptions: { 8: 'About a week' } }) });
  await openTable(page);
  await expect(page.locator('.nt-legend')).toHaveCount(0);
  await handCard(page, 8).focus();
  await expect(page.getByRole('tooltip')).toHaveText('8: About a week');
});
