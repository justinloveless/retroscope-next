// "You can't open this page" (UX18 decision 2): one screen for a page that does not exist and for one that
// belongs to a team the user is not on, so it never tells the two apart or names what was asked for. A
// member removed while a room is open sees "You no longer have access" instead. The router's not-found and
// the team, retro and poker screens show it; it draws its own <main>, so give it a container outside one.

import { h, replaceContent } from '../core/h.js';
import { icon } from '../ui/icons.js';
import { currentUser, homeTeam, refreshMe, setHeaderTab } from './app.js';
import { navLink, signOutNow, teamPath } from './header.js';

const COPY = {
  missing: ["You can't open this page", "It doesn't exist, or it belongs to a team you're not on. To get in, ask one of that team's admins to send you an invite."],
  removed: ['You no longer have access', "You were removed from this team, or it was deleted. To get back in, ask one of the team's admins for a new invite."],
};

// A room's kick codes (protocol.gen.js KICK_CODES) that end the user's access: the room is gone, or the user is
// no longer on its team. A tab over its room limit is not one of them: closing another room brings it back.
const GONE = new Set(['deleted', 'not_found']);
const REMOVED = new Set(['access_revoked', 'forbidden']);

/** The screen a room's kick shows ({removed}), or null when the room should say so itself. */
export function kickScreen(code) {
  if (GONE.has(code)) {
    return { removed: false };
  }
  return REMOVED.has(code) ? { removed: true } : null;
}

/** Replaces `root`'s content with the screen; `removed` when the user just lost access to the page's team. */
export function showNoAccess(root, { removed = false } = {}) {
  const [title, text] = removed ? COPY.removed : COPY.missing;
  const here = location.pathname + location.search;
  const actions = h('div', { class: 'no-access-actions' });

  function renderActions() {
    if (!currentUser()) {
      replaceContent(actions, navLink(`/login?return=${encodeURIComponent(here)}`, 'Sign in', { class: 'btn btn-primary btn-lg' }));
      return;
    }
    const team = homeTeam();
    replaceContent(actions,
      team
        ? navLink(teamPath(team.id), `Back to ${team.name}`, { class: 'btn btn-primary btn-lg' })
        : navLink('/teams', 'Create or join a team', { class: 'btn btn-primary btn-lg' }),
      h('button', {
        type: 'button',
        class: 'btn btn-link',
        on: { click: () => signOutNow(`/login?return=${encodeURIComponent(here)}`) },
      }, 'Use a different account'));
  }

  const main = h('main', { class: 'no-access' },
    h('section', { class: 'card no-access-card', 'aria-labelledby': 'no-access-title' },
      h('span', { class: 'no-access-icon' }, icon('lock')),
      h('h1', { id: 'no-access-title', tabindex: '-1', text: title }),
      h('p', { text }),
      actions));
  renderActions();
  root.replaceChildren(main);
  document.title = `${title} · Retroscope`;
  setHeaderTab(null);
  if (removed) {
    // The team is still in `me` until it is reloaded; "Back to" then names a team the user is still on.
    refreshMe().then(renderActions, () => {});
  }
  return main;
}
