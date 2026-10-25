import { test } from 'node:test';
import assert from 'node:assert/strict';
import { KICK_CODES } from '../../../web/src/net/protocol.gen.js';
import { isLimitKick, kickScreen } from '../../../web/src/screens/no-access.js';

test('a room kick shows no access when the room is gone or the user lost it, and not for a full tab', () => {
  assert.deepEqual(kickScreen('deleted'), { removed: false });
  assert.deepEqual(kickScreen('not_found'), { removed: false });
  assert.deepEqual(kickScreen('access_revoked'), { removed: true });
  assert.deepEqual(kickScreen('forbidden'), { removed: true });
  assert.equal(kickScreen('too_many_rooms'), null);
  assert.equal(kickScreen(null), null);
});

test('a room open in too many of the user\'s tabs is a limit, like too many rooms in one tab, not lost access', () => {
  assert.equal(kickScreen('too_many_connections'), null);
  assert.equal(isLimitKick('too_many_connections'), true);
  assert.equal(isLimitKick('too_many_rooms'), true);
  for (const code of ['access_revoked', 'forbidden', 'deleted', 'not_found', null, undefined]) {
    assert.equal(isLimitKick(code), false, String(code));
  }
});

test('every kick code is either a no-access screen or a limit the page offers to try again, never both', () => {
  for (const code of KICK_CODES) {
    assert.notEqual(kickScreen(code) !== null, isLimitKick(code), code);
  }
});
