// /login and the emailed-link pages /auth/magic, /auth/reset and /auth/verify (§e). Link tokens arrive
// in the URL fragment, which never reaches the server; the page drops it from the address bar at once,
// and keeps it in sessionStorage only while the visitor signs in to finish verifying.

import { h, replaceContent } from '../core/h.js';
import { http } from '../core/http.js';
import { toast } from '../ui/toast.js';
import { currentMe, navigate, refreshMe, safeReturn, signedIn } from './app.js';
import { card, createFrame, errorText, field, form, link, muted, notice, row } from './frame.js';
import { signInCard } from './sign-in.js';

const PENDING_VERIFY = 'rs.verify';

const LINK_ERRORS = {
  invalid_token: 'This link has expired or was already used.',
  not_found: 'This link has expired or was already used.',
  expired: 'This link has expired or was already used.',
};

function store(key, value) {
  try {
    if (value === null) {
      sessionStorage.removeItem(key);
    } else {
      sessionStorage.setItem(key, value);
    }
  } catch {
    // Without storage the visitor opens the emailed link again after signing in.
  }
}

function stored(key) {
  try {
    return sessionStorage.getItem(key);
  } catch {
    return null;
  }
}

function takeToken() {
  const token = new URLSearchParams(location.hash.slice(1)).get('t');
  if (location.hash) {
    history.replaceState(history.state, '', location.pathname + location.search);
  }
  return token;
}

function loginView(frame, query) {
  const signIn = signInCard(query);
  frame.setTitle('Sign in to Retroscope');
  replaceContent(frame.main, signIn.el);
  return signIn.destroy;
}

async function magicView(frame) {
  frame.setTitle('Sign in with your email link');
  const token = takeToken();
  if (!token) {
    replaceContent(frame.main, notice('This sign-in link is incomplete.', link('/login', 'Request a new link', { class: 'btn btn-primary' })));
    return;
  }
  replaceContent(frame.main, notice('Checking your link…'));
  let preview;
  try {
    preview = await http.post('/api/auth/magic/preview', { token });
  } catch (error) {
    replaceContent(frame.main, notice(errorText(error, LINK_ERRORS), link('/login', 'Request a new link', { class: 'btn btn-primary' })));
    return;
  }
  // The token is consumed only on this click (S18): a mail scanner prefetching the link signs nobody in.
  const button = h('button', { type: 'button', class: 'btn btn-primary btn-lg' }, `Sign in as ${preview.email}`);
  const alert = h('p', { role: 'alert', hidden: true, style: { color: 'var(--red)' } });
  button.addEventListener('click', async () => {
    button.disabled = true;
    try {
      signedIn(await http.post('/api/auth/magic/consume', { token }));
      if (!currentMe()?.user) {
        await refreshMe();
      }
      await navigate('/dashboard', { replace: true });
    } catch (error) {
      alert.textContent = errorText(error, LINK_ERRORS);
      alert.hidden = false;
      button.disabled = false;
    }
  });
  replaceContent(frame.main, card(null, h('p', { text: `Sign in to Retroscope as ${preview.email}?` }), h('div', { style: row() }, button), alert));
}

function resetView(frame) {
  frame.setTitle('Choose a new password');
  const token = takeToken();
  if (!token) {
    replaceContent(frame.main, notice('This reset link is incomplete.', link('/login', 'Back to sign in', { class: 'btn btn-primary' })));
    return;
  }
  const password = field({ label: 'New password', name: 'password', type: 'password', autocomplete: 'new-password', required: true, hint: 'At least 10 characters.' });
  const confirm = field({ label: 'Repeat the new password', name: 'confirm', type: 'password', autocomplete: 'new-password', required: true });
  const reset = form({
    fields: { password, confirm },
    submitLabel: 'Set password',
    messages: LINK_ERRORS,
    async submit(values) {
      if (values.password !== values.confirm) {
        confirm.setError('The passwords do not match.');
        return;
      }
      await http.post('/api/auth/reset/consume', { token, password: values.password });
      toast('Password updated. Sign in with your new password.', { variant: 'success' });
      await navigate('/login', { replace: true });
    },
  });
  replaceContent(frame.main, card(null, muted('Every other session of your account will be signed out.'), reset.el));
}

async function verifyView(frame) {
  frame.setTitle('Verify your email');
  const token = takeToken() ?? stored(PENDING_VERIFY);
  if (!token) {
    replaceContent(frame.main, notice('This verification link is incomplete.'));
    return;
  }
  // §e S4: only a session of the account the link was sent to may verify it.
  const signInFirst = () => {
    store(PENDING_VERIFY, token);
    frame.setTitle('Sign in to finish verifying');
    replaceContent(frame.main, notice(
      'Sign in to the account this email was sent to, and we will finish verifying it.',
      link(`/login?return=${encodeURIComponent('/auth/verify')}`, 'Sign in', { class: 'btn btn-primary' })));
  };
  if (!currentMe()?.user) {
    signInFirst();
    return;
  }
  replaceContent(frame.main, notice('Verifying…'));
  try {
    await http.post('/api/auth/verify/consume', { token });
  } catch (error) {
    if (error.status === 401 || error.status === 403) {
      signInFirst();
    } else {
      store(PENDING_VERIFY, null);
      replaceContent(frame.main, notice(errorText(error, LINK_ERRORS)));
    }
    return;
  }
  store(PENDING_VERIFY, null);
  await refreshMe().catch(() => {});
  replaceContent(frame.main, notice('Your email is verified.', link('/dashboard', 'Continue', { class: 'btn btn-primary' })));
}

export function mount(root) {
  const frame = createFrame(root, { width: '28rem' });
  let cleanup = null;

  return {
    update(params) {
      cleanup?.();
      cleanup = null;
      const path = location.pathname.replace(/\/+$/, '');
      if (path === '/login') {
        if (currentMe()?.user) {
          navigate(safeReturn(params.query.get('return')), { replace: true });
          return;
        }
        cleanup = loginView(frame, params.query);
      } else if (path === '/auth/magic') {
        magicView(frame);
      } else if (path === '/auth/reset') {
        resetView(frame);
      } else if (path === '/auth/verify') {
        verifyView(frame);
      } else {
        frame.setTitle('Page not found');
        replaceContent(frame.main, notice('There is nothing at this address.', link('/login', 'Sign in', { class: 'btn btn-primary' })));
      }
    },
    unmount() {
      cleanup?.();
      frame.destroy();
    },
  };
}
