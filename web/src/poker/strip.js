// The round strip (§g.8): a native horizontal scroller with centred snapping that navigates when a
// user's scroll settles, one chip per round (ticket, pending and points pills, the spotlight cone), grip
// reorder with Pointer Events (a long press on touch) or the arrow keys, and a context menu.

import { h, setStyle } from '../core/h.js';
import { reconcile } from '../core/list.js';
import { currentMe } from '../screens/app.js';
import { createAvatar } from '../ui/avatar.js';
import { createButton } from '../ui/button.js';
import { issueTypeGlyph } from '../ui/glyphs.js';
import { confirmDialog } from '../ui/confirm.js';
import { icon } from '../ui/icons.js';
import { createMenu } from '../ui/menu.js';
import { DRAG_THRESHOLD_PX } from './drag.js';
import { apply, uuidv7 } from './model.js';
import { askNewRound } from './table.js';

export const SETTLE_DEBOUNCE_MS = 120;
export const LONG_PRESS_MS = 300;
const WHEEL_STEP_PX = 100;
const TARGET_MS = 1000;
const EDGE_SCROLL_PX = 40;
const EDGE_SCROLL_SPEED = 8;

/** `ids` with `id` moved to position `index` (clamped). */
export function moveTo(ids, id, index) {
  const rest = ids.filter((x) => x !== id);
  const at = Math.max(0, Math.min(rest.length, index));
  return [...rest.slice(0, at), id, ...rest.slice(at)];
}

export function chipLabel(round) {
  return round.ticketKey ?? `Round ${round.number}`;
}

/** The points a chip shows: the Jira story points, else the revealed winner. */
export function chipPoints(round) {
  if (round.jira) {
    return round.jira.points;
  }
  return round.state === 'revealed' ? round.winner ?? null : null;
}

/** Whether the viewer may delete rounds (§c.4): the session's team admins and owners. */
export function mayDeleteRounds(view, me = currentMe()) {
  const role = me?.teams?.find((t) => t.id === view.teamId)?.role;
  return role === 'admin' || role === 'owner';
}

export function personOf(view, pid) {
  return view.presence.find((p) => p.pid === pid) ?? view.roster.find((p) => p.pid === pid) ?? null;
}

/** 'scrollend' where the browser fires it, else 'debounce' (a pause in `scroll` events, F19). */
export function settleMode(el) {
  return 'onscrollend' in el ? 'scrollend' : 'debounce';
}

function orderIntent(order) {
  return (state) => apply(state, { k: 'poker.round.order', d: { order } });
}

function reducedMotion() {
  return globalThis.matchMedia?.('(prefers-reduced-motion: reduce)').matches ?? true;
}

function describe(round, spotlit) {
  const parts = [`Round ${round.number}`];
  if (round.ticketKey) {
    parts.push(round.ticketKey);
  }
  if (round.pending) {
    parts.push('pending');
  } else if (round.state === 'revealed') {
    parts.push('revealed');
  } else {
    parts.push(round.active ? 'open' : 'ended');
  }
  const points = chipPoints(round);
  if (points != null) {
    parts.push(`${points} points`);
  }
  if (spotlit) {
    parts.push('in the spotlight');
  }
  return parts.join(', ');
}

