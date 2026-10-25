// A team page context for the section components (team/index.js builds the real one): a room whose state the
// test sets, and an API whose answers the test chooses and whose calls it can read.
import { FakeEvent, installDom } from '../core/_dom.js';

export const TEAM = '0192f0a0-0000-7000-8000-00000000a001';

export function setupDom() {
  const doc = installDom();
  globalThis.requestAnimationFrame = (fn) => setTimeout(fn, 0);
  globalThis.cancelAnimationFrame = (id) => clearTimeout(id);
  globalThis.window = { addEventListener() {}, removeEventListener() {} };
  globalThis.location = new URL(`https://app.test/teams/${TEAM}`);
  const proto = Object.getPrototypeOf(doc.createElement('dialog'));
  proto.showModal = function showModal() {
    this.open = true;
  };
  proto.close = function close(value = '') {
    this.open = false;
    this.returnValue = value;
    this.dispatchEvent(new FakeEvent('close'));
  };
  return doc;
}

/** A room whose view the test replaces with set(); select() listeners run at once, as the hub's would next frame. */
export function fakeRoom(view) {
  const subs = new Set();
  return {
    view,
    select(pick, eq, listener) {
      const sub = { pick, eq, listener, last: pick(this.view) };
      subs.add(sub);
      return () => subs.delete(sub);
    },
    set(next) {
      this.view = next;
      for (const sub of [...subs]) {
        const slice = sub.pick(next);
        if (!sub.eq(sub.last, slice)) {
          const previous = sub.last;
          sub.last = slice;
          sub.listener(slice, previous);
        }
      }
    },
  };
}

/** A section's ctx: `answers` maps "METHOD path" to a body or a function of the request body. */
export function fakeCtx({ answers = {}, admin = false, team = { name: 'Platform Team', jira: { configured: false } }, room = fakeRoom({ poker: {} }) } = {}) {
  const calls = [];
  const navigated = [];
  let query = new URLSearchParams();
  const answer = (method) => async (path, body) => {
    calls.push({ method, path, body });
    const found = answers[`${method} ${path}`] ?? answers[`${method} ${path.split('?')[0]}`];
    if (found === undefined) {
      throw Object.assign(new Error(`no answer for ${method} ${path}`), { status: 404 });
    }
    return typeof found === 'function' ? found(body, path) : structuredClone(found);
  };
  return {
    calls,
    navigated,
    teamId: TEAM,
    api: { get: answer('GET'), post: answer('POST'), patch: answer('PATCH'), put: answer('PUT'), del: answer('DELETE') },
    room,
    team: () => team,
    isAdmin: () => admin,
    isOwner: () => admin,
    query: () => query,
    setQuery(changes) {
      query = new URLSearchParams(query);
      for (const [key, value] of Object.entries(changes)) {
        if (value == null) {
          query.delete(key);
        } else {
          query.set(key, value);
        }
      }
    },
    navigate: (to) => navigated.push(to),
  };
}

/** Lets the section's pending loads answer. */
export const settle = () => new Promise((resolve) => setTimeout(resolve, 0));

export const text = (node) => node?.textContent.replace(/\s+/g, ' ').trim() ?? null;
