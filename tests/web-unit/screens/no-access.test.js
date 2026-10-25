import { test } from 'node:test';
import assert from 'node:assert/strict';
import { kickScreen } from '../../../web/src/screens/no-access.js';

test('a room kick shows no access when the room is gone or the user lost it, and not for a full tab', () => {
  assert.deepEqual(kickScreen('deleted'), { removed: false });
  assert.deepEqual(kickScreen('not_found'), { removed: false });
  assert.deepEqual(kickScreen('access_revoked'), { removed: true });
  assert.deepEqual(kickScreen('forbidden'), { removed: true });
  assert.equal(kickScreen('too_many_rooms'), null);
  assert.equal(kickScreen(null), null);
});
