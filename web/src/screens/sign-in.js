// The sign-in card: Google and Microsoft, email and password, creating an account, the emailed sign-in link
// and the password reset request. /login shows it (screens/auth.js), and so does the welcome page at '/'.

import { h, replaceContent } from '../core/h.js';
import { http } from '../core/http.js';
import { createTabs } from '../ui/tabs.js';
import { toast } from '../ui/toast.js';
import { navigate, safeReturn, signedIn } from './app.js';
import { card, field, form, muted, row, stack } from './frame.js';

const LOGIN_ERRORS = {
  invalid_credentials: 'Email or password is incorrect.',
  rate_limited: 'Too many sign-in attempts. Wait a few minutes, or use a sign-in link.',
};

// OIDC failures come back to /login?error=<code>.
const OIDC_ERRORS = {
  account_exists: 'An account with this email exists. Sign in with your existing method, then link this provider from Account settings.',
  tenant_not_allowed: 'Your Microsoft organisation is not allowed to sign in here.',
  provider_disabled: 'That sign-in method is not available.',
};

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

/** The sign-in and create-account card, with the emailed-link and reset forms. Returns {el, destroy()}. */
export function signInCard(query) {
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
  return { el: body, destroy: () => tabs.destroy() };
}
