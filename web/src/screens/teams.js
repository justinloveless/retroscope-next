// /teams: create or join a team (UX18 decision 7). Everything happens inside a team, so a signed-in user in
// none lands here, and the team switcher's "Create a team" leads here. Creating needs a verified email: until
// then the form is disabled, with a link that sends the verification email again. Teams are invite-only, so
// joining means opening an invite; a pasted invite link (or its token) is kept as the pending invite and
// previewed on /invite. There is no team discovery and no request to join. A pending invite wins here as it
// does on '/': a full page load of '/' by someone in no team reaches this screen through the server's
// redirect, which cannot see the invite waiting in this browser.

import { h } from '../core/h.js';
import { http } from '../core/http.js';
import { icon } from '../ui/icons.js';
import { currentMe, currentUser, navigate, refreshMe, session } from './app.js';
import { MUTED, createFrame, errorText, field, form, muted, row, stack } from './frame.js';
import { teamPath } from './header.js';
import { parseInvite, pendingInvite, savePendingInvite } from './pending-invite.js';

function panel(id, symbol, tint, title, ...children) {
  return h('section', { class: 'card', 'aria-labelledby': id, style: { ...stack('1rem'), padding: '1.5rem' } },
    h('div', { style: row('0.75rem') },
      h('span', { style: { display: 'inline-flex', padding: '0.625rem', 'border-radius': 'var(--radius)', ...tint } }, icon(symbol)),
      h('h2', { id, text: title, style: { 'font-size': '1.1875rem', 'font-weight': '700' } })),
    ...children);
}

function createPanel() {
  const name = field({ label: 'Team name', name: 'name', required: true, maxlength: 100, autocomplete: 'off' });
  const description = field({ label: 'Description (optional)', name: 'description', type: 'textarea', maxlength: 500, rows: 3 });
  const create = form({
    fields: { name, description },
    submitLabel: 'Create team',
    messages: {
      email_unverified: 'Verify your email address before creating a team.',
      rate_limited: 'You have created many teams today. Try again tomorrow.',
    },
    async submit(values) {
      let team;
      try {
        team = await http.post('/api/teams', { name: values.name.trim(), description: values.description.trim() || null });
      } catch (error) {
        if (error.code === 'email_unverified') {
          refreshMe().catch(() => {});
        }
        throw error;
      }
      await refreshMe().catch(() => {});
      await navigate(teamPath(team.id));
    },
  });

  const sent = h('p', { role: 'status' });
  const resend = h('button', { type: 'button', class: 'btn btn-link', style: { 'justify-self': 'start', 'padding-left': '0' } }, 'Send the link again');
  resend.addEventListener('click', async () => {
    resend.disabled = true;
    try {
      await http.post('/api/auth/verify/request', {});
      sent.textContent = 'If your address still needs verifying, a new link is on its way.';
    } catch (error) {
      sent.textContent = errorText(error, { rate_limited: 'You asked for a link recently. Check your inbox, or try again later.' });
    } finally {
      resend.disabled = false;
    }
  });
  const unverified = h('div', {
    style: {
      ...stack('0.375rem'),
      padding: '0.75rem 0.875rem',
      border: '1px solid color-mix(in srgb, var(--amber) 55%, transparent)',
      'border-radius': 'var(--radius)',
      background: 'color-mix(in srgb, var(--amber) 14%, hsl(var(--background)))',
      'font-size': '0.875rem',
    },
  },
  h('strong', { text: 'Verify your email first' }),
  h('span', { text: 'We sent a verification link when you signed up. Open it, then come back here.' }),
  resend,
  sent);

  return {
    el: panel('teams-create', 'plus', { background: 'hsl(var(--primary))', color: 'hsl(var(--primary-foreground))' }, 'Create a team',
      unverified, create.el, muted('You become its owner and can invite people from Members.')),
    render(user) {
      const verified = user.emailVerified !== false;
      unverified.hidden = verified;
      create.button.disabled = !verified;
    },
  };
}

function joinPanel() {
  const link = field({
    label: 'Already have the link? Paste it',
    name: 'invite',
    required: true,
    autocomplete: 'off',
    spellcheck: 'false',
    placeholder: 'https://…/invite#t=…',
  });
  const join = form({
    fields: { link },
    submitLabel: 'Open invite',
    submitClass: 'btn btn-secondary',
    async submit(values) {
      const token = parseInvite(values.link);
      if (!token) {
        link.setError('Paste the whole invite link, or the code at its end.');
        return;
      }
      savePendingInvite(token);
      await navigate('/invite');
    },
  });
  return panel('teams-join', 'mail', { background: 'var(--avatar-bg)', color: 'var(--avatar-fg)' }, 'Join a team',
    h('p', { text: 'Teams are invite-only. Ask an admin of the team to invite you, then open the link in the invite.' }),
    join.el);
}

export function mount(root) {
  const frame = createFrame(root, { width: '60rem' });
  const creating = createPanel();
  frame.main.append(
    h('p', { text: 'Everything in Retroscope happens inside a team. Start one, or join the team that invited you.', style: { ...MUTED, 'font-size': '1.0625rem' } }),
    h('div', {
      style: { display: 'grid', 'grid-template-columns': 'repeat(auto-fit, minmax(min(20rem, 100%), 1fr))', gap: '1.5rem', 'align-items': 'start' },
    }, creating.el, joinPanel()));

  function render() {
    const me = currentMe();
    if (me?.user) {
      frame.setTitle(me.teams?.length ? 'Create or join a team' : `Welcome, ${me.user.nickname || me.principal.name}`);
      creating.render(me.user);
    }
  }

  // Verifying happens in another tab (the emailed link), so coming back here checks again.
  const onFocus = () => {
    if (currentUser()?.emailVerified === false) {
      refreshMe().catch(() => {});
    }
  };
  const stop = session.select((s) => s.me, Object.is, render);
  window.addEventListener('focus', onFocus);

  return {
    update() {
      if (!currentUser()) {
        navigate(`/login?return=${encodeURIComponent('/teams')}`, { replace: true });
        return;
      }
      if (pendingInvite()) {
        navigate('/invite', { replace: true });
        return;
      }
      render();
    },
    unmount() {
      stop();
      window.removeEventListener('focus', onFocus);
      frame.destroy();
    },
  };
}
