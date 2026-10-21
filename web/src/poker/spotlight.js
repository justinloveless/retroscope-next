// The spotlight control (§c.4, §g.8): the holder's avatar and a menu to spotlight the viewed round,
// take it over (after "Take spotlight?"), stop, or give it to someone present. While this tab holds it,
// the round this tab views is the spotlight round: moves trail navigation by 150 ms. A tab of the holder
// that is not bound binds by moving; the server refuses that with not_holder when another tab is bound.

import { h } from '../core/h.js';
import { createAvatar } from '../ui/avatar.js';
import { createButton } from '../ui/button.js';
import { confirmDialog } from '../ui/confirm.js';
import { createDialog } from '../ui/dialog.js';
import { createMenu } from '../ui/menu.js';
import { isObserver } from './model.js';
import { personOf } from './strip.js';

export const MOVE_TRAIL_MS = 150;

/**
 * Where this tab stands: 'none' (nobody holds it), 'bound' (this tab drives it), 'mine' (I hold it,
 * this tab is not bound yet), 'elsewhere' (another tab of mine is bound) or 'other' (someone else).
 */
export function spotlightRole(view, me, elsewhere = false) {
  const holder = view.spotlight?.holder ?? null;
  if (holder === null) {
    return 'none';
  }
  if (holder !== me) {
    return 'other';
  }
  if (elsewhere) {
    return 'elsewhere';
  }
  return view.heldByThisTab ? 'bound' : 'mine';
}

/** People the spotlight can be given to: present, not the holder. */
export function giveCandidates(view) {
  const holder = view.spotlight?.holder ?? null;
  return view.presence.filter((p) => p.pid !== holder);
}

