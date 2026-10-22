// Visual baselines of the Neotro table, hand and reveal (§j) in both themes, on a desktop and a 390×844
// phone viewport. Pixels differ between engines and operating systems, so they are kept for Chromium only;
// the baselines carry the platform in their file name (see docs/decisions/U31.md). The time zone and locale
// are pinned for every project in playwright.config.js, so a machine's own settings never change a baseline.
import { expect, test } from '../_harness/fixtures.js';
import { BOB, DOT, GUS, ME, R1, ROOM, handCard, openTable, revealed, setupPoker } from './_poker.js';

const VIEWPORTS = { desktop: { width: 1280, height: 900 }, phone: { width: 390, height: 844 } };
// In the Linux image a seat's name label was seen once to land one pixel over (28 pixels in all); a real
// change to the table, a card or a glyph moves far more.
const SHOT = { fullPage: true, maxDiffPixels: 40 };

const OTHERS = {
  [BOB.pid]: { points: 5, locked: true, abstained: false },
  [GUS.pid]: { points: 3, high: 5, locked: true, abstained: false },
  [DOT.pid]: { points: 13, locked: false, abstained: false },
};

test.beforeEach(({}, testInfo) => {
  test.skip(testInfo.project.name !== 'chromium', 'visual baselines are Chromium-only');
});

for (const [size, viewport] of Object.entries(VIEWPORTS)) {
  for (const theme of ['light', 'dark']) {
    test.describe(`${size} ${theme}`, () => {
      test.use({ viewport });

      test('voting: seats, a selected card and the hand', async ({ page, fake }) => {
        setupPoker(fake, { theme, others: OTHERS });
        await openTable(page);
        fake.tx(ROOM, [
          { k: 'poker.vote.state', d: { roundId: R1, pid: BOB.pid, status: 'locked' } },
          { k: 'poker.vote.state', d: { roundId: R1, pid: GUS.pid, status: 'selected' } },
        ]);
        await expect(page.locator(`.nt-seat[data-pid="${BOB.pid}"]`)).toHaveAttribute('data-status', 'locked');
        await handCard(page, 8).click();
        await expect(handCard(page, 8)).toHaveAttribute('aria-pressed', 'true');
        await page.mouse.move(0, 0);
        await page.evaluate(() => document.fonts.ready);
        await expect(page).toHaveScreenshot(`voting-${size}-${theme}.png`, SHOT);
      });

      test('revealed: grouped stacks and the results bar', async ({ page, fake }) => {
        const votes = setupPoker(fake, { theme, others: OTHERS });
        await openTable(page);
        votes.set(ME.pid, { points: 5, locked: true, abstained: false });
        fake.tx(ROOM, [revealed(R1, votes, BOB.pid, 5)]);
        // Dot never locked in, so the reveal turns that vote into a hidden abstain.
        await expect(page.locator('.nt-flip.is-flipped')).toHaveCount(3);
        await page.mouse.move(0, 0);
        await page.evaluate(() => document.fonts.ready);
        await expect(page).toHaveScreenshot(`revealed-${size}-${theme}.png`, SHOT);
      });
    });
  }
}
