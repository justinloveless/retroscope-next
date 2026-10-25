import { networkSwitch } from '../../network-switch.js';
import { openBoard } from '../retro-board/_board.js';
import { expect, openSocket, test } from './fixtures.js';

// The real-stack specs take a context off the network with e2e/network-switch.js. A routed socket has two ends: the
// page's, which the app sees, and a real connection to the server. If the switch leaves the server's end open, the
// server keeps the tab's cid bound to it and refuses every reconnect with 4409 for as long as the test runs.

const RAW_CID = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb';

test('offline closes the server end of a socket that said hello, so the tab is welcomed back under its cid', async ({ page, fake }) => {
  const network = await networkSwitch(page.context());
  await openBoard(page, fake);
  const [before] = fake.liveConnections();
  expect(before.cid).not.toBeNull();
  const refused = [];
  fake.on('hello', (conn) => {
    if (conn.me === null) {
      refused.push(conn.cid);
    }
  });

  await network.offline();
  await expect.poll(() => before.open).toBe(false);
  await network.online();

  // The tab reconnects on its backoff, which grew while it was offline.
  await expect.poll(() => fake.liveConnections().filter((c) => c.cid === before.cid && c.me !== null).length, { timeout: 15_000 })
    .toBe(1);
  expect(refused).toEqual([]);
});

test('a socket the page closes is closed at the server too', async ({ page, fake }) => {
  await networkSwitch(page.context());
  await openBoard(page, fake);
  const socket = await openSocket(page, 'raw');
  await socket.send({ t: 'hello', v: 1, cid: RAW_CID, subs: [] });
  await socket.waitForFrames(1);
  const raw = fake.liveConnections().find((c) => c.cid === RAW_CID);

  await page.evaluate(() => window.raw.ws.close(1000));
  await expect.poll(() => raw.open).toBe(false);
});
