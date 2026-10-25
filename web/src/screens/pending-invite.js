// The pending team invite (UX18 decision 13). Invites are the only way into a team, and signing in can take a
// visitor through an emailed link that opens in another tab, so the token waits in localStorage (`rs.invite`)
// with an expiry instead of in this tab's sessionStorage. It is cleared on accept, on sign-out and when it
// expires. An in-app '/' resumes it (app.js homePath), the sign-in card returns to it (sign-in.js) and the
// create-or-join screen opens it (teams.js). The server never sees it, so a member's full page load of '/'
// goes to their team, and the invite waits for the next of those.

const KEY = 'rs.invite';

/** How long an invite waits for its visitor to sign in: a day, the life of the email verification link. */
export const PENDING_INVITE_MS = 24 * 60 * 60 * 1000;

// Tokens are base64url; imported ones keep the original app's strings. Either way a token is one URL-safe word.
const TOKEN = /^[\w.~-]{6,200}$/;

function local() {
  try {
    return globalThis.localStorage ?? null;
  } catch {
    return null;
  }
}

/**
 * The token in what a visitor pasted: an invite link (`…/invite#t=<token>`), the older link form
 * (`…/invite/<token>`) or the bare token; null when it is none of those. Only the token is kept: the
 * pasted address itself is never followed.
 */
export function parseInvite(text) {
  const value = String(text ?? '').trim();
  if (TOKEN.test(value)) {
    return value;
  }
  let url;
  try {
    url = new URL(value, 'https://retroscope.invalid/');
  } catch {
    return null;
  }
  const path = url.pathname.replace(/\/+$/, '');
  let token = null;
  if (path === '/invite') {
    token = new URLSearchParams(url.hash.slice(1)).get('t');
  } else if (/^\/invite\/[^/]+$/.test(path)) {
    try {
      token = decodeURIComponent(path.slice('/invite/'.length));
    } catch {
      return null;
    }
  }
  return token !== null && TOKEN.test(token) ? token : null;
}

export function savePendingInvite(token, now = Date.now(), storage = local()) {
  try {
    storage?.setItem(KEY, JSON.stringify({ t: token, exp: now + PENDING_INVITE_MS }));
  } catch {
    // Without storage the visitor opens the invite link again after signing in.
  }
}

export function clearPendingInvite(storage = local()) {
  try {
    storage?.removeItem(KEY);
  } catch {
    // Nothing was stored.
  }
}

/** The waiting invite's token, or null; an expired or unreadable one is removed. */
export function pendingInvite(now = Date.now(), storage = local()) {
  let stored = null;
  let entry = null;
  try {
    stored = storage?.getItem(KEY) ?? null;
    entry = JSON.parse(stored);
  } catch {
    entry = null;
  }
  if (typeof entry?.t === 'string' && typeof entry.exp === 'number' && now < entry.exp) {
    return entry.t;
  }
  if (stored !== null) {
    clearPendingInvite(storage);
  }
  return null;
}