export function createSpotlight(ctx) {
  const avatarSlot = h('span', { class: 'nt-spotlight-holder', hidden: true });
  const button = createButton({ label: 'Spotlight', icon: 'spotlight', variant: 'gold', press: true, size: 'sm' });
  const el = h('div', { class: 'nt-control nt-spotlight' }, avatarSlot, button.el);
  ctx.slots.toolbar.append(el);
  const menu = createMenu({ items: [], label: 'Spotlight', placement: 'bottom-end', onSelect: (action) => actions[action]?.() });
  menu.attach(button.el);
  // Items are built on the way open: the menu's own click handler runs after this one.
  button.el.addEventListener('pointerdown', () => menu.update({ items: items() }), true);
  button.el.addEventListener('keydown', () => menu.update({ items: items() }), true);

  let view = null;
  let viewed = null;
  let avatar = null;
  let elsewhere = false;
  let lastHolder = null;
  let wasBound = false;
  let moveTimer = 0;
  let moveTarget = null;

  const role = () => spotlightRole(view, ctx.me, elsewhere);
  const holderName = () => personOf(view, view.spotlight?.holder)?.name ?? 'Someone';

  function items() {
    if (!view) {
      return [];
    }
    const list = [];
    switch (role()) {
      case 'none':
        list.push({ action: 'take', label: 'Spotlight this round', icon: 'spotlight' });
        break;
      case 'other':
        list.push({ action: 'take', label: `Take spotlight from ${holderName()}`, icon: 'spotlight' });
        break;
      case 'elsewhere':
        list.push({ action: 'take', label: 'Spotlight from this window', icon: 'spotlight' });
        break;
      default:
        break;
    }
    if (role() !== 'other') {
      list.push({ action: 'give', label: 'Give to…', icon: 'user-plus', disabled: giveCandidates(view).length === 0 });
    }
    if (['bound', 'mine', 'elsewhere'].includes(role())) {
      list.push('separator', { action: 'release', label: 'Stop spotlighting', icon: 'x' });
    }
    return list;
  }

  function take(force = false) {
    if (force) {
      elsewhere = false;
    }
    ctx.room.send('poker.spotlight.take', { roundId: viewed, force }, {
      onNack(nack) {
        if (nack.code !== 'spotlight_held') {
          return false;
        }
        confirmTake(nack.d?.holder ?? null);
        return true;
      },
    });
  }

  async function confirmTake(holder) {
    const mine = holder === ctx.me;
    const ok = await confirmDialog({
      title: 'Take spotlight?',
      message: mine
        ? 'Your other window is spotlighting. This window will drive the spotlight instead.'
        : `${personOf(view, holder)?.name ?? 'Someone'} is spotlighting. Everyone following will see the round you are on.`,
      confirmLabel: 'Take spotlight',
    });
    if (ok && ctx.room) {
      take(true);
    }
  }

  function give() {
    const list = h('div', { class: 'nt-give-list', role: 'list' });
    const dialog = createDialog({
      title: 'Give the spotlight to…',
      description: 'Only people in the session right now can receive it.',
      content: list,
      onClose: () => dialog.destroy(),
    });
    for (const person of giveCandidates(view)) {
      const who = createAvatar({ name: person.name, src: person.avatar ? `/blob/${person.avatar}` : null, size: 'sm' });
      list.append(h('div', { role: 'listitem' }, h('button', {
        type: 'button',
        class: 'nt-give-person',
        on: {
          click() {
            ctx.room.send('poker.spotlight.give', { principalId: person.pid });
            dialog.close();
          },
        },
      }, who.el, h('span', { text: person.pid === ctx.me ? `${person.name} (you)` : person.name }),
      isObserver(view, person.pid) ? h('span', { class: 'nt-give-note', text: 'observer' }) : null)));
    }
    dialog.open();
  }

  const actions = {
    take: () => take(false),
    give,
    release: () => ctx.room.send('poker.spotlight.release', {}),
  };

  function scheduleMove(roundId) {
    moveTarget = roundId;
    if (moveTimer) {
      return;
    }
    moveTimer = setTimeout(() => {
      moveTimer = 0;
      const r = view && role();
      if ((r === 'bound' || r === 'mine') && moveTarget !== view.spotlight.roundId) {
        ctx.room.send('poker.spotlight.move', { roundId: moveTarget }, {
          onNack(nack) {
            if (nack.code === 'not_holder') {
              elsewhere = true;
              render();
              return true;
            }
            return false;
          },
        });
      }
    }, MOVE_TRAIL_MS);
  }

  function render() {
    const spot = view.spotlight;
    const r = role();
    el.dataset.role = r;
    button.update({
      pressed: r === 'bound',
      label: r === 'none' ? 'Spotlight' : `Spotlight: ${holderName()}${r === 'elsewhere' ? ' (another window)' : ''}`,
    });
    avatarSlot.hidden = !spot;
    if (!spot) {
      avatar?.destroy();
      avatar = null;
      return;
    }
    const person = personOf(view, spot.holder);
    const props = { name: person?.name ?? 'Spotlight holder', src: person?.avatar ? `/blob/${person.avatar}` : null, size: 'sm' };
    if (avatar) {
      avatar.update(props);
    } else {
      avatar = createAvatar(props);
      avatarSlot.append(avatar.el);
    }
  }

  return {
    el,
    update(next, roundId) {
      const holder = next.spotlight?.holder ?? null;
      // A refused move marks the binding as elsewhere until the holder changes or this tab claims it.
      if (holder !== lastHolder || (next.heldByThisTab && !wasBound)) {
        elsewhere = false;
      }
      lastHolder = holder;
      wasBound = next.heldByThisTab;
      view = next;
      viewed = roundId;
      render();
      const r = role();
      if ((r === 'bound' || r === 'mine') && roundId !== view.spotlight.roundId && view.rounds.some((x) => x.id === roundId)) {
        scheduleMove(roundId);
      }
    },
    destroy() {
      clearTimeout(moveTimer);
      menu.destroy();
      el.remove();
    },
  };
}
