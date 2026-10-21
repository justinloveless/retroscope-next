// Tab-wide state the shell owns: who is signed in, the one WebSocket and in-app navigation. entry.js
// wires it up; screens (including the room screens of other units) import from here instead of
// creating their own socket, so a tab never holds two connections. The socket code loads on first use,
// so pages without a room never download it.

import { createStore } from '../core/store.js';
import { http } from '../core/http.js';
import { applyTheme } from '../core/theme.js';
import { toast } from '../ui/toast.js';

// `me` is the /api/me body ({principal, user?, teams, unreadMentions}) or null when signed out.
export const session = createStore({ me: null });

export function setMe(me) {
  session.set({ me: me ?? null });
}

export const currentMe = () => session.get().me;
export const currentUser = () => session.get().me?.user ?? null;

export async function refreshMe() {
  try {
    setMe(await http.get('/api/me'));
  } catch (error) {
    if (error.status !== 401) {
      throw error;
    }
    setMe(null);
  }
  return currentMe();
}

// A signed-in response (login, magic link) carries the /api/me body.
export function signedIn(me) {
  setMe(me);
  if (me?.user?.theme) {
    applyTheme(me.user.theme);
  }
}

let navigator = (to) => {
  location.assign(to);
  return Promise.resolve();
};

export function setNavigator(fn) {
  navigator = fn;
}

export function navigate(to, options) {
  return navigator(to, options);
}

let header = null;

export function setHeader(value) {
  header = value;
}

/** Fills the app header's centre slot (on a board, its title); the slot clears on the next route. */
export function setHeaderCenter(...nodes) {
  header?.setCenter(...nodes);
}

// `?return=` values: same-origin paths only (no scheme, no //host, no /\host, no control characters).
export function safeReturn(value, fallback = '/dashboard') {
  if (typeof value !== 'string' || !value.startsWith('/') || /^\/[/\\]/.test(value) || /[\u0000-\u001f\u007f\\]/.test(value)) {
    return fallback;
  }
  return value;
}

/** Applies a theme preference now and, when signed in, saves it to the account. */
export async function setThemePreference(pref) {
  applyTheme(pref);
  if (currentUser()) {
    try {
      setMe(await http.patch('/api/me', { theme: pref }));
    } catch {
      toast('Your theme could not be saved to your account.', { variant: 'error', duration: 5000 });
    }
  }
}

export const avatarUrl = (principal) => (principal?.avatar ? `/blob/${principal.avatar}` : null);

const NACK_MESSAGES = {
  forbidden: 'You are not allowed to do that.',
  not_found: 'That item no longer exists.',
  board_archived: 'This board is archived.',
  stage_forbids: 'The current stage does not allow that.',
  vote_limit: 'You have used all your votes.',
  self_vote: 'You cannot vote for your own card.',
  too_large: 'That is too long.',
  outbox_full: 'You have been offline too long; that change was not saved.',
};

export function nackMessage(nack) {
  return NACK_MESSAGES[nack.code] ?? nack.msg ?? 'That change could not be applied.';
}

let socket = null;
let socketPid = null;
let signedOutNotice = null;

// Room screens draw their own "Reconnecting…" banner; only the terminal close is shown here, because
// the screen that held the socket has nothing left to draw it with.
function showStatus({ state, code }) {
  const terminal = state === 'closed' && (code === 4401 || code === 4403);
  if (terminal && signedOutNotice === null) {
    signedOutNotice = toast('You were signed out. Reload the page to continue.', { variant: 'error', duration: 0 });
  } else if (!terminal && signedOutNotice !== null) {
    signedOutNotice();
    signedOutNotice = null;
  }
}

function stopSocket() {
  const stopping = socket;
  socket = null;
  socketPid = null;
  return stopping?.then((s) => s.stop(), () => {});
}

/**
 * Resolves to the tab's one socket, created on first use with the signed-in principal. A tab whose
 * principal changed (a guest joined, someone signed in) gets a new socket, so its hello and outbox
 * carry the right pid. start() is idempotent, so callers simply call it.
 */
export function getSocket() {
  const pid = currentMe()?.principal?.id ?? null;
  if (socket !== null && socketPid !== pid) {
    stopSocket();
  }
  socketPid = pid;
  socket ??= import('../net/socket.js').then(({ createSocket }) => {
    const created = createSocket({
      pid,
      onNack: (nack) => toast(nackMessage(nack), { variant: 'error', duration: 6000 }),
    });
    created.status.select((s) => s, Object.is, showStatus);
    return created;
  });
  return socket;
}

export async function signOut({ everywhere = false } = {}) {
  await http.post(everywhere ? '/api/auth/logout-all' : '/api/auth/logout');
  await stopSocket();
  setMe(null);
  await navigate('/login');
}
