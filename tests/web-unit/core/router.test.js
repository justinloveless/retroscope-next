import { test, beforeEach } from 'node:test';
import assert from 'node:assert/strict';
import { FakeEvent, installDom } from './_dom.js';
import { createRouter, loadStyles, match, resolve } from '../../../web/src/core/router.js';
import { routes as appRoutes } from '../../../web/src/routes.js';

test('match extracts and decodes parameters', () => {
  assert.deepEqual(match('/teams/:tid/poker/:code', '/teams/t1/poker/ab%20c'), { tid: 't1', code: 'ab c' });
  assert.deepEqual(match('/', '/'), {});
  assert.equal(match('/teams/:id', '/teams'), null);
  assert.equal(match('/teams/:id', '/teams/a/b'), null);
  assert.equal(match('/teams/:id', '/teams/'), null);
  assert.equal(match('/dashboard', '/dashboardx'), null);
});

test('a wildcard matches the rest of the path', () => {
  assert.deepEqual(match('/admin/*', '/admin/users/42'), {});
  assert.deepEqual(match('/auth/*', '/auth/magic'), {});
  assert.equal(match('/auth/*', '/account'), null);
});

test('a malformed percent-encoding leaves the route unmatched instead of throwing', () => {
  assert.equal(match('/poker/:code', '/poker/%E0%A4%A'), null);
  assert.equal(match('/retro/:code', '/retro/%'), null);
  assert.equal(resolve(appRoutes, new URL('http://app.test/retro/%zz')), null);
});

test('resolve walks the real route table and adds route and query', () => {
  const found = resolve(appRoutes, new URL('http://app.test/teams/t1/settings?tab=jira'));
  assert.equal(found.route.module, 'team/index.js');
  assert.equal(found.params.id, 't1');
  assert.equal(found.params.route, '/teams/:id/settings');
  assert.equal(found.params.query.get('tab'), 'jira');
  assert.equal(resolve(appRoutes, new URL('http://app.test/teams/t1')).params.route, '/teams/:id');
  assert.equal(resolve(appRoutes, new URL('http://app.test/nope')), null);
});

let doc;
let browser;

function fakeBrowser(href) {
  let url = new URL(href);
  const listeners = new Map();
  const assigned = [];
  globalThis.location = {
    get href() {
      return url.href;
    },
    get origin() {
      return url.origin;
    },
    get pathname() {
      return url.pathname;
    },
    assign: (to) => assigned.push(to),
  };
  globalThis.history = {
    pushState: (_s, _t, to) => {
      url = new URL(to, url);
    },
    replaceState: (_s, _t, to) => {
      url = new URL(to, url);
    },
  };
  globalThis.window = {
    addEventListener: (type, fn) => listeners.set(type, fn),
    removeEventListener: (type) => listeners.delete(type),
    scrollTo() {},
  };
  return {
    assigned,
    back(to) {
      url = new URL(to, url);
      listeners.get('popstate')?.(new FakeEvent('popstate'));
    },
  };
}

function screen(name, log) {
  return {
    mount(root, boot) {
      log.push(`mount ${name} ${JSON.stringify(boot)}`);
      return {
        update(params) {
          log.push(`update ${name} ${JSON.stringify(Object.fromEntries(Object.entries(params).filter(([k]) => k !== 'query')))}`);
        },
        unmount() {
          log.push(`unmount ${name}`);
        },
      };
    },
  };
}

function testRoutes(log, gate = {}) {
  const load = (name) => async () => {
    await gate[name];
    return screen(name, log);
  };
  return [
    { path: '/', module: 'landing', load: load('landing') },
    { path: '/teams/:id', module: 'team', load: load('team') },
    { path: '/teams/:id/settings', module: 'team', load: load('team') },
    { path: '/poker/:code', module: 'poker', load: load('poker') },
  ];
}

beforeEach(() => {
  doc = installDom();
  browser = fakeBrowser('http://app.test/');
});

