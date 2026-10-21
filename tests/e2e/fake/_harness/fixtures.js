// Shared fixtures for every spec under tests/e2e/fake/: import { test, expect } from here instead of
// @playwright/test. Each test gets its own fake server (`fake`) on a free port, used as baseURL, and
// fails if the page reports a CSP violation (DESIGN §j) or the client sends a frame the protocol
// refuses. A spec that sends invalid frames on purpose sets test.use({ allowProtocolViolations: true }).
import { test as base, expect } from '@playwright/test';
import { startFakeServer } from '../../../web-fake-server/server.js';

// The checks every test ends with; teeth.spec.js runs them on purpose-made violations.
export const expectNoScriptErrors = (fake) => expect(fake.scriptErrors, 'errors thrown by fake server scripts').toEqual([]);
export const expectNoProtocolViolations = (fake) =>
  expect(fake.violations.map((v) => v.errors), 'client frames refused by the protocol').toEqual([]);
export const expectNoCspViolations = (page) => expect(page.cspViolations, 'CSP violations').toEqual([]);

export const test = base.extend({
  allowProtocolViolations: [false, { option: true }],

  fake: async ({ allowProtocolViolations }, use) => {
    const fake = await startFakeServer();
    fake.scriptErrors = [];
    fake.on('error', (error) => fake.scriptErrors.push(error));
    await use(fake);
    await fake.close();
    expectNoScriptErrors(fake);
    if (!allowProtocolViolations) {
      expectNoProtocolViolations(fake);
    }
  },

  baseURL: async ({ fake }, use) => {
    await use(fake.url);
  },

  page: async ({ page }, use) => {
    page.cspViolations = [];
    await page.exposeFunction('__rsCspViolation', (v) => page.cspViolations.push(v));
    await page.addInitScript(() => {
      document.addEventListener('securitypolicyviolation', (e) => {
        window.__rsCspViolation({ directive: e.effectiveDirective, blocked: e.blockedURI, sample: e.sample });
      });
    });
    await use(page);
    expectNoCspViolations(page);
  },
});

export { expect };

// Runs in the page: opens a raw WebSocket to the fake and collects frames, for specs that exercise
// the protocol before (or without) web/src/net. Returns a handle stored on window[name].
export async function openSocket(page, name = 'rs') {
  await page.evaluate((key) => {
    const ws = new WebSocket(`${location.origin.replace(/^http/, 'ws')}/ws`);
    const state = { ws, frames: [], close: null };
    ws.addEventListener('message', (e) => state.frames.push(JSON.parse(e.data)));
    ws.addEventListener('close', (e) => {
      state.close = { code: e.code, reason: e.reason };
    });
    window[key] = state;
    return new Promise((done, fail) => {
      ws.addEventListener('open', () => done());
      ws.addEventListener('error', () => fail(new Error('WebSocket failed to open')));
    });
  }, name);
  return {
    send: (frame) => page.evaluate(([key, f]) => window[key].ws.send(JSON.stringify(f)), [name, frame]),
    frames: () => page.evaluate((key) => window[key].frames, name),
    close: () => page.evaluate((key) => window[key].close, name),
    waitForFrames: (count) => page.waitForFunction(([key, n]) => window[key].frames.length >= n, [name, count]),
    waitForClose: () => page.waitForFunction((key) => window[key].close !== null, name),
  };
}
