// The felt table (docs/followups/UX3.md): one printed betting line per occupied side and none on an empty
// side, a printed outline on every seat (dimmed for an observer), and WCAG AA contrast for the seat labels on
// the felt and for the issue's text on the frosted pane, in both themes. Contrast is measured against the
// rendered pixels: the text is made transparent, the area behind it captured and its least favourable pixel
// compared with the text colour.
import { expect, test } from '../_harness/fixtures.js';
import { BOB, DOT, GUS, ME, openTable, setupPoker, snapshot } from './_poker.js';

const desktopOnly = (testInfo) => test.skip(testInfo.project.name === 'mobile-webkit', 'the ring is a desktop layout');

function luminance([r, g, b]) {
  const lin = (c) => {
    const v = c / 255;
    return v <= 0.03928 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4;
  };
  return 0.2126 * lin(r) + 0.7152 * lin(g) + 0.0722 * lin(b);
}

function ratio(a, b) {
  const [hi, lo] = [luminance(a), luminance(b)].sort((x, y) => y - x);
  return (hi + 0.05) / (lo + 0.05);
}

/** The pixels behind `locator`'s text, as [r, g, b] triples, with the text itself hidden. */
async function backgroundPixels(page, locator) {
  await locator.evaluate((el) => {
    el.dataset.contrastProbe = el.style.color;
    el.style.color = 'transparent';
    el.style.textShadow = 'none';
  });
  const png = await page.screenshot({ clip: await locator.boundingBox(), animations: 'disabled' });
  await locator.evaluate((el) => {
    el.style.color = el.dataset.contrastProbe;
    el.style.textShadow = '';
  });
  return page.evaluate(async (data) => {
    const image = new Image();
    image.src = `data:image/png;base64,${data}`;
    await image.decode();
    const canvas = document.createElement('canvas');
    canvas.width = image.width;
    canvas.height = image.height;
    const ctx = canvas.getContext('2d');
    ctx.drawImage(image, 0, 0);
    const px = ctx.getImageData(0, 0, image.width, image.height).data;
    const out = [];
    for (let i = 0; i < px.length; i += 4) {
      out.push([px[i], px[i + 1], px[i + 2]]);
    }
    return out;
  }, png.toString('base64'));
}

async function textColour(locator) {
  return locator.evaluate((el) => getComputedStyle(el).color.match(/\d+(\.\d+)?/g).slice(0, 3).map(Number));
}

async function worstContrast(page, locator) {
  const text = await textColour(locator);
  const pixels = await backgroundPixels(page, locator);
  return Math.min(...pixels.map((p) => ratio(text, p)));
}

for (const theme of ['light', 'dark']) {
  test.describe(theme, () => {
    test.use({ viewport: { width: 1440, height: 900 } });

    test('each occupied side gets one printed betting line, an empty side none', async ({ page, fake }, testInfo) => {
      desktopOnly(testInfo);
      setupPoker(fake, { theme, snap: snapshot({ roster: [ME, BOB], presence: [ME, BOB] }) });
      await openTable(page);
      await expect(page.locator('.nt-seats.is-ring > .nt-seat')).toHaveCount(1);
      await expect(page.locator('.nt-print-left')).toHaveAttribute('d', /^M .* A /);
      await expect(page.locator('.nt-print-right')).toHaveAttribute('hidden', '');
      await expect(page.locator('.nt-print path:not([hidden])')).toHaveCount(1);
    });

    test('every seat has a printed outline, an observer a dimmed one with no card', async ({ page, fake }, testInfo) => {
      desktopOnly(testInfo);
      setupPoker(fake, { theme, snap: snapshot({ roster: [ME, BOB, GUS, DOT], presence: [ME, BOB, GUS, DOT], observers: [DOT.pid] }) });
      await openTable(page);
      const outline = (pid) => page.locator(`.nt-seat[data-pid="${pid}"] .nt-seat-slot`).evaluate((slot) => {
        const style = getComputedStyle(slot, '::before');
        return { width: parseFloat(style.borderTopWidth), opacity: Number(style.opacity) };
      });
      const player = await outline(BOB.pid);
      expect(player.width).toBeGreaterThanOrEqual(4);
      expect(player.width).toBeLessThanOrEqual(6.5);
      const observer = await outline(DOT.pid);
      expect(observer.opacity).toBeLessThan(player.opacity);
      await expect(page.locator(`.nt-seat[data-pid="${DOT.pid}"] .nt-card`)).toHaveCount(0);
    });

    test('seat labels meet AA contrast on the felt', async ({ page, fake }, testInfo) => {
      desktopOnly(testInfo);
      setupPoker(fake, { theme });
      await openTable(page);
      const names = page.locator('.nt-seats.is-ring .nt-seat-name');
      await expect(names.first()).toBeVisible();
      for (const name of await names.all()) {
        expect(await worstContrast(page, name)).toBeGreaterThanOrEqual(4.5);
      }
    });

    test('the issue text meets AA contrast on the frosted pane', async ({ page, fake }, testInfo) => {
      desktopOnly(testInfo);
      setupPoker(fake, { theme });
      await openTable(page);
      for (const selector of ['.nt-issue-kicker', '.nt-issue-text']) {
        const text = page.locator(selector).first();
        await expect(text).toBeVisible();
        expect(await worstContrast(page, text)).toBeGreaterThanOrEqual(4.5);
      }
    });
  });
}
