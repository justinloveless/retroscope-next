// Slice subscriptions over any state source. Writes only mark the hub dirty; subscribers run once in
// the next animation frame with the latest state, and only when their selected slice changed.

export function shallowEq(a, b) {
  if (Object.is(a, b)) {
    return true;
  }
  if (typeof a !== 'object' || typeof b !== 'object' || a === null || b === null) {
    return false;
  }
  if (Array.isArray(a) !== Array.isArray(b)) {
    return false;
  }
  if (a instanceof Map && b instanceof Map) {
    if (a.size !== b.size) {
      return false;
    }
    for (const [k, v] of a) {
      if (!b.has(k) || !Object.is(v, b.get(k))) {
        return false;
      }
    }
    return true;
  }
  const keys = Object.keys(a);
  if (keys.length !== Object.keys(b).length) {
    return false;
  }
  return keys.every((k) => Object.hasOwn(b, k) && Object.is(a[k], b[k]));
}

function defaultSchedule(run) {
  if (typeof requestAnimationFrame === 'function') {
    requestAnimationFrame(run);
  } else {
    setTimeout(run, 16);
  }
}

function report(error) {
  if (typeof reportError === 'function') {
    reportError(error);
  } else {
    console.error(error);
  }
}

/**
 * read() returns the current state. select(fn, eq, listener) calls listener(slice, previous) after a
 * notify() whenever eq(previous, slice) is false, batched to one call per frame; it returns unsubscribe.
 */
export function createHub(read, { schedule = defaultSchedule } = {}) {
  const subscriptions = new Set();
  let scheduled = false;

  function flush() {
    scheduled = false;
    const state = read();
    for (const sub of [...subscriptions]) {
      if (!subscriptions.has(sub)) {
        continue;
      }
      try {
        const next = sub.fn(state);
        if (!sub.eq(sub.value, next)) {
          const previous = sub.value;
          sub.value = next;
          sub.listener(next, previous);
        }
      } catch (error) {
        report(error);
      }
    }
  }

  function notify() {
    if (!scheduled) {
      scheduled = true;
      schedule(() => {
        if (scheduled) {
          flush();
        }
      });
    }
  }

  function select(fn, eq = Object.is, listener) {
    const sub = { fn, eq, listener, value: fn(read()) };
    subscriptions.add(sub);
    return () => subscriptions.delete(sub);
  }

  return { select, notify, flush };
}

export function createStore(initial, options) {
  let state = initial;
  const hub = createHub(() => state, options);

  function set(next) {
    if (!Object.is(next, state)) {
      state = next;
      hub.notify();
    }
  }

  return {
    get: () => state,
    set,
    update: (fn) => set(fn(state)),
    select: hub.select,
    flush: hub.flush,
  };
}
