import { after, beforeEach, test } from 'node:test';
import assert from 'node:assert/strict';
import { installDom } from '../core/_dom.js';
import { revealHeader, setHeader, setHeaderTeam, setMe } from '../../../web/src/screens/app.js';
import { createHeader } from '../../../web/src/screens/header.js';
import { kickScreen } from '../../../web/src/screens/no-access.js';

const A = { id: '01900000-0000-7000-8000-0000000000a1', name: 'Apps', role: 'owner', openActions: 2 };
const B = { id: '01900000-0000-7000-8000-0000000000b2', name: 'Billing', role: 'member', openActions: 0 };
const me = {
  principal: { id: 'p1', kind: 'user', name: 'Ada' },
  user: { email: 'ada@example.com', emailVerified: true, isAdmin: false },
  teams: [A, B],
  lastTeamId: A.id,
  unreadMentions: 0,
};

let header = null;

beforeEach(() => {
  header?.destroy();
  installDom();
  globalThis.requestAnimationFrame = (fn) => setTimeout(fn, 0);
  globalThis.window = { addEventListener() {}, removeEventListener() {} };
  setMe(me);
  header = createHeader();
  setHeader(header);
  document.body.append(header.el);
});

after(() => {
  header?.destroy();
  setHeader(null);
});

function visit(path, route, params = {}) {
  globalThis.location = new URL(`https://app.test${path}`);
  header.show(route, params);
}

const tabs = () => header.el.querySelector('nav').querySelectorAll('a');
const tab = (label) => tabs().find((a) => a.firstChild.textContent === label);

test('the poker screen hides the header until it shows the no-access screen, and the next page hides it again', () => {
  visit('/poker/ABC', '/poker/:code', { code: 'ABC' });
  assert.equal(header.el.hidden, true);
  revealHeader();
  assert.equal(header.el.hidden, false);
  assert.ok(document.documentElement.classList.contains('app-has-header'));
  visit('/poker/DEF', '/poker/:code', { code: 'DEF' });
  assert.equal(header.el.hidden, true);
});

test('a board shows its own team, from its snapshot, with Retros active', () => {
  visit('/retro/XYZ', '/retro/:code', { code: 'XYZ' });
  setHeaderTeam(B.id);
  assert.equal(header.el.querySelector('nav').getAttribute('aria-label'), 'Billing sections');
  assert.equal(tab('Retros').getAttribute('aria-current'), 'page');
  assert.equal(tab('Retros').href, `/teams/${B.id}?tab=retros`);
});

test('on /mentions the last team shows with no section active, and the bell is the current page', () => {
  visit('/mentions', '/mentions');
  assert.equal(header.el.querySelector('nav').getAttribute('aria-label'), 'Apps sections');
  assert.equal(tabs().filter((a) => a.hasAttribute('aria-current')).length, 0);
  assert.equal(header.el.querySelector('.app-mentions').getAttribute('aria-current'), 'page');
  visit('/account', '/account');
  assert.equal(header.el.querySelector('.app-mentions').hasAttribute('aria-current'), false);
});

test('a room kick shows no access when the room is gone or the user lost it, and not for a full tab', () => {
  assert.deepEqual(kickScreen('deleted'), { removed: false });
  assert.deepEqual(kickScreen('not_found'), { removed: false });
  assert.deepEqual(kickScreen('access_revoked'), { removed: true });
  assert.deepEqual(kickScreen('forbidden'), { removed: true });
  assert.equal(kickScreen('too_many_rooms'), null);
  assert.equal(kickScreen(null), null);
});
