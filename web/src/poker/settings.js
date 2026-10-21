// Session Configuration (§c.4 `poker.settings.update`): presence detection, following the spotlight,
// observers, and clearing every round. Settings changes carry the values the dialog opened with as
// `expected`, so a concurrent change is refused `stale` instead of overwritten (R5).

import { h } from '../core/h.js';
import { createButton } from '../ui/button.js';
import { confirmDialog } from '../ui/confirm.js';
import { createDialog } from '../ui/dialog.js';
import { createSwitch } from '../ui/switch.js';
import { uuidv7 } from './model.js';
import { createObserverList, setObserver } from './observers.js';
import { mayDeleteRounds } from './strip.js';

const FIELDS = ['presenceEnabled', 'followSpotlight'];

/** The `poker.settings.update` payload for `values` against `opened`, or null when nothing changed. */
export function settingsPatch(opened, values) {
  const changed = FIELDS.filter((field) => values[field] !== opened[field]);
  if (changed.length === 0) {
    return null;
  }
  const patch = { expected: {} };
  for (const field of changed) {
    patch[field] = values[field];
    patch.expected[field] = opened[field];
  }
  return patch;
}

function help(text) {
  return h('p', { class: 'nt-help', text });
}

export function createSettings(ctx) {
  const button = createButton({ label: 'Session settings', icon: 'settings', iconOnly: true, variant: 'default', press: true, size: 'sm', onClick: open });
  const el = h('div', { class: 'nt-control nt-settings' }, button.el);
  ctx.slots.toolbar.append(el);
  let view = null;
  let dialog = null;

  function open() {
    if (!view || dialog) {
      return;
    }
    const opened = { presenceEnabled: view.presenceEnabled, followSpotlight: view.followSpotlight };
    const values = { ...opened };
    const presence = createSwitch({ label: 'Enable presence detection', checked: opened.presenceEnabled, onChange: (on) => { values.presenceEnabled = on; } });
    const follow = createSwitch({ label: 'Follow spotlight ticket', checked: opened.followSpotlight, onChange: (on) => { values.followSpotlight = on; } });
    const observers = createObserverList(view, ctx.me);
    const danger = mayDeleteRounds(view)
      ? h('section', { class: 'nt-danger' },
        h('h3', { text: 'Danger zone' }),
        help('Deletes every round and its votes, then starts again at round 1.'),
        createButton({ label: 'Delete all rounds', variant: 'destructive', onClick: clearAll }).el)
      : null;
    const form = h('form', {
      class: 'nt-settings-form',
      on: {
        submit(event) {
          event.preventDefault();
          save(opened, values, observers);
        },
      },
    },
    presence.el, help('Absent players are dimmed and do not hold up an automatic reveal.'),
    follow.el, help('Everyone else’s view follows the round the spotlight holder is on.'),
    observers.el,
    danger);
    dialog = createDialog({
      title: 'Session Configuration',
      className: 'nt-settings-dialog',
      content: form,
      actions: [
        createButton({ label: 'Cancel', variant: 'secondary', onClick: () => dialog.close() }).el,
        createButton({ label: 'Save changes', variant: 'primary', onClick: () => form.requestSubmit() }).el,
      ],
      onClose() {
        dialog.destroy();
        dialog = null;
      },
    });
    dialog.open();
  }

  function save(opened, values, observers) {
    const patch = settingsPatch(opened, values);
    if (patch) {
      ctx.room.send('poker.settings.update', patch);
    }
    for (const change of observers.changes(view)) {
      setObserver(ctx.room, change.principalId, change.observer);
    }
    dialog.close();
  }

  async function clearAll() {
    const ok = await confirmDialog({
      title: 'Delete all rounds?',
      message: 'Every round and its votes are deleted for everyone, and the session starts again at round 1.',
      confirmLabel: 'Delete all rounds',
      destructive: true,
    });
    if (ok) {
      ctx.room.send('poker.rounds.clearAll', { newRoundId: uuidv7() });
      dialog?.close();
    }
  }

  return {
    el,
    open,
    update(next) {
      view = next;
    },
    destroy() {
      dialog?.destroy();
      el.remove();
    },
  };
}
