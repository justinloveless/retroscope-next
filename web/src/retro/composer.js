// The card and comment composer: the shared markup editor, an anonymous toggle, and a draft per column
// or card, so switching tabs, reloading or a refused post never loses what was typed (R21). This module
// is loaded lazily and carries the editor with it, keeping both out of the board's first paint.

import { h } from '../core/h.js';
import { createEditor } from '../markup/editor.js';
import { checkMarkup } from '../markup/parse.js';
import { createButton } from '../ui/button.js';

export { createEditor };

const LIMIT_MESSAGES = {
  too_long: { card: 'Cards can be at most 8,000 characters.', comment: 'Comments can be at most 4,000 characters.' },
  too_many_images: 'A post can hold at most 8 images.',
  invalid_character: 'The text contains a character that cannot be saved.',
};

/**
 * props: drafts {read(key), write(key, body, anonymous)}, draftKey, field ('card' | 'comment'), label,
 * placeholder, submitLabel, mentions() → [{id, name}], canPostAnonymously() → bool,
 * upload(file) → Promise<blobId>, uploadFailed(error), onSubmit(body, anonymous) → true when sent,
 * onCancel() (shows a Cancel button).
 */
export function createComposer(props) {
  const draft = props.drafts.read(props.draftKey);

  const editor = createEditor({
    value: draft?.body ?? '',
    mentions: props.mentions,
    label: props.label,
    placeholder: props.placeholder,
    onSubmit: send,
    onChange() {
      showError(null);
      save();
    },
    uploadImage: props.upload,
    onError: props.uploadFailed,
  });
  const error = h('p', { class: 'retro-composer-error', role: 'alert', hidden: true });
  const anonymous = h('input', { type: 'checkbox', checked: draft?.anonymous ?? false, on: { change: save } });
  const anonymousField = h('label', { class: 'retro-composer-anonymous' }, anonymous, h('span', { text: 'Post anonymously' }));
  const submit = createButton({ label: props.submitLabel ?? 'Add', variant: 'primary', size: 'sm', type: 'submit' });
  const cancel = props.onCancel ? createButton({ label: 'Cancel', variant: 'ghost', size: 'sm', onClick: discard }) : null;
  const el = h('form', {
    class: 'retro-composer',
    on: {
      submit(event) {
        event.preventDefault();
        send(editor.getValue());
      },
    },
  }, editor.el, error, h('div', { class: 'retro-composer-row' }, anonymousField, h('span', { class: 'retro-spacer' }), cancel, submit));

  function canAnonymous() {
    return props.canPostAnonymously?.() ?? false;
  }

  function save() {
    props.drafts.write(props.draftKey, editor.getValue(), anonymous.checked);
  }

  function showError(message) {
    error.textContent = message ?? '';
    error.hidden = !message;
  }

  function clear() {
    editor.setValue('');
    anonymous.checked = false;
    showError(null);
    save();
  }

  function discard() {
    clear();
    props.onCancel();
  }

  function send(body) {
    const check = checkMarkup(body, props.field);
    if (check.error === 'empty') {
      return;
    }
    if (check.error) {
      const message = LIMIT_MESSAGES[check.error];
      showError(typeof message === 'string' ? message : message[props.field]);
      return;
    }
    if (props.onSubmit(body, canAnonymous() && anonymous.checked)) {
      clear();
      editor.focus();
    }
  }

  function refresh() {
    anonymousField.hidden = !canAnonymous();
  }

  refresh();
  return {
    el,
    refresh,
    focus: () => editor.focus(),
    /** Puts a refused post back (R21), after whatever was typed since. */
    restore(body, wasAnonymous) {
      const current = editor.getValue();
      editor.setValue(current.trim() === '' ? body : `${current}\n\n${body}`);
      anonymous.checked = wasAnonymous;
      save();
    },
    destroy() {
      editor.destroy();
      el.remove();
    },
  };
}
