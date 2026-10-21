// Share (DESIGN §d `PATCH /api/retro/{id}`, §0.1 private boards): the board's link for everyone, and who may
// open it for the people who manage the board. Board passwords are bcrypt-hashed on the server, so a
// password can be shown only right after it was set, to the person who typed it; a new password signs
// out everyone who unlocked the board with the old one.

import { h, replaceContent } from '../core/h.js';
import { ApiError, http } from '../core/http.js';
import { createButton } from '../ui/button.js';
import { createDialog } from '../ui/dialog.js';
import { icon } from '../ui/icons.js';

const MIN_PASSWORD = 4;
const MAX_PASSWORD = 72;
const COPIED_MS = 2000;

const ACCESS = {
  link: { label: 'Anyone with the link', icon: 'link' },
  team: { label: 'Team members only', icon: 'users' },
  password: { label: 'People with the password', icon: 'lock' },
};

/**
 * BoardManage (§d): a team board's admins and owner, or a quick board's creator. Quick boards do not tell
 * the page who created them, so every signed-in user is offered the controls and the server decides.
 */
export function mayManage(view, me) {
  if (me?.kind !== 'user') {
    return false;
  }
  if (!view.teamId) {
    return true;
  }
  const role = view.members?.find((m) => m.pid === me.pid)?.role;
  return role === 'admin' || role === 'owner';
}

export function boardUrl(code) {
  return `${location.origin}/retro/${encodeURIComponent(code)}`;
}

function copyField(value, label) {
  const input = h('input', { class: 'input retro-share-value', readonly: true, value, 'aria-label': label });
  const button = createButton({ label: 'Copy', icon: 'copy', variant: 'secondary', size: 'sm' });
  let reset = 0;
  button.el.addEventListener('click', async () => {
    clearTimeout(reset);
    try {
      await navigator.clipboard.writeText(input.value);
      button.update({ label: 'Copied', icon: 'check' });
    } catch {
      input.select();
      button.update({ label: 'Press Ctrl+C', icon: 'copy' });
    }
    reset = setTimeout(() => button.update({ label: 'Copy', icon: 'copy' }), COPIED_MS);
  });
  input.addEventListener('focus', () => input.select());
  return {
    el: h('div', { class: 'retro-share-row' }, input, button),
    set(next) {
      input.value = next;
    },
  };
}

function problemMessage(failure) {
  if (failure instanceof ApiError) {
    if (failure.status === 403) {
      return 'Only the board\'s creator or a team admin can change who may open it.';
    }
    if (failure.code === 'network') {
      return 'The server could not be reached. Try again.';
    }
  }
  return 'That change could not be saved.';
}

function openDialog(ctx, boardId, onClosed) {
  const view = ctx.view();
  const manage = mayManage(view, ctx.me());
  const link = copyField(boardUrl(view.code), 'Board link');
  const status = h('p', { class: 'retro-share-status' });
  const options = Object.entries(ACCESS)
    .filter(([value]) => value !== 'team' || view.teamId)
    .map(([value, { label, icon: name }]) => h('label', { class: 'retro-share-option' },
      h('input', { type: 'radio', name: 'access', value, checked: value === view.meta.visibility }),
      icon(name), h('span', { text: label })));
  const password = h('input', {
    class: 'input', type: 'password', autocomplete: 'new-password', minlength: MIN_PASSWORD, maxlength: MAX_PASSWORD,
    'aria-label': 'New board password',
  });
  const passwordHelp = h('span', { class: 'retro-config-help' });
  const passwordField = h('label', { class: 'field retro-share-password' },
    h('span', { class: 'field-label', text: 'Password' }), password, passwordHelp);
  const error = h('p', { class: 'retro-composer-error', role: 'alert', hidden: true });
  const reveal = h('div', { class: 'retro-share-reveal', role: 'status', hidden: true });
  const save = createButton({ label: 'Save', variant: 'primary', type: 'submit' });
  const access = h('form', {
    class: 'retro-share-access',
    on: {
      change: paint,
      submit(event) {
        event.preventDefault();
        submit();
      },
    },
  }, h('fieldset', { class: 'retro-config-group' }, h('legend', { class: 'field-label', text: 'Who can open this board' }), options),
  passwordField, error, h('div', { class: 'retro-share-actions' }, save));

  // The checks above give clearer messages than the browser's bubbles.
  access.noValidate = true;

  function chosen() {
    return options.map((option) => option.firstChild).find((input) => input.checked)?.value ?? view.meta.visibility;
  }

  function paint() {
    const visibility = ctx.view().meta.visibility;
    const target = chosen();
    passwordField.hidden = target !== 'password';
    passwordHelp.textContent = visibility === 'password'
      ? 'Leave blank to keep the current password. A new one signs out everyone who used the old one.'
      : `${MIN_PASSWORD} to ${MAX_PASSWORD} characters. You will see it once, to share it.`;
    status.textContent = visibility === 'password'
      ? 'This board needs a password. Share it along with the link.'
      : visibility === 'team' ? 'Only members of the team can open this board.' : 'Anyone with the link can open this board.';
  }

  async function submit() {
    const visibility = ctx.view().meta.visibility;
    const target = chosen();
    const body = {};
    if (target !== visibility) {
      body.visibility = target;
    }
    if (target === 'password' && (password.value !== '' || visibility !== 'password')) {
      if (password.value.length < MIN_PASSWORD) {
        error.hidden = false;
        error.textContent = password.value === '' ? 'Choose a password.' : `Use at least ${MIN_PASSWORD} characters.`;
        password.focus();
        return;
      }
      body.password = password.value;
    }
    if (Object.keys(body).length === 0) {
      dialog.close();
      return;
    }
    error.hidden = true;
    save.update({ busy: true, disabled: true });
    try {
      await http.patch(`/api/retro/${boardId}`, body);
    } catch (failure) {
      error.hidden = false;
      error.textContent = problemMessage(failure);
      return;
    } finally {
      save.update({ busy: false, disabled: false });
    }
    if (body.password) {
      replaceContent(reveal,
        h('p', { text: 'Share this password with the people you invite. It will not be shown again.' }),
        copyField(body.password, 'Board password').el);
      reveal.hidden = false;
      password.value = '';
    } else {
      dialog.close();
    }
  }

  paint();
  const dialog = createDialog({
    title: 'Share this board',
    className: 'retro-share',
    content: [
      h('div', { class: 'field' }, h('span', { class: 'field-label', text: 'Link' }), link.el),
      status,
      manage ? access : null,
      reveal,
    ],
    onClose() {
      dialog.destroy();
      onClosed();
    },
  });
  dialog.open();
  return { paint };
}

export const shareFeature = {
  board(ctx, { toolbar }) {
    let open = null;
    const button = createButton({
      label: 'Share',
      icon: 'share-2',
      variant: 'outline',
      size: 'sm',
      className: 'retro-share-open',
      onClick() {
        open = openDialog(ctx, ctx.boardId, () => {
          open = null;
        });
      },
    });
    toolbar.append(button.el);
    return {
      update() {
        open?.paint();
      },
      destroy() {
        button.destroy();
      },
    };
  },
};
