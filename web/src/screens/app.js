// Tab-wide state the shell owns: who is signed in, the one WebSocket and in-app navigation. entry.js
// wires it up; screens (including the room screens of other units) import from here instead of
// creating their own socket, so a tab never holds two connections.

import { createStore } from '../core/store.js';
import { http } from '../core/http.js';
import { applyTheme } from '../core/theme.js';
import { createSocket } from '../net/socket.js';
import { toast } from '../ui/toast.js';

const OFFLINE_NOTICE_MS = 10_000;

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

// `?return=` values: same-origin paths only (no scheme, no //host, no /\host, no control characters).
export function safeReturn(value, fallback = '/dashboard') {
  if (typeof value !== 'string' || !value.startsWith('/') || /^\/[/\\]/.test(value) || /[\u0000-\u001f\u007f\\]/.test(value)) {
    return fallback;
  }
  return value;
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
let banner = null;

function showStatus({ state, offlineSince, code }) {
  let text = null;
  if (state === 'closed' && (code === 4401 || code === 4403)) {
    text = 'You were signed out. Reload the page to continue.';
  } else if (state !== 'open' && state !== 'closed' && offlineSince !== null && Date.now() - offlineSince > OFFLINE_NOTICE_MS) {
    text = 'Reconnecting… your changes are saved and will sync.';
  }
  if (text === null) {
    banner?.dismiss();
    banner = null;
  } else if (banner?.text !== text) {
    banner?.dismiss();
    banner = { text, dismiss: toast(text, { variant: state === 'closed' ? 'error' : 'default', duration: 0 }) };
  }
}

/** The tab's socket, created on first use; start() is idempotent, so callers simply call it. */
export function getSocket() {
  if (socket === null) {
    const created = createSocket({
      pid: currentMe()?.principal?.id ?? null,
      onNack: (nack) => toast(nackMessage(nack), { variant: 'error', duration: 6000 }),
    });
    let timer = 0;
    // The status store only changes on transitions, so a second look after the notice delay is
    // what turns a lingering reconnect into the "Reconnecting…" notice.
    created.status.select((s) => s, Object.is, (status) => {
      clearTimeout(timer);
      showStatus(status);
      if (status.state !== 'open' && status.state !== 'closed') {
        timer = setTimeout(() => showStatus(created.status.get()), OFFLINE_NOTICE_MS + 100);
      }
    });
    socket = created;
  }
  return socket;
}

export async function signOut({ everywhere = false } = {}) {
  await http.post(everywhere ? '/api/auth/logout-all' : '/api/auth/logout');
  socket?.stop();
  socket = null;
  setMe(null);
  await navigate('/login');
}
