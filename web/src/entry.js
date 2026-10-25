import { h } from './core/h.js';
import { http } from './core/http.js';
import { createRouter, resolve } from './core/router.js';
import { applyTheme, storedTheme } from './core/theme.js';
import { routes } from './routes.js';
import { toast } from './ui/toast.js';
import { currentUser, getSocket, redirectFor, setHeader, setMe, setNavigator } from './screens/app.js';
import { createHeader } from './screens/header.js';

const THEMES = new Set(['light', 'dark', 'system']);

// Room screens share the tab's one socket (two sockets of one tab share a cid and fight with 4409). It
// starts as soon as the page loads, so the WebSocket handshake overlaps the chunk download, and reaches
// the screen as mount(root, boot, {socket}). Only a signed-in user opens it: /ws admits nobody else.
const ROOM_MODULES = new Set(['poker/index.js', 'retro/index.js', 'team/index.js']);

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

// The shared "You can't open this page" screen (UX18 decision 2) loads only when a path matches no route.
function notFound(root) {
  const holder = h('div');
  root.replaceChildren(holder);
  import('./screens/no-access.js').then(({ showNoAccess }) => {
    if (holder.isConnected) {
      showNoAccess(holder);
    }
  }, () => toast('This page could not be loaded.', { variant: 'error' }));
}

async function sharedSocket(route) {
  if (!ROOM_MODULES.has(route.module) || !currentUser()) {
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

// A signed-out visitor on a member page signs in first, and a signed-in '/' goes home (app.js homePath). The
// server redirects full page loads itself; this covers in-app navigation and a server that does not (the
// fake leaves the sign-in gate to the client).
const redirect = (url, route) => redirectFor(url, route?.path ?? null);
const asked = new URL(location.href);
const moved = redirect(asked, resolve(routes, asked)?.route);
if (moved) {
  history.replaceState(history.state, '', moved);
}

const first = resolve(routes, new URL(location.href));
header.show(first?.route.path ?? null, first?.params);
if (first) {
  // A failure here surfaces again when the router asks for the same socket.
  sharedSocket(first.route).catch(() => {});
}

const router = createRouter({
  routes,
  root,
  // The page's boot JSON describes the path it was served for.
  boot: moved ? null : boot,
  loadBoot: async (path, signal) => adopt(await http.get(`/api/boot?path=${encodeURIComponent(path)}`, { signal })),
  notFound,
  context: sharedSocket,
  redirect,
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
