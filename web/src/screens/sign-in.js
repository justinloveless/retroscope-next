// The sign-in card: Google, email and password, creating an account, the emailed sign-in link
// and the password reset request. /login shows it (screens/auth.js), and so does the welcome page at '/'
// (UX18 decision 15). Without a ?return= it lands on the pending invite when one waits, else on '/', which
// finds the user's team.

import { h, replaceContent } from '../core/h.js';
import { http } from '../core/http.js';
import { createTabs } from '../ui/tabs.js';
import { toast } from '../ui/toast.js';
import { navigate, safeReturn, signedIn } from './app.js';
import { card, field, form, muted, row, stack } from './frame.js';
import { pendingInvite } from './pending-invite.js';

const LOGIN_ERRORS = {
  invalid_credentials: 'Email or password is incorrect.',
  rate_limited: 'Too many sign-in attempts. Wait a few minutes, or use a sign-in link.',
};

// OIDC failures come back to /login?error=<code>.
const OIDC_ERRORS = {
  account_exists: 'An account with this email exists. Sign in with your existing method, then link this provider from Account settings.',
  provider_disabled: 'That sign-in method is not available.',
};

function oidcButtons(returnTo) {
  return h('div', { style: stack('0.5rem') },
    h('a', { href: `/auth/oidc/google/start?return=${encodeURIComponent(returnTo)}`, class: 'btn btn-outline' }, 'Continue with Google'));
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
  // Google comes back by a full page load, and the server answers '/' with the user's team: it
  // cannot see the invite, which waits in this browser. So a pending invite is named as the return itself.
  const returnTo = safeReturn(query.get('return'), pendingInvite() ? '/invite' : '/');
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
    signin: [signIn.el, h('div', { style: { ...row('1rem'), 'justify-content': 'space-between' } },
      h('button', { type: 'button', class: 'btn btn-link btn-sm', dataset: { mode: 'magic' } }, 'Email me a sign-in link'),
      h('button', { type: 'button', class: 'btn btn-link btn-sm', dataset: { mode: 'reset' } }, 'Forgot password?'))],
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
  const providers = oidcButtons(returnTo);
  const line = () => h('span', { style: { flex: '1', height: '1px', background: 'hsl(var(--border))' } });
  const divider = h('p', { style: { ...row('0.625rem'), 'flex-wrap': 'nowrap', color: 'hsl(var(--muted-foreground))', 'font-size': '0.8125rem' } }, line(), 'or', line());

  function show(mode) {
    const tabbed = mode === 'signin' || mode === 'signup';
    if (tabbed) {
      tabs.select(mode);
    } else {
      replaceContent(other, panelFor[mode], back);
    }
    tabs.el.hidden = !tabbed;
    providers.hidden = !tabbed;
    divider.hidden = !tabbed;
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
    tabs.el,
    error ? h('p', { role: 'alert', style: { color: 'var(--red)' }, text: OIDC_ERRORS[error] ?? 'Signing in did not work. Try again.' }) : null,
    providers,
    divider,
    ...tabs.panels.values(), other);
  body.setAttribute('aria-label', 'Sign in');
  body.addEventListener('click', (event) => {
    const mode = event.target.closest('[data-mode]')?.dataset.mode;
    if (mode) {
      show(mode);
    }
  });
  return { el: body, destroy: () => tabs.destroy() };
}
