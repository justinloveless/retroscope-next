// Tab-wide state the shell owns: who is signed in, the one WebSocket and in-app navigation. entry.js
// wires it up; screens (including the room screens of other units) import from here instead of
// creating their own socket, so a tab never holds two connections. The socket code loads on first use,
// so pages without a room never download it.

import { createStore } from '../core/store.js';
import { http } from '../core/http.js';
import { applyTheme } from '../core/theme.js';
import { toast } from '../ui/toast.js';
import { clearPendingInvite, pendingInvite } from './pending-invite.js';

// `me` is the /api/me body ({principal, user, teams: [{id, name, role, openActions}], lastTeamId,
// unreadMentions}) or null when signed out. Only users sign in (UX18): there is no guest shape.
export const session = createStore({ me: null });

export function setMe(me) {
  session.set({ me: me ?? null });
}

export const currentMe = () => session.get().me;
export const currentUser = () => session.get().me?.user ?? null;

/**
 * Reloads `me`. A navigation within one screen module never refetches the boot JSON, so screens call this
 * after anything that changes the user's teams: creating, joining, leaving or deleting one, renaming it,
 * and role changes.
 */
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

/** The team a page without a team of its own shows (UX18 decision 9): the last one used, else the first. */
export function homeTeam(me = currentMe()) {
  const teams = me?.teams ?? [];
  return teams.find((t) => t.id === me?.lastTeamId) ?? teams[0] ?? null;
}

/**
 * Where '/' takes a signed-in user (UX18 decision 8, as the server's redirect does): a pending invite
 * first, then the last team, then the first team, else the create-or-join screen. Signed out, '/' is
 * the welcome page.
 */
export function homePath(me = currentMe(), invite = pendingInvite()) {
  if (!me?.user) {
    return '/';
  }
  if (invite) {
    return '/invite';
  }
  const team = homeTeam(me);
  return team ? `/teams/${encodeURIComponent(team.id)}` : '/teams';
}

// The routes a signed-out visitor may open (UX18 decision 1); every other route needs a signed-in member.
const PUBLIC_ROUTES = new Set(['/', '/login', '/auth/*', '/invite']);

/**
 * Where the router sends `url` (matched to the route pattern `route`, null when none matches) instead of
 * showing it: a signed-out visitor goes to sign in first and comes back, and a signed-in visit to '/'
 * finds the user's team. Null shows `url`. Unknown paths are not gated, as the server answers them 404.
 */
export function redirectFor(url, route, me = currentMe()) {
  if (!me?.user) {
    return route === null || PUBLIC_ROUTES.has(route) ? null : `/login?return=${encodeURIComponent(url.pathname + url.search)}`;
  }
  return route === '/' ? homePath(me) : null;
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

// The header's team context (UX18 decision 10). The header takes the team from the route; a room screen
// whose URL has none tells it the room's team from its snapshot, and pages that hold a team room patch the
// team's live values. Both reset on the next page. The header never subscribes to a room itself.

/** The team the page belongs to (a room snapshot's teamId); null goes back to the route's. */
export function setHeaderTeam(teamId) {
  header?.setTeam(teamId ?? null);
}

/** The active section: 'poker', 'retros', 'actions', 'kudos', 'members', or null for none. */
export function setHeaderTab(tab) {
  header?.setTab(tab);
}

/** Shows the header on a page that hides it (the poker screen) once that page shows a shell screen instead. */
export function revealHeader() {
  header?.reveal();
}

/** Live values of one of the user's teams ({name, role, openActions}) from a room that holds them. */
export function patchTeam(teamId, values) {
  const me = currentMe();
  const team = me?.teams?.find((t) => t.id === teamId);
  const changed = Object.entries(values).filter(([key, value]) => value !== undefined && team?.[key] !== value);
  if (team && changed.length > 0) {
    setMe({ ...me, teams: me.teams.map((t) => (t === team ? { ...t, ...Object.fromEntries(changed) } : t)) });
  }
}

// `?return=` values: same-origin paths only (no scheme, no //host, no /\host, no control characters).
export function safeReturn(value, fallback = '/') {
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
 * Resolves to the tab's one socket, created on first use with the signed-in user. A tab whose user
 * changed gets a new socket, so its hello and outbox carry the right pid. start() is idempotent, so
 * callers simply call it. /ws admits signed-in users only, as do the room pages that call this.
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

/** Forgets the session in this tab once the server has ended it: the socket, `me` and any pending invite. */
export async function signedOut() {
  clearPendingInvite();
  await stopSocket();
  setMe(null);
}

export async function signOut({ everywhere = false, to = '/login' } = {}) {
  await http.post(everywhere ? '/api/auth/logout-all' : '/api/auth/logout');
  await signedOut();
  await navigate(to);
}
