import { test, beforeEach } from 'node:test';
import assert from 'node:assert/strict';
import { currentMe, homePath, homeTeam, patchTeam, redirectFor, safeReturn, session, setMe } from '../../../web/src/screens/app.js';

const A = { id: '01900000-0000-7000-8000-0000000000a1', name: 'Apps', role: 'owner', openActions: 2 };
const B = { id: '01900000-0000-7000-8000-0000000000b2', name: 'billing', role: 'member', openActions: 0 };
const user = (extra = {}) => ({
  principal: { id: 'p1', kind: 'user', name: 'Ada' },
  user: { email: 'ada@example.com', emailVerified: true },
  teams: [A, B],
  lastTeamId: B.id,
  unreadMentions: 0,
  ...extra,
});

const stored = new Map();
globalThis.localStorage = {
  getItem: (key) => (stored.has(key) ? stored.get(key) : null),
  setItem: (key, value) => stored.set(key, String(value)),
  removeItem: (key) => stored.delete(key),
};

beforeEach(() => {
  stored.clear();
  setMe(null);
});

test('homeTeam is the last team used while the user is still in it, else the first', () => {
  assert.equal(homeTeam(user()), B);
  assert.equal(homeTeam(user({ lastTeamId: 'gone' })), A);
  assert.equal(homeTeam(user({ lastTeamId: null })), A);
  assert.equal(homeTeam(user({ teams: [] })), null);
  assert.equal(homeTeam(null), null);
});

test('homePath sends a signed-in user to a pending invite, then the last team, then the first, then /teams', () => {
  assert.equal(homePath(user(), 'token'), '/invite');
  assert.equal(homePath(user(), null), `/teams/${B.id}`);
  assert.equal(homePath(user({ lastTeamId: null }), null), `/teams/${A.id}`);
  assert.equal(homePath(user({ teams: [], lastTeamId: null }), null), '/teams');
  assert.equal(homePath(user({ teams: [{ id: 'a/b', name: 'Odd' }], lastTeamId: null }), null), '/teams/a%2Fb');
});

test('signed out, / is the welcome page, even with an invite waiting', () => {
  assert.equal(homePath(null, 'token'), '/');
  assert.equal(homePath({ principal: { id: 'g' }, teams: [] }, null), '/', 'a body without a user is not signed in');
});

test('homePath reads the pending invite from storage by default', () => {
  assert.equal(homePath(user()), `/teams/${B.id}`);
  stored.set('rs.invite', JSON.stringify({ t: 'abcdefgh', exp: Date.now() + 60_000 }));
  assert.equal(homePath(user()), '/invite');
  stored.set('rs.invite', JSON.stringify({ t: 'abcdefgh', exp: Date.now() - 1 }));
  assert.equal(homePath(user()), `/teams/${B.id}`, 'an expired invite no longer counts');
});

const at = (href) => new URL(href, 'https://app.test');

test('signed out, every route but /, /login, /auth/* and /invite goes to sign in first, and comes back', () => {
  for (const route of ['/', '/login', '/auth/*', '/invite']) {
    assert.equal(redirectFor(at('/'), route, null), null, route);
  }
  assert.equal(redirectFor(at('/teams/t1?tab=retros'), '/teams/:id', null), `/login?return=${encodeURIComponent('/teams/t1?tab=retros')}`);
  assert.equal(redirectFor(at('/retro/ABC#x'), '/retro/:code', null), `/login?return=${encodeURIComponent('/retro/ABC')}`, 'never the fragment');
  for (const [path, route] of [['/teams', '/teams'], ['/account', '/account'], ['/mentions', '/mentions'], ['/admin/users', '/admin/*'], ['/poker/X', '/poker/:code'], ['/teams/t/poker/X', '/teams/:tid/poker/:code']]) {
    assert.equal(redirectFor(at(path), route, null), `/login?return=${encodeURIComponent(path)}`, path);
  }
  assert.equal(redirectFor(at('/nowhere'), null, null), null, 'unknown paths are the not-found screen’s');
});

test('signed in, / finds the team and every other route shows as asked', () => {
  assert.equal(redirectFor(at('/'), '/', user()), `/teams/${B.id}`);
  assert.equal(redirectFor(at('/?utm=x'), '/', user({ teams: [] })), '/teams');
  for (const route of ['/login', '/invite', '/teams', '/teams/:id', '/retro/:code', '/account', null]) {
    assert.equal(redirectFor(at('/x'), route, user()), null, String(route));
  }
});

test('redirectFor reads the session by default', () => {
  assert.equal(redirectFor(at('/account'), '/account'), '/login?return=%2Faccount');
  setMe(user());
  assert.equal(redirectFor(at('/account'), '/account'), null);
});

test('safeReturn keeps same-origin paths and falls back to /', () => {
  assert.equal(safeReturn('/teams/t1?tab=kudos'), '/teams/t1?tab=kudos');
  for (const bad of [null, '', 'https://evil.test/', '//evil.test', '/\\evil.test', '/a\\b', '/a\nb']) {
    assert.equal(safeReturn(bad), '/', String(bad));
  }
  assert.equal(safeReturn('nope', '/account'), '/account');
});

test('patchTeam updates one of the user’s teams in place and ignores what did not change', () => {
  setMe(user());
  const before = currentMe();
  patchTeam(A.id, { openActions: 2, name: undefined, role: 'owner' });
  assert.equal(currentMe(), before, 'nothing changed, so me is the same object');

  let notified = 0;
  const stop = session.select((s) => s.me, Object.is, () => notified++);
  patchTeam(A.id, { openActions: 5, name: 'Apps team' });
  session.flush();
  stop();
  assert.equal(notified, 1);
  assert.deepEqual(currentMe().teams[0], { ...A, openActions: 5, name: 'Apps team' });
  assert.equal(currentMe().teams[1], B);

  const after = currentMe();
  patchTeam('not-mine', { name: 'X' });
  assert.equal(currentMe(), after, 'a team the user is not in is never added');
  setMe(null);
  patchTeam(A.id, { name: 'X' });
  assert.equal(currentMe(), null);
});
