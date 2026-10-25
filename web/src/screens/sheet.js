// The header's slide-out sheet for narrow screens (UX18 decision 17): switching teams, "Create a team", the
// team's settings (for its admins), the theme and the account items. Loaded on the first tap of the
// account button, which opens it instead of the account menu below 768 px.

import { h, replaceContent } from '../core/h.js';
import { currentTheme } from '../core/theme.js';
import { createAvatar } from '../ui/avatar.js';
import { createDialog } from '../ui/dialog.js';
import { icon } from '../ui/icons.js';
import { avatarUrl, setThemePreference } from './app.js';
import { ROLES, THEMES, byName, navLink, signOutNow, teamBadge, teamPath } from './header.js';

export function createSheet() {
  const body = h('div', { class: 'app-sheet-body' });
  const dialog = createDialog({ title: 'Menu', content: body, className: 'app-sheet' });
  let shown = null;

  body.addEventListener('click', (event) => {
    if (event.target.closest('a[href]')) {
      dialog.close();
    }
  });

  const section = (label, ...children) => h('section', { class: 'app-sheet-section', 'aria-label': label }, h('h3', { text: label }), ...children);

  function render() {
    const { me, team, gear } = shown;
    if (!me) {
      dialog.close();
      return;
    }
    const themeButtons = THEMES.map(([id, label]) => h('button', {
      type: 'button',
      class: 'btn btn-outline btn-sm',
      'aria-pressed': String(currentTheme() === id),
      on: {
        click() {
          setThemePreference(id);
          render();
        },
      },
    }, label));
    replaceContent(body,
      h('div', { class: 'app-sheet-who' }, createAvatar({ name: me.principal.name, src: avatarUrl(me.principal), size: 'md' }).el, h('strong', { text: me.principal.name })),
      section('Your teams', h('div', { class: 'app-sheet-links' },
        byName(me.teams).map((t) => navLink(teamPath(t.id), [
          teamBadge(t),
          h('span', { class: 'app-sheet-team' }, h('span', { text: t.name }), h('small', { text: ROLES[t.role] ?? '' })),
          t.id === team?.id ? icon('check') : null,
        ], { 'aria-current': t.id === team?.id ? 'true' : null })),
        navLink('/teams', [icon('plus'), 'Create a team']),
        gear ? navLink(`${teamPath(team.id)}/settings`, [icon('settings'), 'Team settings']) : null)),
      section('Theme', h('div', { class: 'app-sheet-themes', role: 'group', 'aria-label': 'Theme' }, themeButtons)),
      h('div', { class: 'app-sheet-links' },
        navLink('/account', [icon('user'), 'Account']),
        me.user.isAdmin ? navLink('/admin/users', [icon('shield'), 'Admin']) : null,
        h('button', {
          type: 'button',
          class: 'btn btn-outline app-sheet-signout',
          on: {
            click() {
              dialog.close();
              signOutNow();
            },
          },
        }, icon('log-out'), 'Sign out')));
  }

  return {
    open(next) {
      shown = next;
      render();
      dialog.open();
    },
    update(next) {
      shown = next;
      if (dialog.el.open) {
        render();
      }
    },
    destroy: dialog.destroy,
  };
}
