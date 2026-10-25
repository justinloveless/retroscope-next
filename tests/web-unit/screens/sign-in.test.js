import { beforeEach, test } from 'node:test';
import assert from 'node:assert/strict';
import { installDom } from '../core/_dom.js';
import { savePendingInvite } from '../../../web/src/screens/pending-invite.js';
import { signInCard } from '../../../web/src/screens/sign-in.js';

const stored = new Map();
globalThis.localStorage = {
  getItem: (key) => (stored.has(key) ? stored.get(key) : null),
  setItem: (key, value) => stored.set(key, String(value)),
  removeItem: (key) => stored.delete(key),
};

beforeEach(() => {
  stored.clear();
  installDom();
});

/** The href of each OIDC button on a sign-in card built for `search`. */
function oidcLinks(search = '') {
  const card = signInCard(new URLSearchParams(search));
  const links = Object.fromEntries(card.el.querySelectorAll('a')
    .filter((a) => a.getAttribute('href')?.startsWith('/auth/oidc/'))
    .map((a) => [a.textContent, a.getAttribute('href')]));
  card.destroy();
  return links;
}

test('without a ?return= or a pending invite, Google comes back to /', () => {
  assert.deepEqual(oidcLinks(), {
    'Continue with Google': '/auth/oidc/google/start?return=%2F',
  });
});

// OIDC comes back by a full page load, and the server's '/' goes to the team without seeing the invite.
test('without a ?return=, a pending invite is where Google comes back to', () => {
  savePendingInvite('invite-token-1');
  assert.deepEqual(oidcLinks(), {
    'Continue with Google': '/auth/oidc/google/start?return=%2Finvite',
  });
  assert.deepEqual(Object.values(oidcLinks('?error=account_exists')), [
    '/auth/oidc/google/start?return=%2Finvite',
  ], 'a failed OIDC attempt comes back to /login?error= with no return');
});

test('an explicit ?return= wins over a pending invite, and an unsafe one counts as none', () => {
  savePendingInvite('invite-token-1');
  assert.equal(oidcLinks(`?return=${encodeURIComponent('/teams/t1?tab=retros')}`)['Continue with Google'],
    `/auth/oidc/google/start?return=${encodeURIComponent('/teams/t1?tab=retros')}`);
  assert.equal(oidcLinks('?return=%2F')['Continue with Google'], '/auth/oidc/google/start?return=%2F');
  assert.equal(oidcLinks(`?return=${encodeURIComponent('//evil.example')}`)['Continue with Google'], '/auth/oidc/google/start?return=%2Finvite');
});

test('an expired invite no longer counts', () => {
  savePendingInvite('invite-token-1', Date.now() - 25 * 60 * 60 * 1000);
  assert.equal(oidcLinks()['Continue with Google'], '/auth/oidc/google/start?return=%2F');
});
