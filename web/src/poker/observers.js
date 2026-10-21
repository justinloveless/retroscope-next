// Observers (§c.4 `poker.observer.set`): a toolbar toggle for my own observer mode and the checklist
// the settings dialog uses to make anyone in the session an observer or a voter again (parity: any
// participant may set it for anyone).

import { h } from '../core/h.js';
import { createButton } from '../ui/button.js';
import { isObserver } from './model.js';

/** Everyone the session knows: roster, present people and observers, by name. */
export function participants(view) {
  const people = new Map();
  for (const p of [...view.roster, ...view.presence]) {
    if (!people.has(p.pid)) {
      people.set(p.pid, p);
    }
  }
  for (const pid of view.observers) {
    if (!people.has(pid)) {
      people.set(pid, { pid, kind: 'user', name: 'Unknown' });
    }
  }
  return [...people.values()].sort((a, b) => a.name.localeCompare(b.name));
}

export function setObserver(room, principalId, observer) {
  room.send('poker.observer.set', { principalId, observer });
}

/**
 * The checklist: one checkbox per participant, checked for observers. `changes()` lists the principals
 * whose box differs from `view` as [{principalId, observer}].
 */
export function createObserverList(view, me) {
  const boxes = new Map();
  const list = h('ul', { class: 'nt-observer-list' });
  for (const person of participants(view)) {
    const box = h('input', { type: 'checkbox', checked: isObserver(view, person.pid), name: 'observer', dataset: { pid: person.pid } });
    boxes.set(person.pid, box);
    list.append(h('li', {}, h('label', { class: 'nt-observer-option' }, box,
      h('span', { text: person.pid === me ? `${person.name} (you)` : person.name }))));
  }
  return {
    el: h('fieldset', { class: 'nt-observers' }, h('legend', { text: 'Observers' }),
      h('p', { class: 'nt-help', text: 'Observers watch without a seat at the table.' }),
      list),
    changes(current) {
      return [...boxes].filter(([pid, box]) => box.checked !== isObserver(current, pid))
        .map(([principalId, box]) => ({ principalId, observer: box.checked }));
    },
  };
}

/** The toolbar's observer toggle for the viewer. */
export function createObserverToggle(ctx) {
  let observing = false;
  const button = createButton({
    label: 'Observer mode',
    icon: 'eye',
    iconOnly: true,
    variant: 'default',
    press: true,
    size: 'sm',
    pressed: false,
    onClick: () => setObserver(ctx.room, ctx.me, !observing),
  });
  const el = h('div', { class: 'nt-control nt-observer-toggle' }, button.el);
  ctx.slots.toolbar.append(el);
  return {
    el,
    update(view) {
      const next = isObserver(view, ctx.me);
      if (next !== observing) {
        observing = next;
        button.update({ pressed: observing, icon: observing ? 'eye-off' : 'eye', label: observing ? 'Stop observing' : 'Observer mode' });
      }
    },
    destroy() {
      el.remove();
    },
  };
}
