// /login and the emailed-link pages /auth/magic, /auth/reset and /auth/verify (§e). Link tokens arrive
// in the URL fragment, which never reaches the server; the page drops it from the address bar at once,
// and keeps it in sessionStorage only while the visitor signs in to finish verifying.

import { h, replaceContent } from '../core/h.js';
import { http } from '../core/http.js';
import { createTabs } from '../ui/tabs.js';
import { toast } from '../ui/toast.js';
import { currentMe, navigate, refreshMe, safeReturn, signedIn } from './app.js';
import { card, createFrame, errorText, field, form, link, muted, notice, row, stack } from './frame.js';

const PENDING_VERIFY = 'rs.verify';

const LOGIN_ERRORS = {
  invalid_credentials: 'Email or password is incorrect.',
  rate_limited: 'Too many sign-in attempts. Wait a few minutes, or use a sign-in link.',
};

const LINK_ERRORS = {
  invalid_token: 'This link has expired or was already used.',
  not_found: 'This link has expired or was already used.',
  expired: 'This link has expired or was already used.',
};

// OIDC failures come back to /login?error=<code>.
const OIDC_ERRORS = {
  account_exists: 'An account with this email exists. Sign in with your existing method, then link this provider from Account settings.',
  tenant_not_allowed: 'Your Microsoft organisation is not allowed to sign in here.',
  provider_disabled: 'That sign-in method is not available.',
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

function oidcButtons(returnTo) {
  const start = (provider) => `/auth/oidc/${provider}/start?return=${encodeURIComponent(returnTo)}`;
  return h('div', { style: stack('0.5rem') },
    h('a', { href: start('google'), class: 'btn btn-outline' }, 'Continue with Google'),
    h('a', { href: start('entra'), class: 'btn btn-outline' }, 'Continue with Microsoft'));
}

function emailLinkForm({ path, label, sent }) {
  const email = field({ label: 'Email', name: 'email', type: 'email', autocomplete: 'email', required: true });
  const holder = h('div');
  const request = form({
    fields: { email },
    submitLabel: label,
    async submit(values) {
      await http.post(path, { email: values.email.trim() });
      replaceContent(holder, h('p', { role: 'status', text: sent(values.email.trim()) }));
    },
  });
  holder.append(request.el);
  return holder;
}

function loginView(frame, query) {
  const returnTo = safeReturn(query.get('return'));
  const done = () => navigate(returnTo, { replace: true });

  const email = field({ label: 'Email', name: 'email', type: 'email', autocomplete: 'username', required: true });
  const password = field({ label: 'Password', name: 'password', type: 'password', autocomplete: 'current-password', required: true });
  const signIn = form({
    fields: { email, password },
    submitLabel: 'Sign in',
    messages: LOGIN_ERRORS,
    async submit(values) {
      signedIn(await http.post('/api/auth/login', { email: values.email.trim(), password: values.password }));
      await done();
    },
  });

  const fullName = field({ label: 'Full name', name: 'fullName', autocomplete: 'name', required: true, maxlength: 100 });
  const newEmail = field({ label: 'Email', name: 'email', type: 'email', autocomplete: 'email', required: true });
  const newPassword = field({ label: 'Password', name: 'password', type: 'password', autocomplete: 'new-password', required: true, hint: 'At least 10 characters.' });
  const signUp = form({
    fields: { fullName, email: newEmail, password: newPassword },
    submitLabel: 'Create account',
    async submit(values) {
      const credentials = { email: values.email.trim(), password: values.password };
      await http.post('/api/auth/signup', { ...credentials, fullName: values.fullName.trim() });
      try {
        signedIn(await http.post('/api/auth/login', credentials));
      } catch {
        // The address already had an account: the server emailed its owner instead (no oracle here).
        signUp.setError('Check your inbox: we sent you an email about this address.');
        return;
      }
      toast('Account created. Check your inbox to verify your email.', { variant: 'success' });
      await done();
    },
  });

  const panelFor = {
    signin: [signIn.el, h('div', { style: row('1rem') },
      h('button', { type: 'button', class: 'btn btn-ghost btn-sm', dataset: { mode: 'magic' } }, 'Email me a sign-in link'),
      h('button', { type: 'button', class: 'btn btn-ghost btn-sm', dataset: { mode: 'reset' } }, 'Forgot password?'))],
    signup: [signUp.el],
    magic: [muted('We will email you a link that signs you in. It works once and expires in 15 minutes.'), emailLinkForm({
      path: '/api/auth/magic/request',
      label: 'Send sign-in link',
      sent: (to) => `If ${to} can receive mail, a sign-in link is on its way. Open it in this browser.`,
    })],
    reset: [muted('We will email you a link to choose a new password. It expires in 30 minutes.'), emailLinkForm({
      path: '/api/auth/reset/request',
      label: 'Send reset link',
      sent: (to) => `If an account exists for ${to}, a reset link is on its way.`,
    })],
  };

  const tabs = createTabs({
    tabs: [{ id: 'signin', label: 'Sign in' }, { id: 'signup', label: 'Create account' }],
    selected: query.get('tab') === 'signup' ? 'signup' : 'signin',
    label: 'Sign in or create an account',
  });
  const other = h('div', { hidden: true, style: stack('1rem') });
  for (const [id, panel] of tabs.panels) {
    panel.append(...panelFor[id]);
  }
  const back = h('button', { type: 'button', class: 'btn btn-ghost btn-sm', dataset: { mode: 'signin' } }, 'Back to sign in');

  function show(mode) {
    const tabbed = mode === 'signin' || mode === 'signup';
    if (tabbed) {
      tabs.select(mode);
    } else {
      replaceContent(other, panelFor[mode], back);
    }
    tabs.el.hidden = !tabbed;
    for (const [id, panel] of tabs.panels) {
      panel.hidden = !tabbed || id !== tabs.selected;
    }
    other.hidden = tabbed;
    if (!tabbed) {
      other.querySelector('input')?.focus();
    }
  }

  const error = query.get('error');
  const body = card(null,
    error ? h('p', { role: 'alert', style: { color: 'var(--red)' }, text: OIDC_ERRORS[error] ?? 'Signing in did not work. Try again.' }) : null,
    oidcButtons(returnTo),
    h('p', { style: { 'text-align': 'center', color: 'hsl(var(--muted-foreground))', 'font-size': '0.8125rem' }, text: 'or' }),
    tabs.el, ...tabs.panels.values(), other);
  body.addEventListener('click', (event) => {
    const mode = event.target.closest('[data-mode]')?.dataset.mode;
    if (mode) {
      show(mode);
    }
  });
  frame.setTitle('Sign in to Retroscope');
  replaceContent(frame.main, body);
  return () => tabs.destroy();
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
