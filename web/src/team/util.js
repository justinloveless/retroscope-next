import { h } from '../core/h.js';
import { http } from '../core/http.js';
import { toast } from '../ui/toast.js';
import { icon } from '../ui/icons.js';
import { createButton } from '../ui/button.js';
import { createDialog } from '../ui/dialog.js';

const MESSAGES = {
  network: 'The server could not be reached.',
  forbidden: 'You do not have permission to do that.',
  unauthenticated: 'Please sign in again.',
  email_unverified: 'Verify your email address first.',
  not_found: 'That no longer exists.',
  rate_limited: 'Too many requests. Try again in a moment.',
  jira_auth_failed: 'Jira rejected the credentials. Use a classic (unscoped) API token.',
  jira_unavailable: 'Jira is not answering right now.',
  jira_not_configured: 'Jira is not configured for this team.',
  outbox_full: 'You have been offline too long; reload to continue.',
  too_many_rooms: 'This tab has too many rooms open. Close some of them, then try again.',
  invalid: 'That was not accepted.',
};

export function errorMessage(error, fallback = 'Something went wrong.') {
  return MESSAGES[error?.code] ?? error?.detail ?? error?.title ?? fallback;
}

export function toastError(error, fallback) {
  if (error?.name !== 'AbortError') {
    toast(errorMessage(error, fallback), { variant: 'error', duration: 6000 });
  }
}

/** HTTP calls scoped to one team's API. */
export function teamApi(teamId) {
  const base = `/api/teams/${encodeURIComponent(teamId)}`;
  return {
    base,
    get: (path, options) => http.get(base + path, options),
    post: (path, body) => http.post(base + path, body ?? {}),
    put: (path, body) => http.put(base + path, body),
    patch: (path, body) => http.patch(base + path, body),
    del: (path, body) => http.request('DELETE', base + path, body === undefined ? {} : { body }),
  };
}

/** Time-ordered UUID (RFC 9562 v7) for client-minted ids. */
export function uuidv7(now = Date.now()) {
  const bytes = crypto.getRandomValues(new Uint8Array(16));
  let ms = now;
  for (let i = 5; i >= 0; i--) {
    bytes[i] = ms % 256;
    ms = Math.floor(ms / 256);
  }
  bytes[6] = (bytes[6] & 0x0f) | 0x70;
  bytes[8] = (bytes[8] & 0x3f) | 0x80;
  const hex = [...bytes].map((b) => b.toString(16).padStart(2, '0')).join('');
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

export function avatarSrc(blobId) {
  return blobId ? `/blob/${blobId}` : null;
}

export function emptyState(iconName, title, text, ...actions) {
  return h('div', { class: 'team-empty' },
    iconName ? icon(iconName, { className: 'team-empty-icon' }) : null,
    h('h3', { text: title }),
    text ? h('p', { text }) : null,
    actions);
}

export function loading(text) {
  return h('p', { class: 'team-muted', role: 'status', text });
}

/** A label wrapping its form control, with an optional hint under it. */
export function field(label, control, hint) {
  return h('label', { class: 'field' },
    h('span', { class: 'field-label', text: label }),
    control,
    hint ? h('span', { class: 'team-hint', text: hint }) : null);
}

export function input(props) {
  return h('input', { class: 'input', type: 'text', ...props });
}

/**
 * A dialog around a form. submit() resolves when done (the dialog closes) or throws an ApiError,
 * which is shown inside the dialog; a field the server names in `fields` is marked invalid.
 */
export function formDialog({ title, description, content, submitLabel, destructive = false, submit }) {
  const error = h('p', { class: 'team-form-error', role: 'alert', hidden: true });
  const form = h('form', { class: 'team-form' }, content, error, h('button', { type: 'submit', class: 'sr-only', tabindex: '-1', 'aria-hidden': 'true' }));
  const cancel = createButton({ label: 'Cancel', variant: 'secondary', onClick: () => dialog.close() });
  const ok = createButton({ label: submitLabel, variant: destructive ? 'destructive' : 'primary' });
  const dialog = createDialog({
    title,
    description,
    content: form,
    actions: [cancel.el, ok.el],
    onClose: () => dialog.destroy(),
  });

  let busy = false;

  async function run(event) {
    event?.preventDefault();
    if (busy || !form.reportValidity()) {
      return;
    }
    busy = true;
    ok.update({ busy: true, disabled: true });
    error.hidden = true;
    try {
      await submit();
      dialog.close('ok');
    } catch (failure) {
      busy = false;
      error.textContent = errorMessage(failure);
      error.hidden = false;
      for (const name of Object.keys(failure?.fields ?? {})) {
        form.elements.namedItem(name)?.setAttribute('aria-invalid', 'true');
      }
      ok.update({ busy: false, disabled: false });
    }
  }

  form.addEventListener('submit', run);
  ok.el.addEventListener('click', run);
  dialog.open();
  form.querySelector('input, select, textarea')?.focus();
  return dialog;
}

/** Confirmation that requires typing `expected` (team and board deletion). */
export function confirmTyped({ title, message, expected, confirmLabel, submit }) {
  const typed = input({ name: 'confirm', autocomplete: 'off', required: true });
  return formDialog({
    title,
    description: message,
    content: field(`Type "${expected}" to confirm`, typed),
    submitLabel: confirmLabel,
    destructive: true,
    async submit() {
      if (typed.value.trim() !== expected) {
        throw Object.assign(new Error('mismatch'), { detail: 'The name does not match.' });
      }
      await submit();
    },
  });
}

export async function copyText(text) {
  try {
    await navigator.clipboard.writeText(text);
    toast('Copied to the clipboard.', { variant: 'success', duration: 2500 });
  } catch {
    toast('Copy failed; select the text and copy it yourself.', { variant: 'error', duration: 4000 });
  }
}

export function card(title, description, ...content) {
  return h('section', { class: 'card team-settings-card' },
    h('h2', { class: 'team-section-title', text: title }),
    description ? h('p', { class: 'team-muted', text: description }) : null,
    content);
}
