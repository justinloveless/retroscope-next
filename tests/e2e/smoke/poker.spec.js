// Two teammates at one table over real WebSockets through the proxy: votes stay hidden until the reveal. Ann owns the
// team (seeded verified); Bob signs up through the route and joins it through a link invite.
import { clientIp, createPoker, createTeam, expect, seedOwner, signUp, test } from './_stack.js';

test('two teammates vote, lock in and both see the same reveal', async ({ browser, baseURL, ignoreHTTPSErrors }) => {
  const contexts = await Promise.all([1, 2].map(() => browser.newContext({
    baseURL,
    ignoreHTTPSErrors,
    extraHTTPHeaders: { 'X-Test-Client-Ip': clientIp() },
  })));
  const [ann, bob] = await Promise.all(contexts.map((context) => context.newPage()));
  await seedOwner(ann.request, 'Ann Smoke');
  await signUp(bob.request, 'Bob Smoke');
  const team = await createTeam(ann.request, [bob.request]);
  const { code } = await createPoker(ann.request, team.id);

  for (const page of [ann, bob]) {
    await page.goto(`/teams/${team.id}/poker/${code}`);
    await page.locator('.nt-table').waitFor();
  }
  // Each player sees the other's seat; their own vote is in their hand.
  await expect(ann.locator('.nt-seat')).toHaveCount(1);

  await ann.locator('.nt-hand-card[data-points="5"]').click();
  await ann.getByRole('button', { name: 'Lock In' }).click();
  await expect(bob.locator('.nt-seat[data-status="locked"]')).toHaveCount(1);
  await expect(bob.locator('.nt-seat .nt-card[data-card="5"]')).toHaveCount(0);

  await bob.locator('.nt-hand-card[data-points="8"]').click();
  await bob.getByRole('button', { name: 'Lock In' }).click();
  await expect(ann.locator('.nt-seat[data-status="locked"]')).toHaveCount(1);
  // A fresh room holds auto-reveal for its 15 s warm-up (§c.7), so the host reveals by hand (R).
  await ann.keyboard.press('r');

  for (const page of [ann, bob]) {
    await expect(page.locator('.nt-stack-pill')).toHaveText(['1 x 5 pts', '1 x 8 pts']);
  }
  await Promise.all(contexts.map((context) => context.close()));
});
