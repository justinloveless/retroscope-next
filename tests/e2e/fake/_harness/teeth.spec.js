import { randomUUID } from 'node:crypto';
import { expect, expectNoCspViolations, expectNoProtocolViolations, expectNoScriptErrors, openSocket, test } from './fixtures.js';

// These prove the shared fixtures catch what every fake spec relies on them to catch: each one
// commits a specific violation, checks that it was recorded as that violation, checks that the
// fixture's end-of-test check refuses it, and then clears it so the test itself passes.

test('a CSP violation is recorded and fails the test', async ({ page }) => {
  await page.goto('/');
  await page.evaluate(() => document.body.setAttribute('style', 'color: red'));
  await expect.poll(() => page.cspViolations.length).toBe(1);
  expect(page.cspViolations[0]).toMatchObject({ directive: 'style-src-attr' });
  expect(() => expectNoCspViolations(page)).toThrow(/CSP violations/);
  page.cspViolations.length = 0;
});

test('a frame the protocol refuses is recorded and fails the test', async ({ page, fake }) => {
  await page.goto('/');
  const socket = await openSocket(page);
  await socket.send({ t: 'hello', v: 1, cid: 'not-a-uuid', subs: [] });
  await socket.waitForClose();
  expect((await socket.close()).code).toBe(4400);
  expect(fake.violations).toHaveLength(1);
  expect(fake.violations[0].frame).toMatchObject({ t: 'hello', cid: 'not-a-uuid' });
  expect(fake.violations[0].errors).toEqual([expect.stringMatching(/\bcid\b/)]);
  expect(() => expectNoProtocolViolations(fake)).toThrow(/client frames refused by the protocol/);
  fake.violations.length = 0;
});

test('an exception in a fake script is recorded and fails the test', async ({ page, fake }) => {
  const room = `poker:${randomUUID()}`;
  fake.room(room, { snapshot: {} });
  fake.onCommand('poker.vote.set', () => {
    throw new Error('script exploded');
  });
  await page.goto('/');
  const socket = await openSocket(page);
  await socket.send({ t: 'hello', v: 1, cid: randomUUID(), subs: [{ r: room }] });
  await socket.waitForFrames(2);
  await socket.send({ t: 'cmd', r: room, q: 1, k: 'poker.vote.set', d: { roundId: randomUUID(), points: 5 } });
  await expect.poll(() => fake.scriptErrors.length).toBe(1);
  expect(fake.scriptErrors[0].message).toBe('script exploded');
  expect(() => expectNoScriptErrors(fake)).toThrow(/errors thrown by fake server scripts/);
  fake.scriptErrors.length = 0;
});