test('start mounts the first route with the boot JSON from the page', async () => {
  const log = [];
  const router = createRouter({ routes: testRoutes(log), root: doc.body, boot: { me: 1 }, loadBoot: () => assert.fail('no fetch on first page') });
  await router.start();
  assert.deepEqual(log, ['mount landing {"me":1}', 'update landing {"route":"/"}']);
});

test('navigating within one module only updates; another module fetches boot and remounts', async () => {
  const log = [];
  const boots = [];
  const router = createRouter({
    routes: testRoutes(log),
    root: doc.body,
    boot: {},
    loadBoot: async (path) => {
      boots.push(path);
      return { path };
    },
  });
  browser = fakeBrowser('http://app.test/teams/t1');
  await router.start();
  log.length = 0;

  await router.navigate('/teams/t1/settings');
  assert.deepEqual(log, ['update team {"id":"t1","route":"/teams/:id/settings"}']);
  assert.deepEqual(boots, []);

  log.length = 0;
  await router.navigate('/poker/abc?round=3');
  assert.deepEqual(boots, ['/poker/abc?round=3']);
  assert.deepEqual(log, ['unmount team', 'mount poker {"path":"/poker/abc?round=3"}', 'update poker {"code":"abc","route":"/poker/:code"}']);
  assert.equal(location.href, 'http://app.test/poker/abc?round=3');
});

test('a slower earlier navigation never overwrites a later one', async () => {
  const log = [];
  let release;
  const gate = { poker: new Promise((r) => {
    release = r;
  }) };
  const router = createRouter({ routes: testRoutes(log, gate), root: doc.body, boot: {}, loadBoot: async () => ({}) });
  await router.start();
  log.length = 0;
  const slow = router.navigate('/poker/abc');
  const fast = router.navigate('/teams/t9');
  await fast;
  release();
  await slow;
  assert.deepEqual(log, ['unmount landing', 'mount team {}', 'update team {"id":"t9","route":"/teams/:id"}']);
});

test('a superseded navigation that fails reports nothing and never reloads its URL', async () => {
  for (const withOnError of [true, false]) {
    browser = fakeBrowser('http://app.test/');
    const log = [];
    const errors = [];
    let fail;
    const gate = { poker: new Promise((_resolve, reject) => {
      fail = reject;
    }) };
    const router = createRouter({
      routes: testRoutes(log, gate),
      root: doc.body,
      boot: {},
      loadBoot: async () => ({}),
      onError: withOnError ? (error, url) => errors.push([error.message, url.pathname]) : undefined,
    });
    await router.start();
    log.length = 0;
    const slow = router.navigate('/poker/abc');
    await router.navigate('/teams/t9');
    fail(new Error('chunk failed'));
    await slow;
    assert.deepEqual(errors, []);
    assert.deepEqual(browser.assigned, []);
    assert.deepEqual(log, ['unmount landing', 'mount team {}', 'update team {"id":"t9","route":"/teams/:id"}']);
  }
});

test('unknown paths and malformed URLs render notFound', async () => {
  const log = [];
  const router = createRouter({
    routes: testRoutes(log),
    root: doc.body,
    boot: {},
    loadBoot: async () => ({}),
    notFound: () => log.push('not found'),
  });
  await router.start();
  log.length = 0;
  await router.navigate('/poker/%E0%A4%A');
  assert.deepEqual(log, ['unmount landing', 'not found']);
});

test('a failed boot fetch goes to onError and leaves the current screen mounted', async () => {
  const log = [];
  const errors = [];
  const router = createRouter({
    routes: testRoutes(log),
    root: doc.body,
    boot: {},
    loadBoot: async () => {
      throw new Error('503');
    },
    onError: (error, url) => errors.push([error.message, url.pathname]),
  });
  await router.start();
  log.length = 0;
  await router.navigate('/poker/x');
  assert.deepEqual(errors, [['503', '/poker/x']]);
  assert.deepEqual(log, []);
});

