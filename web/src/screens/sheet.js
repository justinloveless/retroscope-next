// The header's slide-out menu for narrow screens: the main links, the user's teams, the theme and the
// account items, stacked. Loaded on the first tap of the header's menu button.

import { h, replaceContent } from '../core/h.js';
import { currentTheme } from '../core/theme.js';
import { createAvatar } from '../ui/avatar.js';
import { createDialog } from '../ui/dialog.js';
import { icon } from '../ui/icons.js';
import { avatarUrl, setThemePreference } from './app.js';
import { navLink } from './header.js';

export function createSheet({ themes, here, signOut }) {
  const body = h('div', { class: 'app-sheet-body' });
  const dialog = createDialog({ title: 'Menu', content: body, className: 'app-sheet' });
  let me = null;

  body.addEventListener('click', (event) => {
    if (event.target.closest('a[href]')) {
      dialog.close();
    }
  });

  const section = (label, ...children) => h('section', { class: 'app-sheet-section', 'aria-label': label }, h('h3', { text: label }), ...children);

  function render() {
    const user = me?.user;
    const themeButtons = themes.map((t) => h('button', {
      type: 'button',
      class: 'btn btn-outline btn-sm',
      'aria-pressed': String(currentTheme() === t.action),
      on: {
        click() {
          setThemePreference(t.action);
          render();
        },
      },
    }, icon(t.icon), t.label));
    replaceContent(body,
      me ? h('div', { class: 'app-sheet-who' }, createAvatar({ name: me.principal.name, src: avatarUrl(me.principal), size: 'md' }).el, h('strong', { text: me.principal.name })) : null,
      user
        ? h('nav', { class: 'app-sheet-links', 'aria-label': 'Main' },
          navLink('/dashboard', 'Dashboard'),
          navLink('/teams', 'Teams'))
        : null,
      user && me.teams?.length
        ? section('Your teams', h('div', { class: 'app-sheet-links' }, me.teams.map((t) => navLink(`/teams/${t.id}`, t.name))))
        : null,
      section('Theme', h('div', { class: 'app-sheet-themes', role: 'group', 'aria-label': 'Theme' }, themeButtons)),
      user
        ? h('div', { class: 'app-sheet-links' },
          navLink('/account', 'Account'),
          user.isAdmin ? navLink('/admin/users', 'Admin') : null,
          h('button', {
            type: 'button',
            class: 'btn btn-outline app-sheet-signout',
            on: {
              click() {
                dialog.close();
                signOut();
              },
            },
          }, icon('log-out'), 'Sign out'))
        : navLink(`/login?return=${encodeURIComponent(here())}`, 'Sign in', { class: 'btn btn-primary' }));
  }

  return {
    open(next) {
      me = next;
      render();
      dialog.open();
    },
    update(next) {
      me = next;
      if (dialog.el.open) {
        render();
      }
    },
    destroy: dialog.destroy,
  };
}
