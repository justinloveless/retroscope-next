import { match } from './core/router.js';
import { routes } from './routes.js';

async function start() {
  const bootEl = document.getElementById('boot');
  const boot = bootEl ? JSON.parse(bootEl.textContent) : {};
  const root = document.getElementById('app') ?? document.body;
  for (const route of routes) {
    const params = match(route.path, location.pathname);
    if (params) {
      const screen = await route.load();
      screen.mount(root, boot).update(params);
      return;
    }
  }
}

start();
