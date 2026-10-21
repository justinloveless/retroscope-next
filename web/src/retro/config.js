// Board settings (DESIGN §c.4 `retro.config.update`, R5). The dialog sends only the fields the facilitator
// changed, each with the value they saw, so it never overwrites a change made after it opened. A refused
// update is dropped silently (§g.4); the dialog notices because the board's config moved away from what
// it saw, and shows the fresh values instead. `maxVotes: 0` means unlimited on the wire (U2 decision 5).

import { h } from '../core/h.js';
import { createButton } from '../ui/button.js';
import { createDialog } from '../ui/dialog.js';
import { icon } from '../ui/icons.js';
import { createSwitch } from '../ui/switch.js';
import { isFacilitator } from './model.js';

const SAVE_TIMEOUT_MS = 8000;

const FIELDS = [
  { key: 'allowAnonymous', label: 'Allow anonymous posts', group: 'General' },
  { key: 'showAuthorNames', label: 'Show author names', group: 'General' },
  { key: 'sortChronologically', label: 'Sort cards oldest first by default', group: 'General' },
  { key: 'votingEnabled', label: 'Enable voting', group: 'Voting' },
  { key: 'allowSelfVotes', label: 'Allow votes for your own cards', group: 'Voting' },
  { key: 'maxVotes', label: 'Votes per person', group: 'Voting', text: 'Blank for unlimited' },
  { key: 'voteEmoji', label: 'Vote emoji', group: 'Voting', text: 'Up to 8 characters' },
  { key: 'stagesEnabled', label: 'Structured stages (thinking, voting, discussing)', group: 'Stages' },
  { key: 'enforceReadiness', label: 'Advance only when everyone present is ready', group: 'Stages' },
];

/** The config in `retro.config.update`'s encoding: every field present, unlimited votes as 0. */
export function asPatch(config) {
  return Object.fromEntries(FIELDS.map(({ key }) => [key, key === 'maxVotes' ? config.maxVotes ?? 0 : config[key]]));
}

/** `{patch, expected}` for the fields whose value differs from what the dialog started from. */
export function configUpdate(seen, wanted) {
  const patch = {};
  const expected = {};
  for (const { key } of FIELDS) {
    if (wanted[key] !== seen[key]) {
      patch[key] = wanted[key];
      expected[key] = seen[key];
    }
  }
  return Object.keys(patch).length === 0 ? null : { patch, expected };
}

