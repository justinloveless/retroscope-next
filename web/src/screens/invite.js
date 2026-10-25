// /invite#t=<token>: the way into a team (UX18 decision 13). The token stays out of the address bar and out
// of server logs (S23). It waits as the pending invite (pending-invite.js, localStorage with an expiry) while
// the visitor signs in, possibly through an emailed link opened in another tab, and the landing after sign-in
// resumes it; accepting, declining or signing out clears it. A signed-out visitor is offered "Email me a
// sign-in link" first: the link signs in, creates the account if needed and verifies the address, which an
// email invite requires.

import { h, patch, replaceContent } from '../core/h.js';
import { http } from '../core/http.js';
import { currentUser, navigate, refreshMe } from './app.js';
import { MUTED, card, createFrame, errorText, field, form, link, muted, notice, row, stack } from './frame.js';
import { teamBadge, teamPath } from './header.js';
import { clearPendingInvite, pendingInvite, savePendingInvite } from './pending-invite.js';

const RETURN = encodeURIComponent('/invite');

const ACCEPT_ERRORS = {
  email_mismatch: 'This invite was sent to a different email address. Sign in with that address to accept it.',
  email_unverified: 'Verify your email address first: open the link we emailed you, and this invite opens again.',
  expired: 'This invite has expired. Ask a team admin for a new one.',
  revoked: 'This invite was revoked. Ask a team admin for a new one.',
  not_found: 'This invite is no longer valid. Ask a team admin for a new one.',
};

// Answers that mean nobody can use the token again.
const DEAD = new Set(['expired', 'revoked', 'not_found']);

function readToken() {
  const fromHash = new URLSearchParams(location.hash.slice(1)).get('t');
  if (location.hash) {
    history.replaceState(history.state, '', location.pathname + location.search);
  }
  if (fromHash) {
    savePendingInvite(fromHash);
    return fromHash;
  }
  return pendingInvite();
}

/** Leaves the invite for later: '/' then goes to the user's team instead of back here. */
function notNow() {
  clearPendingInvite();
  navigate('/');
}

const notNowButton = (label = 'Not now') => h('button', { type: 'button', class: 'btn btn-ghost', on: { click: notNow } }, label);

function joinCard(preview, lead, ...rest) {
  const badge = patch(teamBadge({ id: '', name: preview.teamName }), {
    style: { width: '3rem', height: '3rem', 'justify-self': 'start', 'border-radius': 'var(--radius)', 'font-size': '1.0625rem' },
  });
  return card(null,
    badge,
    h('div', { style: stack('0.375rem') }, h('h1', { text: `Join ${preview.teamName}`, style: { 'font-size': '1.5rem', 'font-weight': '750' } }), lead),
    ...rest);
}

function signedOutView(preview) {
  const lead = preview.kind === 'email'
    ? 'This invite was sent to your email address. Sign in with that same address to accept it.'
    : 'Sign in or create an account to accept. Any account works with this invite link.';
  const holder = h('div', { style: stack('0.75rem') });
  const email = field({ label: 'Email', name: 'email', type: 'email', autocomplete: 'email', required: true });
  const request = form({
    fields: { email },
    submitLabel: 'Email me a sign-in link',
    submitClass: 'btn btn-indigo btn-lg',
    async submit(values) {
      const to = values.email.trim();
      await http.post('/api/auth/magic/request', { email: to });
      replaceContent(holder, h('p', { role: 'status', text: `If ${to} can receive mail, a sign-in link is on its way. Open it in this browser, and this invite opens after you sign in.` }));
    },
  });
  holder.append(request.el, muted('New here? The link creates your account and confirms your email in one step.'));
  const line = () => h('span', { style: { flex: '1', height: '1px', background: 'hsl(var(--border))' } });
  return joinCard(preview,
    h('p', { text: lead, style: { ...MUTED, 'font-size': '0.9375rem' } }),
    holder,
    h('p', { style: { ...row('0.625rem'), 'flex-wrap': 'nowrap', ...MUTED, 'font-size': '0.8125rem' } }, line(), 'or', line()),
    h('div', { style: { display: 'grid' } },
      h('a', { href: `/auth/oidc/google/start?return=${RETURN}`, class: 'btn btn-outline' }, 'Continue with Google')),
    h('div', { style: { ...row('1rem'), 'justify-content': 'center' } },
      link(`/login?return=${RETURN}`, 'Sign in with a password', { class: 'btn btn-link' }),
      link(`/login?tab=signup&return=${RETURN}`, 'Create an account', { class: 'btn btn-link' })));
}

function signedInView(preview, token) {
  const accept = h('button', { type: 'button', class: 'btn btn-primary btn-lg' }, 'Accept invite');
  const alert = h('p', { role: 'alert', hidden: true, style: { color: 'var(--red)' } });
  accept.addEventListener('click', async () => {
    accept.disabled = true;
    try {
      const joined = await http.post('/api/invites/accept', { token });
      clearPendingInvite();
      // The header's switcher and '/' read `me`, which this navigation would not reload.
      await refreshMe().catch(() => {});
      await navigate(teamPath(joined.teamId), { replace: true });
    } catch (error) {
      if (DEAD.has(error.code)) {
        clearPendingInvite();
      }
      alert.textContent = errorText(error, ACCEPT_ERRORS);
      alert.hidden = false;
      accept.disabled = false;
    }
  });
  return joinCard(preview,
    h('p', { text: `You are signed in as ${currentUser().email}.`, style: { ...MUTED, 'font-size': '0.9375rem' } }),
    h('div', { style: row() }, accept, notNowButton()),
    alert);
}

export function mount(root) {
  const frame = createFrame(root, { width: '30rem' });
  let shown = false;

  async function show() {
    frame.setTitle(null);
    document.title = 'Team invite · Retroscope';
    const token = readToken();
    const away = currentUser() ? notNowButton('Continue') : null;
    if (!token) {
      replaceContent(frame.main, notice('This invite link is incomplete. Ask whoever sent it for the full link.', away));
      return;
    }
    replaceContent(frame.main, notice('Checking your invite…'));
    let preview;
    try {
      preview = await http.post('/api/invites/preview', { token });
    } catch (error) {
      if (DEAD.has(error.code)) {
        clearPendingInvite();
      }
      replaceContent(frame.main, notice(errorText(error, ACCEPT_ERRORS), away));
      return;
    }
    if (preview.expired) {
      clearPendingInvite();
      replaceContent(frame.main, joinCard(preview, muted(ACCEPT_ERRORS.expired), away));
      return;
    }
    replaceContent(frame.main, currentUser() ? signedInView(preview, token) : signedOutView(preview));
  }

  return {
    update() {
      if (!shown) {
        shown = true;
        show();
      }
    },
    unmount() {
      frame.destroy();
    },
  };
}
