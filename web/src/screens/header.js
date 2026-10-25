// The app header (UX18 decisions 9, 10 and 17). The team is the context: the logo (the team's home), the team
// switcher and the team's sections, then the settings gear (team admins and owners, on team pages), mentions
// and the account menu, which also holds the theme. Signed out, and on /teams, it is the logo and the product
// name, plus the account menu on /teams. Below 768 px the sections scroll in a second row and the account
// button opens the slide-out sheet instead. The poker screen keeps its own toolbar: there the header hides
// and docks mentions and the account menu into it. The menus load on first use, to keep the entry small.

import { h, replaceContent } from '../core/h.js';
import { initials } from '../core/fmt.js';
import { currentTheme } from '../core/theme.js';
import { createAvatar } from '../ui/avatar.js';
import { icon } from '../ui/icons.js';
import { toast } from '../ui/toast.js';
import { avatarUrl, homeTeam, navigate, session, setThemePreference, signOut } from './app.js';

const LOGO = typeof RS_ASSETS === 'object' ? RS_ASSETS['logo.svg'] ?? null : null;

/** The team page's sections in header order, by their `?tab=` ids. */
export const SECTIONS = [['poker', 'Poker'], ['retros', 'Retros'], ['actions', 'Action items'], ['kudos', 'Kudos'], ['members', 'Members']];

/** The section a team page's query selects: Poker by default; `boards` is the old name of Retros. */
export function sectionOf(query) {
  const tab = query?.get('tab') === 'boards' ? 'retros' : query?.get('tab');
  return SECTIONS.some(([id]) => id === tab) ? tab : 'poker';
}

export const teamPath = (teamId, section = 'poker') => `/teams/${encodeURIComponent(teamId)}${section === 'poker' ? '' : `?tab=${section}`}`;

export const ROLES = { owner: 'Owner', admin: 'Admin', member: 'Member' };
export const THEMES = [['light', 'Light'], ['dark', 'Dark'], ['system', 'System']];
const MANAGERS = new Set(['owner', 'admin']);
const TEAM_PAGES = new Set(['/teams/:id', '/teams/:id/settings']);
const POKER_ROUTES = new Set(['/poker/:code', '/teams/:tid/poker/:code']);

export const navLink = (href, text, props = {}) => h('a', { href, 'data-nav': '', ...props }, text);
const current = (link, on) => (on ? link.setAttribute('aria-current', 'page') : link.removeAttribute('aria-current'));

/** The user's teams by name, ignoring case: the switcher's order. */
export const byName = (teams) => [...(teams ?? [])].sort((a, b) => a.name.localeCompare(b.name, undefined, { sensitivity: 'base' }));

/** A team's initials on a tint chosen by its id, so teams tell apart at a glance. */
export function teamBadge(team) {
  const tint = [...team.id].reduce((sum, c) => sum + c.charCodeAt(0), 0) % 5;
  return h('span', { class: 'app-team-badge', 'aria-hidden': 'true', dataset: { tint }, text: initials(team.name) });
}

export function signOutNow(to) {
  signOut({ to }).catch(() => toast('Signing out failed. Try again.', { variant: 'error', duration: 5000 }));
}

/** Opens a menu from `button`. The menu code loads on first use; hovering the button fetches it early. */
function menuButton(button, { label, placement, items, onSelect }) {
  let menu = null;
  const load = () => (menu ??= import('../ui/menu.js').then(({ createMenu }) => createMenu({ items: [], label, placement, onSelect })));
  const show = (open) => load().then((m) => {
    if (m.isOpen) {
      m.close();
    } else if (open) {
      m.update({ items: items() });
      m.open(button);
    }
  }, () => toast('This menu could not be loaded. Check your connection.', { variant: 'error', duration: 5000 }));
  button.setAttribute('aria-haspopup', 'menu');
  button.setAttribute('aria-expanded', 'false');
  button.addEventListener('pointerenter', () => load().catch(() => {}), { once: true });
  button.addEventListener('click', () => show(true));
  button.addEventListener('keydown', (event) => {
    if (event.key === 'ArrowDown' && button.getAttribute('aria-expanded') !== 'true') {
      event.preventDefault();
      show(true);
    }
  });
  return () => menu?.then((m) => m.destroy(), () => {});
}

