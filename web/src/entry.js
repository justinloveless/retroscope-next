import { h } from './core/h.js';
import { http } from './core/http.js';
import { createRouter, resolve } from './core/router.js';
import { applyTheme, storedTheme } from './core/theme.js';
import { routes } from './routes.js';
import { toast } from './ui/toast.js';
import { currentMe, getSocket, setHeader, setMe, setNavigator } from './screens/app.js';
import { createHeader } from './screens/header.js';

const THEMES = new Set(['light', 'dark', 'system']);

// Route modules that take the tab's socket as mount(root, boot, {socket}). The socket starts as soon
// as the page loads, so the WebSocket handshake overlaps the chunk download. Screens that still open a
// socket of their own are left out: two sockets of one tab share a cid and fight with 4409.
const SHARED_SOCKET = new Set(['retro/index.js']);

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
  root.replaceChildren(h('main', { class: 'app-not-found' },
    h('h1', { text: 'Page not found' }),
    h('p', { text: 'The page you are looking for does not exist or has moved.' }),
    h('a', { href: '/', 'data-nav': '', class: 'btn btn-primary' }, 'Go home')));
}

async function sharedSocket(route) {
  if (!SHARED_SOCKET.has(route.module) || !currentMe()?.principal) {
    return {};
  }
  const socket = await getSocket();
  socket.start();
  return { socket };
}

const boot = adopt(readBoot());
const host = document.getElementById('app') ?? document.body;
const header = createHeader();
const root = h('div', { class: 'app-view' });
host.replaceChildren(header.el, root);
setHeader(header);

const first = resolve(routes, new URL(location.href));
header.show(first?.route.path ?? null, first?.params);
if (first) {
  // A failure here surfaces again when the router asks for the same socket.
  sharedSocket(first.route).catch(() => {});
}

const router = createRouter({
  routes,
  root,
  boot,
  loadBoot: async (path, signal) => adopt(await http.get(`/api/boot?path=${encodeURIComponent(path)}`, { signal })),
  notFound,
  context: sharedSocket,
  onShow: (route, params) => header.show(route?.path ?? null, params),
  onError(error, url) {
    toast('This page could not be loaded.', {
      variant: 'error',
      action: { label: 'Reload', onClick: () => location.assign(url.href) },
    });
  },
});

setNavigator(router.navigate);
router.start();