function openDialog(ctx, onClosed) {
  let seen = asPatch(ctx.view().config);
  let saving = null;
  let timeout = 0;
  const touched = new Set();
  const notice = h('p', { class: 'retro-config-notice', role: 'status', hidden: true });
  const error = h('p', { class: 'retro-composer-error', role: 'alert', hidden: true });
  const inputs = new Map();
  const groups = new Map();

  for (const field of FIELDS) {
    let control;
    if (field.key === 'maxVotes' || field.key === 'voteEmoji') {
      const input = h('input', {
        class: 'input',
        name: field.key,
        maxlength: field.key === 'maxVotes' ? 2 : 16,
        inputmode: field.key === 'maxVotes' ? 'numeric' : null,
        placeholder: field.key === 'maxVotes' ? 'Unlimited' : null,
        on: { input: () => touched.add(field.key) },
      });
      control = {
        el: h('label', { class: 'field' }, h('span', { class: 'field-label', text: field.label }), input,
          h('span', { class: 'retro-config-help', text: field.text })),
        get: () => (field.key === 'maxVotes' ? (input.value.trim() === '' ? 0 : Number(input.value)) : input.value.trim()),
        set: (value) => {
          input.value = field.key === 'maxVotes' ? (value === 0 ? '' : String(value)) : value;
        },
        input,
      };
    } else {
      const toggle = createSwitch({
        label: field.label,
        onChange() {
          touched.add(field.key);
          paintDependent();
        },
      });
      control = { el: toggle.el, get: () => toggle.checked, set: (checked) => toggle.update({ checked }) };
    }
    control.el.dataset.field = field.key;
    inputs.set(field.key, control);
    if (!groups.has(field.group)) {
      groups.set(field.group, h('fieldset', { class: 'retro-config-group' }, h('legend', { class: 'field-label', text: field.group })));
    }
    groups.get(field.group).append(control.el);
  }

  function values() {
    return Object.fromEntries([...inputs].map(([key, control]) => [key, control.get()]));
  }

  function paintDependent() {
    inputs.get('enforceReadiness').el.hidden = !inputs.get('stagesEnabled').get();
  }

  function fill(config, keys) {
    for (const key of keys) {
      inputs.get(key).set(config[key]);
    }
    paintDependent();
  }

  function validate(wanted) {
    if (!Number.isInteger(wanted.maxVotes) || wanted.maxVotes < 0 || wanted.maxVotes > 99) {
      return ['maxVotes', 'Votes per person is a number from 1 to 99, or blank for unlimited.'];
    }
    if (wanted.voteEmoji === '' || [...wanted.voteEmoji].length > 8) {
      return ['voteEmoji', 'The vote emoji is 1 to 8 characters.'];
    }
    return null;
  }

  function settle(message) {
    clearTimeout(timeout);
    saving = null;
    saveButton.update({ busy: false, disabled: false });
    if (message) {
      notice.hidden = false;
      notice.textContent = message;
    }
  }

  // Someone else's change while the dialog is open: untouched fields follow it; a field the facilitator
  // changed keeps their value. While saving, a field that moved to anything but the saved value means the
  // server refuses the update as stale, so the dialog shows what is current and saves nothing.
  function refresh(config) {
    const current = asPatch(config);
    if (saving) {
      const keys = Object.keys(saving.patch);
      if (keys.every((key) => current[key] === saving.patch[key])) {
        clearTimeout(timeout);
        dialog.close();
        return;
      }
      const conflicts = keys.filter((key) => current[key] !== saving.expected[key] && current[key] !== saving.patch[key]);
      if (conflicts.length === 0) {
        return;
      }
      for (const key of conflicts) {
        touched.delete(key);
      }
      const names = conflicts.map((key) => FIELDS.find((f) => f.key === key).label.toLowerCase());
      settle(`Someone else changed ${names.join(', ')} first, so nothing was saved. The latest values are shown; review them and save again.`);
    }
    const followed = FIELDS.map((f) => f.key).filter((key) => !touched.has(key));
    for (const key of followed) {
      seen[key] = current[key];
    }
    fill(current, followed);
  }

  function save() {
    if (saving) {
      return;
    }
    error.hidden = true;
    const wanted = values();
    const invalid = validate(wanted);
    if (invalid) {
      error.hidden = false;
      error.textContent = invalid[1];
      inputs.get(invalid[0]).input?.focus();
      return;
    }
    const update = configUpdate(seen, wanted);
    if (!update) {
      dialog.close();
      return;
    }
    saving = update;
    notice.hidden = true;
    saveButton.update({ busy: true, disabled: true });
    ctx.command('retro.config.update', update, {
      onNack(nack) {
        settle();
        error.hidden = false;
        error.textContent = nack.code === 'forbidden' ? 'Only facilitators can change the settings.' : 'The settings could not be saved.';
        return true;
      },
    });
    timeout = setTimeout(() => {
      settle('The settings were not saved. The latest values are shown; save again to retry.');
      touched.clear();
      refresh(ctx.view().config);
    }, SAVE_TIMEOUT_MS);
  }

  const saveButton = createButton({ label: 'Save', variant: 'primary', onClick: save });
  const form = h('form', {
    class: 'retro-config',
    on: {
      submit(event) {
        event.preventDefault();
        save();
      },
    },
  }, notice, ...groups.values(), error, h('button', { type: 'submit', hidden: true }));
  const dialog = createDialog({
    title: 'Board settings',
    className: 'retro-config-dialog',
    content: form,
    actions: [createButton({ label: 'Cancel', variant: 'secondary', onClick: () => dialog.close() }), saveButton],
    onClose() {
      clearTimeout(timeout);
      dialog.destroy();
      onClosed();
    },
  });
  fill(seen, Object.keys(seen));
  dialog.open();
  return { refresh };
}

export const configFeature = {
  board(ctx, { toolbar }) {
    let open = null;
    const button = h('button', {
      type: 'button', class: 'btn btn-ghost btn-icon btn-sm retro-config-open', 'aria-label': 'Board settings', title: 'Board settings',
      on: {
        click() {
          open = openDialog(ctx, () => {
            open = null;
          });
        },
      },
    }, icon('settings'));
    toolbar.append(button);
    let config = null;
    return {
      update(view) {
        button.hidden = !isFacilitator(view, ctx.me()) || view.meta.archived;
        if (view.config !== config) {
          config = view.config;
          open?.refresh(config);
        }
      },
      destroy() {
        button.remove();
      },
    };
  },
};