function onAccount(action) {
  if (action === 'signout') {
    signOutNow();
  } else if (action === 'account' || action === 'admin') {
    navigate(action === 'admin' ? '/admin/users' : '/account');
  } else {
    setThemePreference(action);
  }
}

function accountItems() {
  const me = session.get().me;
  return [
    { node: h('div', { class: 'menu-who', 'aria-hidden': 'true' }, createAvatar({ name: me.principal.name, src: avatarUrl(me.principal), size: 'sm' }).el, h('strong', { text: me.principal.name })) },
    'separator',
    { action: 'account', label: 'Account', icon: 'user' },
    me.user.isAdmin ? { action: 'admin', label: 'Admin', icon: 'shield' } : null,
    { group: 'Theme', className: 'menu-segmented', items: THEMES.map(([action, label]) => ({ action, label, checked: currentTheme() === action })) },
    'separator',
    { action: 'signout', label: 'Sign out', icon: 'log-out', destructive: true },
  ];
}

// Named from its content, "Account menu for <name>": the avatar's initials are visible, so a label that left them
// out would not hold the visible text (WCAG 2.5.3).
const accountButton = (className, avatar) => h('button', { type: 'button', class: ['btn btn-ghost btn-icon', className] },
  h('span', { class: 'sr-only', text: 'Account menu for ' }), avatar.el);

/** The mentions link and the account button with its menu: in the header, and docked in the poker toolbar. */
function createControls() {
  const bell = navLink('/mentions', null, { class: 'btn btn-ghost btn-icon app-mentions' });
  const avatar = createAvatar({ name: '', size: 'sm' });
  const account = accountButton('app-account', avatar);
  const destroy = menuButton(account, { label: 'Account', placement: 'bottom-end', items: accountItems, onSelect: onAccount });
  return {
    bell,
    account,
    avatar,
    update(me) {
      const unread = me.unreadMentions ?? 0;
      replaceContent(bell, icon('bell'), unread ? h('span', { class: 'badge app-mentions-badge', 'aria-hidden': 'true', text: String(unread) }) : null);
      // The count is in the name as it is shown (WCAG 2.5.3).
      bell.setAttribute('aria-label', unread ? `Mentions, ${unread} unread` : 'Mentions');
      avatar.update({ name: me.principal.name, src: avatarUrl(me.principal) });
    },
    destroy,
  };
}

