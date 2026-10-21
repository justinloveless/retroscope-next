// /invite#t=<token>. The token stays out of the address bar and out of server logs (S23); while the
// visitor signs in it waits in sessionStorage.

import { h, replaceContent } from '../core/h.js';
import { http } from '../core/http.js';
import { currentUser, navigate } from './app.js';
import { card, createFrame, errorText, link, muted, notice, row } from './frame.js';

const PENDING = 'rs.invite';

const ACCEPT_ERRORS = {
  email_mismatch: 'This invite was sent to a different email address. Sign in with that account to accept it.',
  email_unverified: 'Verify your email address first, then open the invite again.',
  expired: 'This invite has expired. Ask a team admin for a new one.',
  revoked: 'This invite was revoked. Ask a team admin for a new one.',
  not_found: 'This invite is no longer valid. Ask a team admin for a new one.',
};

function storage(write) {
  try {
    return write(sessionStorage);
  } catch {
    return null;
  }
}

function readToken() {
  const fromHash = new URLSearchParams(location.hash.slice(1)).get('t');
  if (location.hash) {
    history.replaceState(history.state, '', location.pathname + location.search);
  }
  if (fromHash) {
    storage((s) => s.setItem(PENDING, fromHash));
    return fromHash;
  }
  return storage((s) => s.getItem(PENDING));
}

export function mount(root) {
  const frame = createFrame(root, { title: 'Team invite', width: '32rem' });
  let shown = false;

  async function show() {
    const token = readToken();
    if (!token) {
      replaceContent(frame.main, notice('This invite link is incomplete. Ask whoever sent it for the full link.'));
      return;
    }
    replaceContent(frame.main, notice('Checking your invite…'));
    let preview;
    try {
      preview = await http.post('/api/invites/preview', { token });
    } catch (error) {
      replaceContent(frame.main, notice(errorText(error, ACCEPT_ERRORS)));
      return;
    }
    const heading = h('p', { style: { 'font-size': '1.125rem' } }, 'You are invited to join ', h('strong', { text: preview.teamName }), '.');
    if (preview.expired) {
      storage((s) => s.removeItem(PENDING));
      replaceContent(frame.main, card(null, heading, muted(ACCEPT_ERRORS.expired)));
      return;
    }
    if (!currentUser()) {
      const back = encodeURIComponent('/invite');
      replaceContent(frame.main, card(null, heading, muted('Sign in or create an account to accept.'),
        h('div', { style: row() },
          link(`/login?return=${back}`, 'Sign in', { class: 'btn btn-primary' }),
          link(`/login?tab=signup&return=${back}`, 'Create account', { class: 'btn btn-outline' }))));
      return;
    }
    const accept = h('button', { type: 'button', class: 'btn btn-primary' }, 'Accept invite');
    const alert = h('p', { role: 'alert', hidden: true, style: { color: 'var(--red)' } });
    accept.addEventListener('click', async () => {
      accept.disabled = true;
      try {
        const joined = await http.post('/api/invites/accept', { token });
        storage((s) => s.removeItem(PENDING));
        await navigate(`/teams/${joined.teamId}`, { replace: true });
      } catch (error) {
        alert.textContent = errorText(error, ACCEPT_ERRORS);
        alert.hidden = false;
        accept.disabled = false;
      }
    });
    replaceContent(frame.main, card(null, heading, h('div', { style: row() }, accept, link('/dashboard', 'Not now', { class: 'btn btn-ghost' })), alert));
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
