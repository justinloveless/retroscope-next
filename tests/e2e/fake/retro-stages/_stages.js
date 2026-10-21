// Shared setup for the stage, timer, focus, settings and share specs: the retro board fixtures from
// retro-board/_board.js, a second person in their own browser context, a recording AudioContext and a
// skewable Date.now().
import { expect, test } from '../_harness/fixtures.js';
import { EMPTY_OVERLAY, ROOM, snapshot } from '../retro-board/_board.js';

export * from '../retro-board/_board.js';

export const STAGED = { allowAnonymous: true, votingEnabled: true, showAuthorNames: true, stagesEnabled: true, enforceReadiness: false, allowSelfVotes: true, voteEmoji: '👍', sortChronologically: false };

/**
 * Serves one board to several people: `fake.me` and the boot JSON follow a `who` cookie, so each browser
 * context is its own principal. `people` maps that cookie value to the principal.
 */
export function servePeople(fake, people, snap, { overlay = EMPTY_OVERLAY, boot = (me) => ({ r: ROOM }) } = {}) {
  const fallback = Object.values(people)[0];
  const who = (req) => people[/(?:^|;\s*)who=([^;]+)/.exec(req?.headers?.cookie ?? '')?.[1]] ?? fallback;
  fake.me = (req) => who(req);
  fake.boot = (path, req) => {
    const me = who(req);
    const room = boot(me);
    return room ? { build: 'fake', theme: 'light', me, room } : { build: 'fake', theme: 'light', me };
  };
  fake.room(ROOM, { snapshot: snap, overlay, epoch: 'aaaaaaaaaaaaaaaa', seq: 0 });
}

/** A page in a fresh browser context (same device as the project) that the fake server knows as `key`. */
export async function personPage(browser, fake, key, { init = [] } = {}) {
  const { viewport, userAgent, deviceScaleFactor, isMobile, hasTouch } = test.info().project.use;
  const context = await browser.newContext({ baseURL: fake.url, viewport, userAgent, deviceScaleFactor, isMobile, hasTouch });
  await context.addCookies([{ name: 'who', value: key, url: fake.url }]);
  for (const script of init) {
    await context.addInitScript(script.fn, script.arg);
  }
  const page = await context.newPage();
  page.cspViolations = [];
  await page.exposeFunction('__rsCspViolation', (v) => page.cspViolations.push(v));
  await page.addInitScript(() => {
    document.addEventListener('securitypolicyviolation', (e) => {
      window.__rsCspViolation({ directive: e.effectiveDirective, blocked: e.blockedURI, sample: e.sample });
    });
  });
  return page;
}

/** The fixture's own page as `key`, with init scripts added before it loads anything. */
export async function asPerson(page, fake, key, { init = [] } = {}) {
  await page.context().addCookies([{ name: 'who', value: key, url: fake.url }]);
  for (const script of init) {
    await page.addInitScript(script.fn, script.arg);
  }
  return page;
}

export async function openAs(page, code = 'RETRO12345', columns = 3) {
  await page.goto(`/retro/${code}`);
  await expect(page.locator('.retro-column')).toHaveCount(columns);
}

/** Answers stage and readiness commands as RetroDecider does (compare-and-set on `from`). */
export function serveStages(fake, snap = snapshot()) {
  const state = { stage: snap.stage, ready: new Set(snap.readiness.ready), config: snap.config, presence: () => snap.presence.map((p) => p.pid) };
  state.decide = (ctx) => {
    const { from, to } = ctx.frame.d;
    if (from !== state.stage) {
      ctx.nack('stale', { d: { current: state.stage } });
      return;
    }
    if (to === state.stage) {
      ctx.ack();
      return;
    }
    if (state.config.enforceReadiness) {
      const advance = ['thinking', 'voting', 'discussing', 'closed'].indexOf(to) === ['thinking', 'voting', 'discussing', 'closed'].indexOf(from) + 1;
      const always = to === 'thinking' || (from === 'closed' && to === 'discussing');
      if (!always && (!advance || !state.presence().every((pid) => state.ready.has(pid)))) {
        ctx.nack('stage_forbids');
        return;
      }
    }
    state.stage = to;
    state.ready.clear();
    ctx.commit([{ k: 'retro.stage', d: { stage: to } }, { k: 'retro.readiness', d: { stage: to, ready: [] } }]);
  };
  fake.onCommand('retro.stage.set', (ctx) => state.decide(ctx));
  fake.onCommand('retro.ready.set', (ctx) => {
    const { stage, ready } = ctx.frame.d;
    if (stage !== state.stage) {
      ctx.nack('stale', { d: { current: state.stage } });
      return;
    }
    if (state.ready.has(ctx.conn.me.pid) === ready) {
      ctx.ack();
      return;
    }
    if (ready) {
      state.ready.add(ctx.conn.me.pid);
    } else {
      state.ready.delete(ctx.conn.me.pid);
    }
    ctx.commit([{ k: 'retro.readiness', d: { stage, ready: [...state.ready].sort() } }]);
  });
  return state;
}

/** Records every tone instead of playing it; `window.__tones` lists `{type, frequency, at}`. */
export const fakeAudio = {
  fn: () => {
    window.__tones = [];
    class Param {
      constructor(value) {
        this.value = value;
      }

      setValueAtTime() {}

      linearRampToValueAtTime() {}
    }
    class Node {
      connect(target) {
        return target;
      }

      disconnect() {}
    }
    window.AudioContext = class {
      constructor() {
        this.state = 'suspended';
        this.destination = new Node();
      }

      get currentTime() {
        return performance.now() / 1000;
      }

      resume() {
        this.state = 'running';
        return Promise.resolve();
      }

      createGain() {
        return Object.assign(new Node(), { gain: new Param(1) });
      }

      createOscillator() {
        const node = Object.assign(new Node(), { type: 'sine', frequency: new Param(440) });
        node.start = (at) => window.__tones.push({ type: node.type, frequency: node.frequency.value, at });
        node.stop = () => {};
        return node;
      }
    };
  },
};

/** Date.now() runs `skew` ms off the real clock; `window.__skew` can be changed to step it. */
export function skewedClock(skew) {
  return {
    fn: (initial) => {
      const real = Date.now.bind(Date);
      window.__skew = initial;
      Date.now = () => real() + window.__skew;
    },
    arg: skew,
  };
}

export const alarms = (page) => page.evaluate(() => window.__tones.filter((t) => t.type === 'square' && t.frequency === 880).length / 3);
