import { h } from './core/h.js';
import { http } from './core/http.js';
import { createRouter } from './core/router.js';
import { applyTheme, storedTheme } from './core/theme.js';
import { routes } from './routes.js';
import { toast } from './ui/toast.js';
import { getSocket, setMe, setNavigator } from './screens/app.js';

const THEMES = new Set(['light', 'dark', 'system']);

// Rooms whose screens subscribe as soon as they mount: the socket handshake overlaps the chunk download.
const ROOM_PATH = /^\/(?:poker\/[^/]+|retro\/[^/]+|teams\/[^/]+(?:\/poker\/[^/]+)?)\/?$/;

function readBoot() {
  try {
    return JSON.parse(document.getElementById('boot')?.textContent ?? 'null') ?? {};
  } catch {
    return {};
  }
}

function adopt(boot) {
  setMe(boot?.me);
  // Keep the pre-paint script's choice: the boot preference, else this browser's stored one.
  applyTheme(THEMES.has(boot?.theme) ? boot.theme : storedTheme() ?? 'system');
  return boot ?? {};
}

function notFound(root) {
  document.title = 'Page not found · Retroscope';
  root.replaceChildren(h('main', {
    style: { display: 'grid', gap: '1rem', 'justify-items': 'center', padding: '4rem 1rem', 'text-align': 'center' },
  },
  h('h1', { text: 'Page not found', style: { 'font-size': '1.75rem', 'font-weight': '700' } }),
  h('p', { text: 'The page you are looking for does not exist or has moved.' }),
  h('a', { href: '/', 'data-nav': '', class: 'btn btn-primary' }, 'Go home')));
}

const boot = adopt(readBoot());
const root = document.getElementById('app') ?? document.body;

if (boot.me && ROOM_PATH.test(location.pathname)) {
  getSocket().start();
}

const router = createRouter({
  routes,
  root,
  boot,
  loadBoot: async (path, signal) => adopt(await http.get(`/api/boot?path=${encodeURIComponent(path)}`, { signal })),
  notFound,
  onError(error, url) {
    toast('This page could not be loaded.', {
      variant: 'error',
      action: { label: 'Reload', onClick: () => location.assign(url.href) },
    });
  },
});

setNavigator(router.navigate);
router.start();