export function createStrip(ctx) {
  const track = h('ol', { class: 'nt-strip-track' });
  const scroller = h('div', { class: 'nt-strip-scroller' }, track);
  const newRound = createButton({ label: 'New round', icon: 'plus', iconOnly: true, variant: 'default', press: true, size: 'sm', onClick: startRound });
  const nextOpen = createButton({ label: 'Next open round', icon: 'play', iconOnly: true, variant: 'default', press: true, size: 'sm', onClick: gotoNextOpen });
  const el = h('section', { class: 'nt-strip', 'aria-label': 'Round strip', dataset: { settle: settleMode(scroller) } },
    h('div', { class: 'nt-strip-view' }, scroller));
  // The round tools sit in the toolbar beside Previous/Next round, as in the original (docs/decisions/UX7.md).
  const tools = h('span', { class: 'nt-round-tools' }, nextOpen.el, newRound.el);
  ctx.slots.strip.append(el);
  ctx.slots.rounds.append(tools);

  const chips = new Map();
  const menu = createMenu({ items: [], label: 'Round actions', onSelect: (action) => menuActions[action]?.() });
  let view = null;
  let viewed = null;
  let centered = null;
  let orderKey = '';
  let userScroll = false;
  let wheelAccum = 0;
  let wheelIndex = null;
  let target = null;
  let settleTimer = 0;
  let drag = null;
  let menuRound = null;

  const send = (k, d, options) => ctx.room.send(k, Object.fromEntries(Object.entries(d).filter(([, v]) => v !== undefined)), options);

  // The room's live view, not the last render: two key presses can land in one animation frame.
  function ids() {
    return ctx.room.view?.rounds.map((r) => r.id) ?? [];
  }

  function reorder(roundId, index) {
    const current = ids();
    const order = moveTo(current, roundId, index);
    if (order.every((id, i) => id === current[i])) {
      return;
    }
    send('poker.round.reorder', { order }, {
      apply: orderIntent(order),
      rebase(fresh) {
        const current = fresh.rounds.map((r) => r.id);
        return current.includes(roundId) ? { order: moveTo(current, roundId, index) } : null;
      },
      applyFor: (d) => orderIntent(d.order),
    });
  }

  async function startRound() {
    const ticketKey = await askNewRound();
    if (ticketKey !== null) {
      send('poker.round.create', { roundId: uuidv7(), ticketKey: ticketKey || undefined });
    }
  }

  function gotoNextOpen() {
    const rounds = view?.rounds ?? [];
    const at = rounds.findIndex((r) => r.id === viewed);
    const next = [...rounds.slice(at + 1), ...rounds.slice(0, Math.max(0, at))]
      .find((r) => r.active && !r.pending && r.state === 'voting');
    if (next) {
      ctx.navigate(next.id);
    }
  }

  // Chips

  function createChip(round) {
    const grip = h('button', { type: 'button', class: 'nt-chip-grip' }, icon('grip-vertical'));
    const key = h('span', { class: 'nt-chip-key' });
    const pending = h('span', { class: 'nt-chip-pending', text: 'Pending' });
    const points = h('span', { class: 'nt-chip-points' });
    const holder = h('span', { class: 'nt-chip-holder' });
    const type = h('span', { class: 'nt-chip-icon', 'aria-hidden': 'true' });
    const main = h('button', { type: 'button', class: 'nt-chip-main' },
      h('span', { class: 'nt-chip-cone', 'aria-hidden': 'true' }), type, key, pending, points, holder);
    const node = h('li', { class: 'nt-chip', dataset: { id: round.id } }, grip, main);
    node.parts = { grip, main, type, key, pending, points, holder, avatar: null, issueType: undefined };
    patchChip(node, round);
    return node;
  }

  function patchChip(node, round) {
    const { grip, main, key, pending, points, holder } = node.parts;
    const spot = view.spotlight?.roundId === round.id ? view.spotlight : null;
    const value = chipPoints(round);
    node.dataset.state = round.pending ? 'pending' : round.state;
    node.classList.toggle('is-active', round.active && !round.pending);
    node.classList.toggle('is-selected', round.id === viewed);
    node.toggleAttribute('data-spotlight', Boolean(spot));
    main.setAttribute('aria-label', describe(round, Boolean(spot)));
    if (round.id === viewed) {
      main.setAttribute('aria-current', 'true');
    } else {
      main.removeAttribute('aria-current');
    }
    grip.setAttribute('aria-label', `Move round ${round.number}`);
    key.textContent = chipLabel(round);
    key.classList.toggle('is-placeholder', !round.ticketKey);
    pending.hidden = !round.pending;
    // The issue type arrives with the room's jira.meta fill; until then (and without a key) the glyph is neutral.
    const issueType = round.ticketKey ? ctx.room.extras['jira.meta']?.[round.ticketKey]?.issueType ?? null : null;
    if (issueType !== node.parts.issueType) {
      node.parts.issueType = issueType;
      node.parts.type.replaceChildren(issueTypeGlyph(issueType));
    }
    points.textContent = value == null ? '' : String(value);
    points.hidden = value == null;
    const person = spot ? personOf(view, spot.holder) ?? { pid: spot.holder, name: 'Spotlight holder' } : null;
    if (person) {
      const props = { name: person.name, src: person.avatar ? `/blob/${person.avatar}` : null, size: 'sm', className: 'nt-chip-avatar' };
      if (node.parts.avatar) {
        node.parts.avatar.update(props);
      } else {
        node.parts.avatar = createAvatar(props);
        holder.append(node.parts.avatar.el);
      }
    } else if (node.parts.avatar) {
      node.parts.avatar.destroy();
      node.parts.avatar = null;
    }
    holder.hidden = !person;
  }

  // Scrolling: the viewed chip is kept centred; a scroll the user made navigates when it settles.

  function centerOf(node) {
    return node.offsetLeft + node.offsetWidth / 2 - scroller.clientWidth / 2;
  }

  function scrollToChip(id, smooth) {
    const node = chips.get(id);
    if (!node) {
      return;
    }
    const left = Math.max(0, Math.min(scroller.scrollWidth - scroller.clientWidth, centerOf(node)));
    if (Math.abs(scroller.scrollLeft - left) > 1) {
      target = { left, at: performance.now() };
      scroller.scrollTo({ left, behavior: smooth && !reducedMotion() ? 'smooth' : 'auto' });
    }
  }

  function centeredIndex() {
    const middle = scroller.scrollLeft + scroller.clientWidth / 2;
    let best = -1;
    let distance = Infinity;
    view.rounds.forEach((round, i) => {
      const node = chips.get(round.id);
      const d = node ? Math.abs(node.offsetLeft + node.offsetWidth / 2 - middle) : Infinity;
      if (d < distance) {
        best = i;
        distance = d;
      }
    });
    return best;
  }

  function settle() {
    clearTimeout(settleTimer);
    // A slow frame (or a newer scrollTo interrupting an older one) can look like a settled scroll; wait
    // for the target of the scroll the page started.
    if (target && Math.abs(scroller.scrollLeft - target.left) > 2 && performance.now() - target.at < TARGET_MS) {
      return;
    }
    wheelIndex = null;
    target = null;
    if (drag || !view) {
      return;
    }
    if (!userScroll) {
      // A scroll nobody asked for (layout shift, font swap, an interrupted smooth scroll) leaves the
      // viewed chip off centre; put it back.
      scrollToChip(viewed, false);
      return;
    }
    userScroll = false;
    const round = view.rounds[centeredIndex()];
    if (round && round.id !== viewed) {
      ctx.navigate(round.id);
    }
  }

  if (settleMode(scroller) === 'scrollend') {
    scroller.addEventListener('scrollend', settle);
  } else {
    scroller.addEventListener('scroll', () => {
      clearTimeout(settleTimer);
      settleTimer = setTimeout(settle, SETTLE_DEBOUNCE_MS);
    }, { passive: true });
  }
  const markUser = () => {
    userScroll = true;
  };
  scroller.addEventListener('touchstart', markUser, { passive: true });
  scroller.addEventListener('pointerdown', markUser);

  // A vertical wheel moves one chip per step; horizontal scrolling stays native.
  scroller.addEventListener('wheel', (event) => {
    userScroll = true;
    if (Math.abs(event.deltaY) <= Math.abs(event.deltaX) || !view?.rounds.length) {
      return;
    }
    event.preventDefault();
    wheelAccum += event.deltaMode === 1 ? event.deltaY * 16 : event.deltaY;
    const steps = Math.trunc(wheelAccum / WHEEL_STEP_PX);
    if (steps === 0) {
      return;
    }
    wheelAccum -= steps * WHEEL_STEP_PX;
    wheelIndex = Math.max(0, Math.min(view.rounds.length - 1, (wheelIndex ?? centeredIndex()) + steps));
    scrollToChip(view.rounds[wheelIndex].id, true);
  }, { passive: false });

  track.addEventListener('click', (event) => {
    const main = event.target.closest('.nt-chip-main');
    if (main) {
      // An explicit pick: the scroll that follows is the page's, not a gesture to settle on.
      userScroll = false;
      const id = main.parentElement.dataset.id;
      scrollToChip(id, true);
      ctx.navigate(id);
    }
  });

  // Reorder: the grip only. A mouse drags after 8 px, touch after a 300 ms press without moving.

  track.addEventListener('pointerdown', (event) => {
    const grip = event.target.closest('.nt-chip-grip');
    if (!grip || event.button !== 0 || drag) {
      return;
    }
    event.stopPropagation();
    const node = grip.parentElement;
    const touch = event.pointerType !== 'mouse';
    const session = {
      node,
      id: node.dataset.id,
      pointerId: event.pointerId,
      startX: event.clientX,
      startY: event.clientY,
      x: event.clientX,
      active: false,
      index: null,
      frame: 0,
      timer: touch ? setTimeout(() => activate(session), LONG_PRESS_MS) : 0,
    };
    drag = session;
    try {
      grip.setPointerCapture(event.pointerId);
    } catch {
      // Synthetic pointers (and pointers already released) cannot be captured; the listeners still run.
    }

    const move = (e) => {
      if (e.pointerId !== session.pointerId) {
        return;
      }
      const distance = Math.hypot(e.clientX - session.startX, e.clientY - session.startY);
      if (!session.active) {
        if (distance > DRAG_THRESHOLD_PX) {
          if (touch) {
            end(false);
            return;
          }
          activate(session);
        } else {
          return;
        }
      }
      e.preventDefault();
      session.x = e.clientX;
      trackPointer(session);
    };
    const finish = (commit) => (e) => {
      if (e.pointerId === session.pointerId) {
        end(commit);
      }
    };
    const up = finish(true);
    const cancel = finish(false);

    function end(commit) {
      clearTimeout(session.timer);
      cancelAnimationFrame(session.frame);
      grip.removeEventListener('pointermove', move);
      grip.removeEventListener('pointerup', up);
      grip.removeEventListener('pointercancel', cancel);
      node.classList.remove('is-dragging');
      setStyle(node, { translate: null });
      for (const chip of chips.values()) {
        chip.classList.remove('is-drop-target');
      }
      el.classList.remove('is-reordering');
      drag = null;
      if (commit && session.active && session.index !== null) {
        reorder(session.id, session.index);
      }
    }

    grip.addEventListener('pointermove', move);
    grip.addEventListener('pointerup', up);
    grip.addEventListener('pointercancel', cancel);
  });

  function activate(session) {
    if (drag !== session || session.active) {
      return;
    }
    session.active = true;
    session.origin = session.node.offsetLeft;
    session.node.classList.add('is-dragging');
    el.classList.add('is-reordering');
    edgeScroll(session);
  }

  function trackPointer(session) {
    const shift = session.x - session.startX + (session.node.offsetLeft - session.origin);
    setStyle(session.node, { translate: `${shift}px 0` });
    const others = [...chips.values()].filter((node) => node !== session.node && node.isConnected);
    others.sort((a, b) => a.offsetLeft - b.offsetLeft);
    session.index = others.filter((node) => {
      const rect = node.getBoundingClientRect();
      return rect.left + rect.width / 2 < session.x;
    }).length;
    const target = others[Math.min(session.index, others.length - 1)];
    for (const node of others) {
      node.classList.toggle('is-drop-target', node === target);
    }
  }

  function edgeScroll(session) {
    const step = () => {
      if (drag !== session) {
        return;
      }
      const rect = scroller.getBoundingClientRect();
      const speed = session.x < rect.left + EDGE_SCROLL_PX ? -EDGE_SCROLL_SPEED : session.x > rect.right - EDGE_SCROLL_PX ? EDGE_SCROLL_SPEED : 0;
      if (speed !== 0) {
        scroller.scrollLeft += speed;
        trackPointer(session);
      }
      session.frame = requestAnimationFrame(step);
    };
    session.frame = requestAnimationFrame(step);
  }

  track.addEventListener('keydown', (event) => {
    const grip = event.target.closest('.nt-chip-grip');
    if (!grip || (event.key !== 'ArrowLeft' && event.key !== 'ArrowRight') || event.shiftKey || event.altKey || event.ctrlKey || event.metaKey) {
      return;
    }
    event.preventDefault();
    const id = grip.parentElement.dataset.id;
    const index = ids().indexOf(id);
    reorder(id, index + (event.key === 'ArrowLeft' ? -1 : 1));
    requestAnimationFrame(() => chips.get(id)?.parts.grip.focus());
  });

  // Context menu: Move left/right, Activate, Delete.

  const menuActions = {
    left: () => moveBy(-1),
    right: () => moveBy(1),
    activate() {
      send('poker.round.activate', { roundId: menuRound });
    },
    async delete() {
      const round = view.rounds.find((r) => r.id === menuRound);
      if (!round) {
        return;
      }
      const ok = await confirmDialog({
        title: `Delete round ${round.number}?`,
        message: 'The round and its votes are deleted for everyone.',
        confirmLabel: 'Delete round',
        destructive: true,
      });
      if (ok) {
        send('poker.round.delete', { roundId: round.id });
      }
    },
  };

  function moveBy(delta) {
    const index = ids().indexOf(menuRound);
    if (index >= 0) {
      reorder(menuRound, index + delta);
    }
  }

  track.addEventListener('contextmenu', (event) => {
    const node = event.target.closest('.nt-chip');
    if (!node || !view) {
      return;
    }
    event.preventDefault();
    const index = ids().indexOf(node.dataset.id);
    const round = view.rounds[index];
    menuRound = round.id;
    const items = [
      { action: 'left', label: 'Move left', icon: 'chevron-left', disabled: index === 0 },
      { action: 'right', label: 'Move right', icon: 'chevron-right', disabled: index === view.rounds.length - 1 },
    ];
    if (!round.active && !round.pending && round.state === 'voting') {
      items.push({ action: 'activate', label: 'Activate round', icon: 'play' });
    }
    if (view.rounds.length > 1 && mayDeleteRounds(view)) {
      items.push('separator', { action: 'delete', label: 'Delete round', icon: 'trash', destructive: true });
    }
    menu.update({ items });
    let { clientX: x, clientY: y } = event;
    if (x === 0 && y === 0) {
      const rect = node.getBoundingClientRect();
      x = rect.left;
      y = rect.bottom;
    }
    menu.openAt(x, y);
  });

  return {
    el,
    update(next, roundId) {
      const moved = roundId !== viewed;
      const key = next.rounds.map((r) => r.id).join();
      const reordered = key !== orderKey;
      orderKey = key;
      view = next;
      viewed = roundId;
      reconcile(track, view.rounds, (r) => r.id, (round) => {
        const node = createChip(round);
        chips.set(round.id, node);
        return node;
      }, patchChip, { flip: !drag, remove: (node, done) => {
        chips.delete(node.dataset.id);
        node.parts.avatar?.destroy();
        done();
      } });
      if (drag?.active) {
        trackPointer(drag);
      }
      if ((moved || reordered || centered === null) && !drag) {
        scrollToChip(viewed, centered !== null);
        centered = viewed;
      }
    },
    destroy() {
      clearTimeout(settleTimer);
      menu.destroy();
      el.remove();
      tools.remove();
    },
  };
}
