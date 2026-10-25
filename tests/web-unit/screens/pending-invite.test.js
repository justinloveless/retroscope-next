import { test } from 'node:test';
import assert from 'node:assert/strict';
import { PENDING_INVITE_MS, clearPendingInvite, parseInvite, pendingInvite, savePendingInvite } from '../../../web/src/screens/pending-invite.js';

const TOKEN = 'Zq3k9X_vT-2mWpL8sB4nYc7dRf1hJ6uA0eGiKoNxQyM';

function memoryStorage() {
  const map = new Map();
  return {
    map,
    getItem: (key) => (map.has(key) ? map.get(key) : null),
    setItem: (key, value) => map.set(key, String(value)),
    removeItem: (key) => map.delete(key),
  };
}

test('parseInvite takes the token from an invite link, the older link form or the bare token', () => {
  assert.equal(parseInvite(`https://retroscope.example/invite#t=${TOKEN}`), TOKEN);
  assert.equal(parseInvite(`  https://retroscope.example/invite#t=${TOKEN}\n`), TOKEN, 'surrounding space is ignored');
  assert.equal(parseInvite(`https://retroscope.example/invite/#t=${TOKEN}`), TOKEN, 'a trailing slash');
  assert.equal(parseInvite(`/invite#t=${TOKEN}`), TOKEN, 'a path without the host');
  assert.equal(parseInvite(`https://retroscope.example/invite/${TOKEN}`), TOKEN, 'the older /invite/<token> form');
  assert.equal(parseInvite('https://retroscope.example/invite/legacy%2Etoken'), 'legacy.token', 'a percent-encoded token');
  assert.equal(parseInvite(TOKEN), TOKEN);
  assert.equal(parseInvite('a1b2c3d4-e5f6'), 'a1b2c3d4-e5f6', 'imported tokens keep the original app’s shape');
});

test('parseInvite refuses anything that is not an invite', () => {
  for (const text of [
    '',
    null,
    undefined,
    'abc',
    'two words',
    'https://retroscope.example/invite',
    'https://retroscope.example/invite#x=abcdefgh',
    'https://retroscope.example/retro/ABCDEFGH',
    'https://retroscope.example/invite/a/b',
    'https://retroscope.example/invite#t=has spaces',
    'https://retroscope.example/invite/%E0%A4%A',
    'javascript:alert(1)',
    `https://retroscope.example/invite#t=${'x'.repeat(201)}`,
  ]) {
    assert.equal(parseInvite(text), null, String(text));
  }
});

test('a saved invite waits until it expires, then is removed', () => {
  const storage = memoryStorage();
  const now = Date.UTC(2026, 9, 25, 9);
  savePendingInvite(TOKEN, now, storage);
  assert.deepEqual(JSON.parse(storage.map.get('rs.invite')), { t: TOKEN, exp: now + PENDING_INVITE_MS });
  assert.equal(pendingInvite(now, storage), TOKEN);
  assert.equal(pendingInvite(now + PENDING_INVITE_MS - 1, storage), TOKEN);
  assert.equal(pendingInvite(now + PENDING_INVITE_MS, storage), null);
  assert.equal(storage.map.has('rs.invite'), false, 'the expired invite is gone');
});

test('the pending invite waits a day, long enough for the email verification link', () => {
  assert.equal(PENDING_INVITE_MS, 24 * 60 * 60 * 1000);
});

test('clearPendingInvite forgets the invite', () => {
  const storage = memoryStorage();
  savePendingInvite(TOKEN, 0, storage);
  clearPendingInvite(storage);
  assert.equal(pendingInvite(0, storage), null);
});

test('an unreadable stored value counts as no invite and is removed', () => {
  for (const value of ['{not json', '"a token"', '{"t":"abcdefgh"}', '{"t":7,"exp":99}', 'null']) {
    const storage = memoryStorage();
    storage.setItem('rs.invite', value);
    assert.equal(pendingInvite(0, storage), null, value);
    assert.equal(storage.map.has('rs.invite'), false, value);
  }
});

test('blocked or missing storage never throws', () => {
  const blocked = {
    getItem() {
      throw new Error('SecurityError');
    },
    setItem() {
      throw new Error('QuotaExceededError');
    },
    removeItem() {
      throw new Error('SecurityError');
    },
  };
  savePendingInvite(TOKEN, 0, blocked);
  assert.equal(pendingInvite(0, blocked), null);
  clearPendingInvite(blocked);
  savePendingInvite(TOKEN, 0, null);
  assert.equal(pendingInvite(0, null), null);
  clearPendingInvite(null);
});
