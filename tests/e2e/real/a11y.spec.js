// An axe scan of the core screens against the real stack (§j), in light and dark. axe-core is evaluated through the
// automation protocol, which the page's CSP does not govern, so the scan adds no script the app would refuse.
// Every violation fails the test; the findings of the first scan were fixed by UF16 (docs/decisions/UF16.md).
import { randomUUID } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { createBoard, createPoker, createTeam, expect, json, seedUsers, test } from './_real.js';

const AXE = readFileSync(createRequire(import.meta.url).resolve('axe-core/axe.min.js'), 'utf8');

/**
 * Violations as `rule selector` strings; generated tab ids are normalised to `#tabs-*-tab-…`. A colour caught halfway
 * through a CSS transition (a theme switch, a button that just became the selected one) is not what anyone reads, so
 * the scan waits for running transitions first.
 */
async function scan(page) {
  await page.evaluate(async () => {
    await new Promise((resolve) => requestAnimationFrame(() => requestAnimationFrame(resolve)));
    const transitions = document.getAnimations().filter((a) => a instanceof CSSTransition);
    await Promise.all(transitions.map((a) => a.finished.catch(() => {})));
  });
  await page.evaluate(AXE);
  const result = await page.evaluate(() => window.axe.run(document, {
    runOnly: { type: 'tag', values: ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa'] },
    resultTypes: ['violations'],
  }));
  return result.violations.flatMap((v) => v.nodes.map((n) => `${v.id} ${n.target.join(' ').replace(/^#tabs-[a-z0-9]+-tab-/, '#tabs-*-tab-')}`));
}

for (const theme of ['light', 'dark']) {
  test(`core screens have no axe violations (${theme})`, async ({ open }) => {
    test.setTimeout(90_000);
    const anonymous = await open();
    for (const path of ['/', '/login', '/login?tab=signup']) {
      await anonymous.page.goto(`${path}`);
      await anonymous.page.evaluate((t) => document.documentElement.setAttribute('data-theme', t), theme);
      expect.soft(await scan(anonymous.page), path).toEqual([]);
    }

    const [user, mate] = await seedUsers(['Ann Axe', 'Bob Axe']);
    const { page } = await open(user);
    const other = await open(mate);
    await page.request.patch('/api/me', json({ theme }));
    const team = await createTeam(page.request, [other.page.request]);
    const poker = await createPoker(page.request, team.id);
    const board = await createBoard(page.request, team.id);
    const screens = [
      ['/teams', 'h1'],
      [`/teams/${team.id}`, 'h1'],
      [`/teams/${team.id}?tab=retros`, 'h1'],
      [`/teams/${team.id}/poker/${poker.code}`, '.nt-table'],
      [`/retro/${board.code}`, '.retro-column-list'],
      ['/account', 'h1'],
      // "You can't open this page", for a team that does not exist and one the user is not in alike.
      [`/teams/${randomUUID()}`, 'h1'],
    ];
    for (const [path, ready] of screens) {
      await page.goto(path);
      await page.locator(ready).first().waitFor();
      await expect(page.locator('html')).toHaveAttribute('data-theme', theme);
      expect.soft(await scan(page), path).toEqual([]);
    }
  });
}
