// The persistent app header, matching the original sticky AppHeader: brand, team switcher, main links, a centre
// slot, mentions, the theme menu and the account menu. It is sticky on wide screens; below 768 px it is
// fixed and the links, teams and account items move into a slide-out sheet. The poker screen keeps its
// own Neotro toolbar, so there the header hides and docks only the theme and account buttons into it.

import { h, replaceContent } from '../core/h.js';
import { currentTheme } from '../core/theme.js';
import { createAvatar } from '../ui/avatar.js';
import { icon } from '../ui/icons.js';
import { createMenu } from '../ui/menu.js';
import { toast } from '../ui/toast.js';
import { avatarUrl, navigate, session, setThemePreference, signOut } from './app.js';

const LOGO = typeof RS_ASSETS === 'object' ? RS_ASSETS['logo.svg'] ?? null : null;
const TITLE_SUFFIX = ' · Retroscope';

const THEMES = [
  { action: 'light', label: 'Light', icon: 'sun' },
  { action: 'dark', label: 'Dark', icon: 'moon' },
  { action: 'system', label: 'System', icon: 'palette' },
];

// Screens that are, or draw, their own sign-in prompt for signed-out visitors.
const OWN_SIGN_IN = new Set(['/login', '/auth/*', '/teams/:id', '/teams/:id/settings', '/retro/:code']);
const POKER_ROUTES = new Set(['/poker/:code', '/teams/:tid/poker/:code']);

export const navLink = (href, text, props = {}) => h('a', { href, 'data-nav': '', ...props }, text);
const here = () => location.pathname + location.search;

function signOutNow() {
  signOut().catch(() => toast('Signing out failed. Try again.', { variant: 'error', duration: 5000 }));
}

function accountItems(me) {
  if (me?.user) {
    return [
      { action: 'account', label: 'Account', icon: 'user' },
      me.user.isAdmin ? { action: 'admin', label: 'Admin', icon: 'shield' } : null,
      'separator',
      { action: 'signout', label: 'Sign out', icon: 'log-out', destructive: true },
    ].filter(Boolean);
  }
  return [{ action: 'signin', label: 'Sign in', icon: 'log-in' }];
}

function onAccountAction(action) {
  if (action === 'signout') {
    signOutNow();
  } else if (action === 'signin') {
    navigate(`/login?return=${encodeURIComponent(here())}`);
  } else {
    navigate(action === 'admin' ? '/admin/users' : '/account');
  }
}

function themeIcon() {
  const resolved = document.documentElement.dataset.theme === 'dark' ? 'moon' : 'sun';
  return icon(currentTheme() === 'system' ? 'palette' : resolved);
}

/** A theme button and an account button with their menus; `destroy()` releases both menus. */
function createControls(me) {
  const themeButton = h('button', { type: 'button', class: 'btn btn-ghost btn-icon app-theme', 'aria-label': 'Theme' }, themeIcon());
  const themeMenu = createMenu({ items: THEMES, label: 'Theme', placement: 'bottom-end', onSelect: setThemePreference });
  themeMenu.attach(themeButton);
  const menus = [themeMenu];
  let account = null;
  if (me) {
    account = h('button', {
      type: 'button',
      class: 'btn btn-ghost btn-sm app-account',
      'aria-label': `Account menu for ${me.principal.name}`,
    }, createAvatar({ name: me.principal.name, src: avatarUrl(me.principal), size: 'sm' }).el, icon('chevron-down'));
    const menu = createMenu({ items: accountItems(me), label: 'Account', placement: 'bottom-end', onSelect: onAccountAction });
    menu.attach(account);
    menus.push(menu);
  }
  return {
    themeButton,
    nodes: [themeButton, account],
    destroy() {
      for (const menu of menus) {
        menu.destroy();
      }
    },
  };
}

