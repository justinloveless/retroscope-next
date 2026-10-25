// Takes a browser context off the network and back, for specs against either target: the real-stack specs use it
// through real/_real.js, and the fake harness can hold it to what it promises against tests/web-fake-server.

// How the switch closes the server's end of a routed socket (see networkSwitch).
const CLOSED = { code: 1000 };

/**
 * Takes a context off the network and back. `context.setOffline` cuts HTTP everywhere, but Firefox and WebKit keep an
 * open WebSocket alive through it, so the room socket runs through a route that this switch closes, and that refuses
 * new sockets while offline. Install it before the page opens its socket (reload a page that already has one).
 *
 * A routed socket is two sockets: the one the app holds and a real connection to the server, and whichever closes
 * takes the other with it. The server's end must really close, or the server keeps the tab's cid bound to it and
 * answers every reconnect with 4409 for as long as the test runs. A page may close a WebSocket only with 1000 or
 * 3000–4999: the browser throws on any other code (1001 included) and Playwright swallows that, so the server's end
 * is always closed with 1000, whatever code the app is shown.
 */
export async function networkSwitch(context) {
  let offline = false;
  const open = new Set();
  await context.routeWebSocket(/\/ws$/, (page) => {
    if (offline) {
      page.close({ code: 1001, reason: 'offline' });
      return;
    }
    const server = page.connectToServer();
    const pair = { page, server };
    open.add(pair);
    page.onClose(() => {
      open.delete(pair);
      server.close(CLOSED);
    });
  });
  return {
    async offline() {
      offline = true;
      await context.setOffline(true);
      for (const { page, server } of open) {
        await server.close(CLOSED);
        await page.close({ code: 1001, reason: 'offline' });
      }
      open.clear();
    },
    async online() {
      offline = false;
      await context.setOffline(false);
    },
  };
}
