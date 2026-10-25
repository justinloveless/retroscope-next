// '/' for a signed-out visitor (UX18 decision 15): a short product page with sign-in and sign-up. A signed-in
// visit never shows it. On a full page load the server sends it to the user's team or the create-or-join
// screen; in-app, the router does the same but lets a pending invite win (app.js homePath). Without guests or
// quick sessions there is nothing to start before signing in.

import { h } from '../core/h.js';
import { icon } from '../ui/icons.js';
import { currentUser, homePath, navigate } from './app.js';
import { MUTED, row, stack } from './frame.js';
import { signInCard } from './sign-in.js';

function feature(name, tint, text) {
  return h('p', { style: { ...row('0.75rem'), 'flex-wrap': 'nowrap' } },
    h('span', { style: { display: 'inline-flex', padding: '0.5rem', 'border-radius': 'var(--radius)', background: tint, color: '#fff' } }, icon(name)),
    text);
}

function hero() {
  return h('div', { style: { ...stack('1.25rem'), 'padding-top': '1.5rem' } },
    h('h1', { text: 'Planning poker and retros for your team', style: { 'font-size': 'clamp(2rem, 5vw, 2.75rem)', 'line-height': '1.1', 'font-weight': '750' } }),
    h('p', {
      text: 'Point your Jira backlog together, run retros, and keep the action items moving. Everything lives inside your team.',
      style: { ...MUTED, 'font-size': '1.125rem', 'line-height': '1.55' },
    }),
    h('div', { style: { ...stack('0.75rem'), 'margin-top': '0.75rem' } },
      feature('spade', 'var(--felt)', 'Planning poker with the Jira refinement flow'),
      feature('folder-kanban', 'var(--indigo)', 'Retro boards with kudos and team action items')),
    h('p', {
      text: 'Got an invite? Open the link from your email. You will sign in or create an account, then join the team.',
      style: { ...MUTED, 'margin-top': '1.25rem', 'max-width': '28rem' },
    }));
}

export function mount(root) {
  let signIn = null;

  return {
    update(params) {
      if (currentUser()) {
        navigate(homePath(), { replace: true });
        return;
      }
      if (signIn) {
        return;
      }
      signIn = signInCard(params.query);
      document.title = 'Retroscope';
      root.replaceChildren(h('main', {
        style: {
          display: 'grid',
          'grid-template-columns': 'repeat(auto-fit, minmax(min(22rem, 100%), 1fr))',
          gap: '3rem 4.5rem',
          'align-items': 'start',
          width: '100%',
          'max-width': '65rem',
          margin: '0 auto',
          padding: 'clamp(2rem, 6vw, 4.5rem) 1rem 3rem',
        },
      }, hero(), signIn.el));
    },
    unmount() {
      signIn?.destroy();
      root.replaceChildren();
    },
  };
}
