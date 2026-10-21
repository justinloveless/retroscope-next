// In-house undo stack of {markup, caret} snapshots. The editor rewrites its DOM programmatically,
// which corrupts the browser's native undo, so every undo and redo goes through here.

const DEFAULT_LIMIT = 200;
const DEFAULT_COALESCE_MS = 1000;

/**
 * @param {{markup: string, caret: {start: number, end: number}}} initial
 * @param {object} [options]
 * @param {number} [options.limit] snapshots kept, the initial one included
 * @param {number} [options.coalesceMs] records with the same key closer than this amend one entry
 * @param {() => number} [options.now]
 */
export function createHistory(initial, options = {}) {
  const limit = Math.max(2, options.limit ?? DEFAULT_LIMIT);
  const coalesceMs = options.coalesceMs ?? DEFAULT_COALESCE_MS;
  const now = options.now ?? (() => Date.now());
  let entries = [initial];
  let index = 0;
  let lastKey = null;
  let lastAt = -Infinity;

  return {
    current: () => entries[index],
    canUndo: () => index > 0,
    canRedo: () => index < entries.length - 1,

    // A typing burst (same key within coalesceMs) amends the newest entry, so one undo removes
    // the burst instead of one character. A null key never coalesces.
    record(state, key = null) {
      const at = now();
      if (state.markup === entries[index].markup) {
        entries[index] = { ...entries[index], caret: state.caret };
        return;
      }
      const amend = key !== null && key === lastKey && at - lastAt < coalesceMs && index > 0;
      entries = entries.slice(0, index + 1);
      if (amend) {
        entries[index] = state;
      } else {
        entries.push(state);
        if (entries.length > limit) {
          entries.shift();
        }
        index = entries.length - 1;
      }
      lastKey = key;
      lastAt = at;
    },

    undo() {
      lastKey = null;
      if (index === 0) {
        return null;
      }
      index--;
      return entries[index];
    },

    redo() {
      lastKey = null;
      if (index === entries.length - 1) {
        return null;
      }
      index++;
      return entries[index];
    },

    reset(state) {
      entries = [state];
      index = 0;
      lastKey = null;
    },
  };
}
