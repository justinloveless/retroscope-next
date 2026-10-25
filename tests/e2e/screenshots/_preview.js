// Shared bits for the preview specs: the demo's fixed ids and codes, the routes worth opening, and a
// console watcher that fails a test on any page error, console error or CSP report.
import { expect } from '@playwright/test';

export const TEAM = '0d3e0000-0001-7000-8000-000000000001';
export const GUILD = '0d3e0000-0001-7000-8000-000000000002';
// A team that does not exist: a missing team and one the user is not on look the same (UX18 decision 2).
export const NO_TEAM = '0d3e0000-0001-7000-8000-0000000000ff';
export const DEMO_PID = '0d3e0000-0002-7000-8000-000000000001';
export const PRIYA_PID = '0d3e0000-0002-7000-8000-000000000002';
export const CODES = {
  thinking: 'SPR42THNK1',
  voting: 'CHKVTNG042',
  discussing: 'SPR41DSCSS',
  closed: 'SPR40CL5D1',
  teamPoker: 'PKR41CHKT1',
  crowdPoker: 'CRWDPKR012',
};

// What a page that answers "not found" logs, which the no-access screen expects.
export const NOT_FOUND = /the server responded with a status of 404/;

/**
 * Every route of web/src/routes.js with a demo path; `ready` is what shows once the page has drawn, and `allow`
 * lists console errors the page expects. '/' signed in opens the team used last, so it is the team route.
 */
export const ROUTES = [
  { route: 'welcome', path: '/?as=signed-out', ready: 'text=Planning poker and retros for your team' },
  { route: 'login', path: '/login?as=signed-out', ready: 'form' },
  { route: 'team', path: '/', ready: 'text=Latest session' },
  { route: 'create-or-join', path: '/teams', ready: 'text=Join a team' },
  { route: 'team-settings', path: `/teams/${TEAM}/settings`, ready: 'text=Board templates' },
  { route: 'poker', path: `/teams/${TEAM}/poker/${CODES.teamPoker}`, ready: '.nt-table' },
  { route: 'retro', path: `/retro/${CODES.thinking}`, ready: '.retro-card' },
  { route: 'mentions', path: '/mentions', ready: 'text=Sprint 42 retro' },
  { route: 'account', path: '/account', ready: 'text=Sessions' },
  { route: 'invite', path: '/invite#t=demo-team-link', ready: 'text=Join Demo Team' },
  { route: 'no-access', path: `/teams/${NO_TEAM}`, ready: "text=You can't open this page", allow: [NOT_FOUND] },
  { route: 'admin', path: '/admin/users', ready: 'role=searchbox', act: search('Priya Raman') },
];

/** Admin lists start empty: run the search and wait for `expected`. */
export function search(expected) {
  return async (page) => {
    await page.getByRole('button', { name: 'Search' }).click();
    await expect(page.getByText(expected).first()).toBeVisible();
  };
}

/** Collects what would show up red in the browser console, but for the errors `allow` expects. */
export function watchConsole(page, { allow = [] } = {}) {
  const problems = [];
  page.on('pageerror', (error) => problems.push(`page error: ${error.message}`));
  page.on('console', (message) => {
    if (message.type() === 'error' && !allow.some((pattern) => pattern.test(message.text()))) {
      problems.push(`console error: ${message.text()}`);
    }
  });
  return problems;
}

/** The demo server's own record of thrown scripts and refused client frames. */
export async function expectHealthyServer(request) {
  const state = await (await request.get('/__demo/state')).json();
  expect(state.problems, 'problems the demo server recorded').toEqual([]);
}

/** The other host name of the same server: a second cookie jar in one browser. */
export function otherHost(baseURL) {
  const url = new URL(baseURL);
  url.hostname = url.hostname === 'localhost' ? '127.0.0.1' : 'localhost';
  return url.origin;
}
