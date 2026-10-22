// An axe scan of the core screens against the real stack (§j), in light and dark. axe-core is evaluated through the
// automation protocol, which the page's CSP does not govern, so the scan adds no script the app would refuse.
// The first scan found the findings in KNOWN, all in code other units own; they are follow-ups in
// docs/decisions/U45.md. Anything else fails, and so does a known finding on a selector it has not been seen on.
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { createBoard, createPoker, createTeam, expect, json, seedUsers, test } from './_real.js';

const AXE = readFileSync(createRequire(import.meta.url).resolve('axe-core/axe.min.js'), 'utf8');

const KNOWN = [
  ['color-contrast', /^\.btn-green$/],
  ['color-contrast', /^\.btn-emerald > \.btn-label$/],
  ['color-contrast', /^(div:nth-child\(\d\) > )?\.btn-primary(\[type="submit"\]|\.btn-sm > \.btn-label)$/],
  ['color-contrast', /^#tabs-\*-tab-(signin|signup|poker|members|actions|kudos)$/],
  ['color-contrast', /^\.nt-chip-key$/],
  ['label-content-name-mismatch', /^\.app-team-switch$/],
  ['label-content-name-mismatch', /^\.retro-kudos-button$/],
  ['aria-prohibited-attr', /^\.retro-presence$/],
];

/** Violations not in KNOWN, as `rule selector` strings; generated tab ids are normalised to `#tabs-*-tab-…`. */
async function scan(page) {
  await page.evaluate(AXE);
  const result = await page.evaluate(() => window.axe.run(document, {
    runOnly: { type: 'tag', values: ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa'] },
    resultTypes: ['violations'],
  }));
  return result.violations.flatMap((v) => v.nodes.map((n) => [v.id, n.target.join(' ').replace(/^#tabs-[a-z0-9]+-tab-/, '#tabs-*-tab-')]))
    .filter(([id, target]) => !KNOWN.some(([known, pattern]) => known === id && pattern.test(target)))
    .map(([id, target]) => `${id} ${target}`);
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
      ['/dashboard', 'h1'],
      [`/teams/${team.id}`, 'h1'],
      [`/teams/${team.id}/poker/${poker.code}`, '.nt-table'],
      [`/retro/${board.code}`, '.retro-column-list'],
      ['/account', 'h1'],
    ];
    for (const [path, ready] of screens) {
      await page.goto(path);
      await page.locator(ready).first().waitFor();
      await expect(page.locator('html')).toHaveAttribute('data-theme', theme);
      expect.soft(await scan(page), path).toEqual([]);
    }
  });
}
