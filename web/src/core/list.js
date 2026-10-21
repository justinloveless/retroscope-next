// Keyed list reconciliation. The container's children belong to reconcile(); nodes are matched by key
// only, so an optimistic entity and its confirmed version are the same DOM node. Moves are limited to
// the nodes outside the longest run that is already in order, which keeps focus and scroll on the rest.

const lists = new WeakMap();

function reducedMotion() {
  return globalThis.matchMedia?.('(prefers-reduced-motion: reduce)').matches ?? true;
}

// Indices (into `seq`) of one longest strictly increasing subsequence; -1 entries are skipped.
function longestIncreasing(seq) {
  const tails = [];
  const prev = new Array(seq.length).fill(-1);
  for (let i = 0; i < seq.length; i++) {
    if (seq[i] < 0) {
      continue;
    }
    let lo = 0;
    let hi = tails.length;
    while (lo < hi) {
      const mid = (lo + hi) >> 1;
      if (seq[tails[mid]] < seq[i]) {
        lo = mid + 1;
      } else {
        hi = mid;
      }
    }
    prev[i] = lo > 0 ? tails[lo - 1] : -1;
    tails[lo] = i;
  }
  const keep = new Set();
  for (let i = tails.length ? tails[tails.length - 1] : -1; i >= 0; i = prev[i]) {
    keep.add(i);
  }
  return keep;
}

function measure(nodes) {
  const rects = new Map();
  for (const node of nodes) {
    rects.set(node, node.getBoundingClientRect());
  }
  return rects;
}

function playFlip(before, options) {
  for (const [node, first] of before) {
    if (!node.isConnected) {
      continue;
    }
    const last = node.getBoundingClientRect();
    const dx = first.left - last.left;
    const dy = first.top - last.top;
    if (dx === 0 && dy === 0) {
      continue;
    }
    node.animate?.(
      [{ transform: `translate(${dx}px, ${dy}px)` }, { transform: 'none' }],
      { duration: options.duration ?? 200, easing: options.easing ?? 'cubic-bezier(.22,1,.36,1)' },
    );
  }
}

/**
 * Makes `container`'s children match `items`.
 * create(item, index) → Element; update(el, item, index) patches an existing node.
 * options.flip animates moved nodes (FLIP; skipped under prefers-reduced-motion).
 * options.remove(el, done) may delay removal (an exit animation) and must call done().
 */
export function reconcile(container, items, keyFn, create, update, options = {}) {
  let byKey = lists.get(container);
  if (!byKey) {
    byKey = new Map();
    lists.set(container, byKey);
  }

  const keys = items.map((item, i) => keyFn(item, i));
  const wanted = new Set(keys);
  if (wanted.size !== keys.length) {
    throw new Error('reconcile: duplicate keys');
  }

  const flip = options.flip && !reducedMotion() ? measure(byKey.values()) : null;

  for (const [key, node] of byKey) {
    if (!wanted.has(key)) {
      byKey.delete(key);
      if (options.remove) {
        options.remove(node, () => node.remove());
      } else {
        node.remove();
      }
    }
  }

  const oldIndex = new Map();
  let position = 0;
  for (let child = container.firstChild; child; child = child.nextSibling) {
    oldIndex.set(child, position++);
  }

  const nodes = items.map((item, i) => {
    let node = byKey.get(keys[i]);
    if (node) {
      update?.(node, item, i);
    } else {
      node = create(item, i);
      byKey.set(keys[i], node);
    }
    return node;
  });

  const stay = longestIncreasing(nodes.map((node) => oldIndex.get(node) ?? -1));
  for (let i = nodes.length - 1, next = null; i >= 0; i--) {
    const node = nodes[i];
    if (!stay.has(i)) {
      container.insertBefore(node, next ?? trailing(container, nodes));
    }
    next = node;
  }

  if (flip) {
    playFlip(flip, options.flip === true ? {} : options.flip);
  }
  return nodes;
}

// Children that are still leaving (delayed removal) stay after the live ones.
function trailing(container, nodes) {
  const live = new Set(nodes);
  let anchor = null;
  for (let child = container.lastChild; child && !live.has(child); child = child.previousSibling) {
    anchor = child;
  }
  return anchor;
}

export function forget(container) {
  lists.delete(container);
}
