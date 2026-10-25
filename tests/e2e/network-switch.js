// Takes a browser context off the network and back, for specs against either target: the real-stack specs use it
// through real/_real.js, and the fake harness can hold it to what it promises against tests/web-fake-server.

/**
 * Takes a context off the network and back. `context.setOffline` cuts HTTP everywhere, but Firefox and WebKit keep an
 * open WebSocket alive through it, so the room socket runs through a route that this switch closes, and that refuses
 * new sockets while offline. Install it before the page opens its socket (reload a page that already has one).
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
    page.onClose(() => open.delete(pair));
  });
  return {
    async offline() {
      offline = true;
      await context.setOffline(true);
      for (const { page, server } of open) {
        await server.close({ code: 1001, reason: 'offline' });
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