test('popstate shows the route now in the address bar', async () => {
  const log = [];
  const router = createRouter({ routes: testRoutes(log), root: doc.body, boot: {}, loadBoot: async () => ({ b: 1 }) });
  await router.start();
  log.length = 0;
  browser.back('/teams/t2');
  await new Promise((r) => setTimeout(r, 0));
  assert.deepEqual(log, ['unmount landing', 'mount team {"b":1}', 'update team {"id":"t2","route":"/teams/:id"}']);
});

function link(href, attrs = {}) {
  const a = doc.createElement('a');
  a.setAttribute('data-nav', '');
  const url = new URL(href, 'http://app.test/');
  a.href = url.href;
  a.origin = url.origin;
  a.target = attrs.target ?? '';
  if (attrs.download) {
    a.setAttribute('download', '');
  }
  const span = doc.createElement('span');
  a.appendChild(span);
  doc.body.appendChild(a);
  return span;
}

test('plain clicks on a[data-nav] are routed; modified, external and download clicks are not', async () => {
  const log = [];
  const router = createRouter({ routes: testRoutes(log), root: doc.body, boot: {}, loadBoot: async () => ({}) });
  await router.start();

  const cases = [
    [link('/teams/t3'), {}, true],
    [link('/teams/t4'), { metaKey: true }, false],
    [link('/teams/t5'), { button: 1 }, false],
    [link('https://elsewhere.test/x'), {}, false],
    [link('/teams/t6', { target: '_blank' }), {}, false],
    [link('/teams/t7', { download: true }), {}, false],
  ];
  for (const [target, init, routed] of cases) {
    const event = new FakeEvent('click', { bubbles: true, button: 0, ...init });
    target.dispatchEvent(event);
    assert.equal(event.defaultPrevented, routed, JSON.stringify(init));
  }
  await new Promise((r) => setTimeout(r, 0));
  assert.equal(location.pathname, '/teams/t3');
  router.stop();
});

test('loadStyles links missing stylesheets once and waits for them', async () => {
  const existing = doc.createElement('link');
  existing.setAttribute('rel', 'stylesheet');
  existing.setAttribute('href', '/assets/neotro-A.css');
  doc.head.appendChild(existing);

  let done = false;
  const loading = loadStyles(['/assets/neotro-A.css', '/assets/retro-B.css']).then(() => {
    done = true;
  });
  const links = doc.querySelectorAll('link[rel=stylesheet]');
  assert.deepEqual(links.map((l) => l.getAttribute('href')), ['/assets/neotro-A.css', '/assets/retro-B.css']);
  await new Promise((r) => setTimeout(r, 0));
  assert.equal(done, false, 'waits for the new stylesheet');
  links[1].dispatchEvent(new FakeEvent('load'));
  await loading;
  assert.equal(done, true);
  await loadStyles(undefined);
});

test('context(route) becomes mount()’s third argument, and onShow reports every screen change', async () => {
  const mounted = [];
  const shown = [];
  const view = { update() {}, unmount() {} };
  const routes = [
    { path: '/', module: 'landing', load: async () => ({ mount: (_root, _boot, app) => (mounted.push(['landing', app]), view) }) },
    { path: '/retro/:code', module: 'retro', load: async () => ({ mount: (_root, _boot, app) => (mounted.push(['retro', app]), view) }) },
  ];
  const router = createRouter({
    routes,
    root: doc.body,
    boot: {},
    loadBoot: async () => ({}),
    notFound() {},
    context: async (route) => (route.module === 'retro' ? { socket: 'tab socket' } : undefined),
    onShow: (route, params) => shown.push(route ? `${route.path} ${params.code ?? ''}`.trim() : null),
  });
  await router.start();
  await router.navigate('/retro/ABC');
  await router.navigate('/retro/DEF');
  await router.navigate('/nowhere');

  assert.deepEqual(mounted, [['landing', {}], ['retro', { socket: 'tab socket' }]]);
  assert.deepEqual(shown, ['/', '/retro/:code ABC', '/retro/:code DEF', null]);
});
