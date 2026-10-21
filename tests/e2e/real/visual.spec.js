// Visual baselines against the real stack (§j): the Neotro table, hand, strip and reveal, and the retro board, light
// and dark, on a desktop and a 390×844 viewport. As with poker-table/visual.spec.js (docs/decisions/U31.md, UF14),
// pixels differ between engines and operating systems, so baselines are Chromium-only and carry the platform in
// their file name. People are seeded with fixed names; avatars (coloured by id) and room codes are masked.
import { expect, json, test } from './_real.js';
import { handCard, shownRound, table } from './_poker.js';
import { addCard, cardsIn, retroBoard } from './_retro.js';

const VIEWPORTS = { desktop: { width: 1280, height: 900 }, phone: { width: 390, height: 844 } };
const SHOT = { maxDiffPixels: 40 };

test.use({ timezoneId: 'UTC', locale: 'en-US', reducedMotion: 'reduce' });

test.beforeEach(({}, testInfo) => {
  test.skip(testInfo.project.name !== 'chromium', 'visual baselines are Chromium-only');
});

const masks = (page) => [page.locator('.avatar')];

async function settle(page) {
  await page.mouse.move(0, 0);
  await page.evaluate(() => document.fonts.ready);
}

for (const [size, viewport] of Object.entries(VIEWPORTS)) {
  for (const theme of ['light', 'dark']) {
    test(`poker table, hand, strip and reveal (${size} ${theme})`, async ({ open }) => {
      const { players } = await table(open, ['Ada Visual', 'Bo Visual', 'Cy Visual'], { ordered: true });
      const [ada, bo, cy] = players;
      await ada.page.request.patch('/api/me', json({ theme }));
      await ada.page.setViewportSize(viewport);
      for (const key of ['VIS-1', 'VIS-2', 'VIS-3']) {
        await ada.page.getByRole('button', { name: 'New round' }).click();
        await ada.page.getByRole('textbox', { name: 'Ticket key (optional)' }).fill(key);
        await ada.page.keyboard.press('Enter');
        await expect(shownRound(ada.page)).toContainText(key);
      }
      await ada.page.reload();
      await ada.page.locator('.nt-table').waitFor();
      await expect(ada.page.locator('html')).toHaveAttribute('data-theme', theme);

      await handCard(bo.page, 5).click();
      await bo.page.getByRole('button', { name: 'Lock In' }).click();
      await handCard(cy.page, 3).click();
      await expect(ada.page.locator('.nt-seat[data-status="locked"]')).toHaveCount(1);
      await expect(ada.page.locator('.nt-seat[data-status="selected"]')).toHaveCount(1);
      await handCard(ada.page, 8).click();
      await expect(handCard(ada.page, 8)).toHaveAttribute('aria-pressed', 'true');
      await settle(ada.page);
      await expect(ada.page.locator('.nt-table')).toHaveScreenshot(`table-${size}-${theme}.png`, { ...SHOT, mask: masks(ada.page) });
      await expect(ada.page.locator('.nt-hand')).toHaveScreenshot(`hand-${size}-${theme}.png`, { ...SHOT, mask: masks(ada.page) });
      await expect(ada.page.locator('.nt-strip')).toHaveScreenshot(`strip-${size}-${theme}.png`, { ...SHOT, mask: masks(ada.page) });

      await ada.page.getByRole('button', { name: 'Lock In' }).click();
      await ada.page.getByRole('button', { name: 'Reveal Cards' }).click();
      await expect(ada.page.locator('.nt-flip.is-flipped')).toHaveCount(2);
      await settle(ada.page);
      await expect(ada.page.locator('.nt-table')).toHaveScreenshot(`reveal-${size}-${theme}.png`, { ...SHOT, mask: masks(ada.page) });
    });

    test(`retro board (${size} ${theme})`, async ({ open }) => {
      const { players } = await retroBoard(open, ['Ada Board', 'Bo Board']);
      const [ada, bo] = players;
      await ada.page.request.patch('/api/me', json({ theme }));
      await ada.page.setViewportSize(viewport);
      await addCard(bo.page, 'Good', 'Shipped the release on time');
      await addCard(bo.page, 'Bad', 'Too many meetings');
      await addCard(bo.page, 'Action Items', 'Book fewer meetings');
      await ada.page.reload();
      await expect(ada.page.locator('html')).toHaveAttribute('data-theme', theme);
      await cardsIn(ada.page, 'Good').first().locator('.retro-vote').click();
      await expect(cardsIn(ada.page, 'Good').first().locator('.retro-vote')).toHaveAttribute('aria-pressed', 'true');
      await settle(ada.page);
      await expect(ada.page.locator('.retro-columns')).toHaveScreenshot(`retro-${size}-${theme}.png`, { ...SHOT, mask: masks(ada.page) });
    });
  }
}
