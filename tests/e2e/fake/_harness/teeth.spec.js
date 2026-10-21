import { expect, openSocket, test } from './fixtures.js';

// These must fail: they prove the shared fixtures catch what every fake spec relies on them to catch.

test('a CSP violation fails the test', async ({ page }) => {
  test.fail();
  await page.goto('/');
  await page.evaluate(() => document.body.setAttribute('style', 'color: red'));
  await page.waitForTimeout(200);
});

test('a frame the protocol refuses fails the test', async ({ page, fake }) => {
  test.fail();
  await page.goto('/');
  const socket = await openSocket(page);
  await socket.send({ t: 'hello', v: 1, cid: 'not-a-uuid', subs: [] });
  await socket.waitForClose();
  expect((await socket.close()).code).toBe(4400);
  expect(fake.violations).toHaveLength(1);
});