export function createHeader() {
  let route = null;
  let teamId = null;
  let controls = null;
  let dock = null;
  let pinnedCenter = false;

  const brand = h('a', { href: '/', 'data-nav': '', class: 'app-brand', 'aria-label': 'Retroscope home' },
    LOGO ? h('img', { src: LOGO, alt: '', width: 28, height: 28 }) : null,
    h('span', { class: 'app-brand-name', text: 'Retroscope' }));
  const teamButton = h('button', { type: 'button', class: 'btn btn-ghost btn-sm app-team-switch' });
  const teamMenu = createMenu({
    items: [],
    label: 'Teams',
    onSelect: (action) => navigate(action === 'all' ? '/teams' : `/teams/${action}`),
  });
  teamMenu.attach(teamButton);
  const nav = h('nav', { class: 'app-nav', 'aria-label': 'Main' });
  const center = h('div', { class: 'app-header-center' });
  const end = h('div', { class: 'app-header-end' });
  const burger = h('button', { type: 'button', class: 'btn btn-ghost btn-icon app-burger', 'aria-label': 'Menu', 'aria-haspopup': 'dialog' }, icon('menu'));
  let sheet = null;
  const el = h('header', { class: 'app-header' },
    h('div', { class: 'app-header-start' }, brand, teamButton, nav),
    center,
    h('div', { class: 'app-header-actions' }, end, burger));

  // The sheet is only for narrow screens, so its code loads on the first tap.
  burger.addEventListener('click', async () => {
    const { createSheet } = await import('./sheet.js');
    sheet ??= createSheet({ themes: THEMES, here, signOut: signOutNow });
    sheet.open(session.get().me);
  });

  function teamName(me) {
    return me?.teams?.find((t) => t.id === teamId)?.name ?? null;
  }

  function renderDock(me) {
    dock?.controls.destroy();
    dock?.el.remove();
    dock = null;
    const toolbar = POKER_ROUTES.has(route) ? document.querySelector('.nt-toolbar-extras') : null;
    if (toolbar) {
      const docked = createControls(me);
      dock = { controls: docked, el: h('div', { class: 'app-dock' }, docked.nodes) };
      toolbar.append(dock.el);
    }
  }

  function render(me) {
    const user = me?.user;
    brand.href = user ? '/dashboard' : '/';
    teamButton.hidden = !user;
    if (user) {
      const current = teamName(me);
      replaceContent(teamButton, icon('users'), h('span', { text: current ?? 'Teams' }), icon('chevron-down'));
      teamButton.setAttribute('aria-label', current ? `Switch team (${current})` : 'Switch team');
      teamMenu.update({
        items: [
          ...(me.teams ?? []).map((t) => ({ action: t.id, label: t.name, icon: t.id === teamId ? 'check' : 'users' })),
          ...(me.teams?.length ? ['separator'] : []),
          { action: 'all', label: 'All teams', icon: 'layout-dashboard' },
        ],
      });
    }
    replaceContent(nav, user
      ? [
        navLink('/dashboard', 'Dashboard', { class: 'btn btn-ghost btn-sm' }),
        navLink('/teams', 'Teams', { class: 'btn btn-ghost btn-sm' }),
      ]
      : []);
    const mentions = user
      ? navLink('/mentions', [
        icon('bell'),
        me.unreadMentions ? h('span', { class: 'badge app-mentions-badge', 'aria-hidden': 'true', text: String(me.unreadMentions) }) : null,
      ], {
        class: 'btn btn-ghost btn-icon app-mentions',
        'aria-label': me.unreadMentions ? `Mentions (${me.unreadMentions} unread)` : 'Mentions',
      })
      : null;
    controls?.destroy();
    controls = createControls(me);
    const signIn = !me && !OWN_SIGN_IN.has(route)
      ? navLink(`/login?return=${encodeURIComponent(here())}`, 'Sign in', { class: 'btn btn-outline btn-sm' })
      : null;
    const guestName = me && !user ? h('span', { class: 'app-guest', text: me.principal.name }) : null;
    replaceContent(end, guestName, mentions, controls.nodes, signIn);
    renderDock(me);
    sheet?.update(me);
  }

  // Room screens set document.title to "<name> · Retroscope"; on a board that name is the centre title
  // until the screen fills the slot itself through setCenter().
  function mirrorTitle() {
    if (pinnedCenter) {
      return;
    }
    const title = route === '/retro/:code' && document.title.endsWith(TITLE_SUFFIX)
      ? document.title.slice(0, -TITLE_SUFFIX.length)
      : '';
    if (center.textContent !== title) {
      replaceContent(center, title ? h('span', { class: 'app-header-title', text: title }) : null);
    }
  }

  const titleWatch = new MutationObserver(mirrorTitle);
  titleWatch.observe(document.head, { childList: true, subtree: true, characterData: true });
  const themeWatch = new MutationObserver(() => {
    for (const button of el.ownerDocument.querySelectorAll('.app-theme')) {
      replaceContent(button, themeIcon());
    }
  });
  themeWatch.observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] });
  const stop = session.select((s) => s.me, Object.is, render);
  render(session.get().me);

  return {
    el,
    /** Called after every navigation with the matched route pattern (null when nothing matched). */
    show(pattern, params = {}) {
      const changed = pattern !== route;
      route = pattern;
      teamId = params.id ?? params.tid ?? null;
      el.hidden = POKER_ROUTES.has(route);
      document.documentElement.classList.toggle('app-has-header', !el.hidden);
      if (changed) {
        pinnedCenter = false;
        center.replaceChildren();
      }
      render(session.get().me);
      mirrorTitle();
    },
    /** Puts `nodes` in the centre slot (null restores the page title); cleared on the next route. */
    setCenter(...nodes) {
      pinnedCenter = nodes.length > 0 && nodes[0] !== null;
      replaceContent(center, ...nodes);
      mirrorTitle();
    },
    destroy() {
      stop();
      titleWatch.disconnect();
      themeWatch.disconnect();
      controls?.destroy();
      dock?.controls.destroy();
      teamMenu.destroy();
      sheet?.destroy();
      el.remove();
    },
  };
}
