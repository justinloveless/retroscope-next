import { routes } from './routes.js';

function match(pattern, path) {
  const want = pattern.split('/');
  const have = path.split('/');
  const params = {};
  for (let i = 0; i < want.length; i++) {
    if (want[i] === '*') {
      return params;
    }
    if (i >= have.length) {
      return null;
    }
    if (want[i].startsWith(':')) {
      params[want[i].slice(1)] = decodeURIComponent(have[i]);
    } else if (want[i] !== have[i]) {
      return null;
    }
  }
  return want.length === have.length ? params : null;
}

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