export function createHeader() {
  let route = null;
  let params = {};
  let path = null;
  // What the page told the header (app.js setHeaderTeam, setHeaderTab); reset when the path changes.
  let pageTeam = null;
  let pageTab;
  let shown = null;
  let sheet = null;
  let dock = null;

  const brandName = h('span', { class: 'app-brand-name', text: 'Retroscope' });
  const brand = navLink('/', [LOGO ? h('img', { src: LOGO, alt: '', width: 28, height: 28 }) : null, brandName], { class: 'app-brand' });
  const switcher = h('button', { type: 'button', class: 'btn btn-outline app-team-switch', title: 'Switch team' });
  const count = h('span', { class: 'badge', hidden: true });
  const tabs = new Map(SECTIONS.map(([id, label]) => [id, navLink('/', [label, id === 'actions' ? count : null])]));
  const nav = h('nav', { class: 'app-tabs' }, [...tabs.values()]);
  const gear = navLink('/', icon('settings'), { class: 'btn btn-ghost btn-icon app-gear', 'aria-label': 'Team settings' });
  const controls = createControls();
  const burgerAvatar = createAvatar({ name: '', size: 'sm' });
  const burger = accountButton('app-burger', burgerAvatar);
  burger.setAttribute('aria-haspopup', 'dialog');
  const el = h('header', { class: 'app-header' }, brand, switcher, nav,
    h('div', { class: 'app-header-actions' }, gear, controls.bell, controls.account, burger));

  const stopTeams = menuButton(switcher, {
    label: 'Teams',
    items: () => [
      { group: 'Your teams', items: byName(shown.me.teams).map((t) => ({ action: t.id, label: t.name, detail: ROLES[t.role], icon: teamBadge(t), checked: t.id === shown.team?.id })) },
      'separator',
      { action: 'create', label: 'Create a team', icon: 'plus' },
    ],
    onSelect: (action) => navigate(action === 'create' ? '/teams' : teamPath(action)),
  });

  // The sheet is only for narrow screens, so its code loads on the first tap.
  burger.addEventListener('click', async () => {
    const { createSheet } = await import('./sheet.js');
    sheet ??= createSheet();
    sheet.open(shown);
  });

  // The team the header shows, its active section and whether the gear shows. A page's own team counts only
  // when the user is in it; otherwise the header shows the user's last team with no section active.
  function context(me) {
    const teams = me?.teams ?? [];
    const asked = pageTeam ?? params.id ?? params.tid ?? null;
    const own = asked === null ? null : teams.find((t) => t.id === asked) ?? null;
    const team = me && route !== '/teams' ? own ?? homeTeam(me) : null;
    let tab = pageTab;
    if (tab === undefined) {
      tab = asked !== null && own === null ? null
        : route === '/teams/:id' ? sectionOf(params.query)
          : route === '/retro/:code' ? 'retros'
            : POKER_ROUTES.has(route) ? 'poker' : null;
    }
    return { me, team, tab: team ? tab : null, gear: own !== null && TEAM_PAGES.has(route) && MANAGERS.has(own.role), settings: route === '/teams/:id/settings' };
  }

  function renderDock(me) {
    const toolbar = me && POKER_ROUTES.has(route) ? document.querySelector('.nt-toolbar-extras') : null;
    if (!toolbar) {
      dock?.destroy();
      dock?.el.remove();
      dock = null;
      return;
    }
    if (!dock) {
      dock = createControls();
      dock.el = h('div', { class: 'app-dock' }, dock.bell, dock.account);
    }
    if (dock.el.parentNode !== toolbar) {
      toolbar.append(dock.el);
    }
    dock.update(me);
  }

  function render() {
    const me = session.get().me?.user ? session.get().me : null;
    shown = context(me);
    const { team, tab } = shown;
    el.toggleAttribute('data-team', team !== null);
    brand.href = team ? teamPath(team.id) : '/';
    brand.setAttribute('aria-label', `${team?.name ?? 'Retroscope'} home`);
    switcher.hidden = team === null;
    nav.hidden = team === null;
    if (team) {
      // Named by the team, as shown; the menu button role says what it does.
      replaceContent(switcher, teamBadge(team), h('span', { class: 'app-team-name', text: team.name }), icon('chevron-down'));
      nav.setAttribute('aria-label', `${team.name} sections`);
      for (const [id, link] of tabs) {
        link.href = teamPath(team.id, id);
        current(link, id === tab);
      }
      count.textContent = String(team.openActions ?? 0);
      count.hidden = !team.openActions;
    }
    gear.hidden = !shown.gear;
    if (shown.gear) {
      gear.href = `${teamPath(team.id)}/settings`;
      current(gear, shown.settings);
    }
    controls.bell.hidden = !me || route === '/teams';
    controls.account.hidden = !me;
    burger.hidden = !me;
    if (me) {
      controls.update(me);
      burgerAvatar.update({ name: me.principal.name, src: avatarUrl(me.principal) });
    }
    document.documentElement.classList.toggle('app-has-tabs', team !== null && !el.hidden);
    renderDock(me);
    sheet?.update(shown);
  }

  const stop = session.select((s) => s.me, Object.is, render);

  return {
    el,
    /** Called after every navigation with the matched route pattern (null when nothing matched). */
    show(pattern, next = {}) {
      if (location.pathname !== path) {
        path = location.pathname;
        pageTeam = null;
        pageTab = undefined;
      }
      route = pattern;
      params = next;
      el.hidden = POKER_ROUTES.has(route);
      document.documentElement.classList.toggle('app-has-header', !el.hidden);
      render();
    },
    setTeam(teamId) {
      pageTeam = teamId;
      render();
    },
    setTab(tab) {
      pageTab = tab;
      render();
    },
    destroy() {
      stop();
      stopTeams();
      controls.destroy();
      dock?.destroy();
      sheet?.destroy();
      el.remove();
    },
  };
}
