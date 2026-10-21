// The board screen: header (title, presence), banners, the columns with pointer and keyboard reorder,
// and one delegated click handler for every card, comment and column action (DESIGN §g.3). Features of
// later units (stages, timer, focus, action items, kudos) plug in through `features`.

import { h, replaceContent } from '../core/h.js';
import { http } from '../core/http.js';
import { reconcile } from '../core/list.js';
import { shallowEq } from '../core/store.js';
import { createAvatar } from '../ui/avatar.js';
import { createButton } from '../ui/button.js';
import { confirmDialog } from '../ui/confirm.js';
import { createDialog } from '../ui/dialog.js';
import { icon } from '../ui/icons.js';
import { toast } from '../ui/toast.js';
import { intent, isFacilitator, uuidv7 } from './model.js';
import { COLUMN_COLORS, createColumn } from './column.js';
import { avatarSrc } from './card.js';

const PENDING_SPINNER_MS = 1500;
const OFFLINE_BANNER_MS = 10_000;
const DRAG_THRESHOLD_PX = 8;
const LONG_PRESS_MS = 300;
const MAX_PRESENCE = 6;
const NEW_COLUMN_COLORS = ['purple', 'orange', 'pink', 'indigo'];

const UPLOAD_MESSAGES = {
  board_quota: 'This board has no room for more images.',
  too_large: 'Images can be at most 2 MB.',
  unsupported_media_type: 'Only PNG, JPEG, GIF and WebP images can be added.',
  storage_full: 'Image storage is full. Please tell an administrator.',
  stage_forbids: 'The current stage does not allow new content.',
  board_archived: 'This board is archived.',
  rate_limited: 'Too many uploads. Try again later.',
  network: 'The image could not be uploaded: you appear to be offline.',
};

const UPLOAD_STATUS = { 409: 'board_quota', 413: 'too_large', 415: 'unsupported_media_type', 507: 'storage_full' };

const KICKED = {
  access_revoked: 'You no longer have access to this board.',
  deleted: 'This board was deleted.',
  not_found: 'This board does not exist.',
  forbidden: 'You do not have access to this board.',
  too_many_rooms: 'Too many boards are open in this tab. Close one and reload.',
};

export function uploadMessage(error) {
  return UPLOAD_MESSAGES[error?.code] ?? UPLOAD_MESSAGES[UPLOAD_STATUS[error?.status]] ?? 'The image could not be uploaded.';
}

// Drafts live in sessionStorage: per tab, so they survive reloads and tab switches but not the session.
const drafts = {
  read(key) {
    try {
      const draft = JSON.parse(sessionStorage.getItem(key) ?? 'null');
      return typeof draft?.body === 'string' ? draft : null;
    } catch {
      return null;
    }
  },
  write(key, body, anonymous) {
    try {
      if (body.trim() === '') {
        sessionStorage.removeItem(key);
      } else {
        sessionStorage.setItem(key, JSON.stringify({ body, anonymous }));
      }
    } catch {
      // Without storage a draft lives as long as the page.
    }
  },
};

function capitalize(text) {
  return text.charAt(0).toUpperCase() + text.slice(1);
}

/** Moves `id` to `index` among the other ids; null when that changes nothing. */
export function moveId(ids, id, index) {
  if (!ids.includes(id)) {
    return null;
  }
  const others = ids.filter((x) => x !== id);
  const order = [...others.slice(0, index), id, ...others.slice(index)];
  return order.every((x, i) => x === ids[i]) ? null : order;
}

export function createBoard({ room, socket, me, boardId, features = [] }) {
  const columns = new Map();
  const commentCounts = new WeakMap();
  const cleanups = [];

  const ctx = {
    boardId,
    features,
    me,
    // The tab's socket, so features that join another room (the team link) share its cid.
    socket,
    view: () => room.view,
    now: () => socket.clock.serverNow(),
    send(k, d, options = {}) {
      const at = ctx.now();
      return room.send(k, d, { apply: (state) => intent(state, k, d, { me: me(), now: at }), ...options });
    },
    command: (k, d, options) => room.send(k, d, options),
    mentions: () => (room.view?.members ?? []).map((m) => ({ id: m.pid, name: m.name })),
    person(pid) {
      const view = room.view;
      return view?.members?.find((m) => m.pid === pid) ?? view?.presence.get(pid) ?? null;
    },
    personName: (pid) => ctx.person(pid)?.name ?? null,
    commentCount(view, cardId) {
      let counts = commentCounts.get(view.comments);
      if (!counts) {
        counts = new Map();
        for (const comment of view.comments.values()) {
          counts.set(comment.cardId, (counts.get(comment.cardId) ?? 0) + 1);
        }
        commentCounts.set(view.comments, counts);
      }
      return counts.get(cardId) ?? 0;
    },
    // The upload body is the raw file; the server sniffs the type (§d).
    upload: async (file) => (await http.post(`/api/retro/${boardId}/images`, file)).blobId,
    uploadFailed: (error) => toast(uploadMessage(error), { variant: 'error' }),
    drafts,
    draftKey: (key) => `rs.draft:${boardId}:${key}`,
    columnAction,
  };

  const title = h('h1', { class: 'retro-title' });
  const renameButton = h('button', { type: 'button', class: 'btn btn-ghost btn-icon btn-sm', 'aria-label': 'Rename board', title: 'Rename board', dataset: { action: 'rename' } }, icon('pencil'));
  const titleRow = h('div', { class: 'retro-title-row' }, title, renameButton);
  const presence = h('div', { class: 'retro-presence' });
  const presenceCount = h('span', { class: 'retro-presence-count' });
  const toolbar = h('div', { class: 'retro-toolbar' });
  const header = h('header', { class: 'retro-header' }, titleRow, h('span', { class: 'retro-spacer' }), toolbar, h('div', { class: 'retro-presence-wrap' }, presence, presenceCount));
  const archived = h('p', { class: 'retro-banner retro-banner-archived', role: 'status', hidden: true }, icon('archive'), ' This board is archived and read-only.');
  const offline = h('p', { class: 'retro-banner retro-banner-offline', role: 'status', hidden: true }, icon('loader-circle'), ' Reconnecting… your changes are saved and will sync.');
  const kicked = h('p', { class: 'retro-banner retro-banner-kicked', role: 'alert', hidden: true });
  const top = h('div', { class: 'retro-top' });
  const before = h('div', { class: 'retro-before' });
  const list = h('div', { class: 'retro-column-list' });
  const addColumn = h('button', { type: 'button', class: 'btn btn-outline retro-add-column', dataset: { action: 'add-column' } }, icon('plus'), 'Add column');
  const row = h('div', { class: 'retro-columns' }, before, list, addColumn);
  const el = h('div', { class: 'retro-board' }, header, archived, offline, kicked, top, row);

  const mounted = features.map((feature) => feature.board?.(ctx, { toolbar, top, before })).filter(Boolean);

  function renderPresence(view) {
    const people = [...view.presence.values()];
    reconcile(presence, people.slice(0, MAX_PRESENCE), (p) => p.pid,
      (p) => createAvatar({ name: p.name, src: avatarSrc(p.avatar), size: 'sm' }).el,
      (node, p) => {
        node.setAttribute('aria-label', p.name);
        node.title = p.name;
      });
    presenceCount.textContent = people.length > MAX_PRESENCE ? `+${people.length - MAX_PRESENCE}` : '';
    presence.setAttribute('aria-label', `${people.length} online`);
  }

  function render([view, kickCode]) {
    kicked.hidden = kickCode === null;
    kicked.textContent = kickCode === null ? '' : KICKED[kickCode] ?? 'You were removed from this board.';
    if (!view) {
      return;
    }
    const facilitator = isFacilitator(view, me()) && !view.meta.archived;
    if (!title.querySelector('input')) {
      title.textContent = view.meta.title;
    }
    document.title = `${view.meta.title} · Retroscope`;
    renameButton.hidden = !facilitator;
    archived.hidden = !view.meta.archived;
    addColumn.hidden = !facilitator;
    renderPresence(view);
    reconcile(list, view.columns, (c) => c.id, (column) => {
      const component = createColumn(column, ctx);
      columns.set(column.id, component);
      return component.el;
    }, (node, column) => columns.get(column.id).update(column, view), {
      flip: true,
      remove(node, done) {
        columns.get(node.dataset.id)?.destroy();
        columns.delete(node.dataset.id);
        done();
      },
    });
    for (const feature of mounted) {
      feature.update?.(view);
    }
  }

  function cardComponent(cardId) {
    const card = room.view?.cards.get(cardId);
    return card ? columns.get(card.columnId)?.card(cardId) : null;
  }

  function startRename() {
    const view = room.view;
    const input = h('input', { class: 'input retro-title-input', value: view.meta.title, maxlength: 200, 'aria-label': 'Board title' });
    let done = false;
    const finish = (save) => {
      if (done) {
        return;
      }
      done = true;
      const next = input.value.trim();
      const current = room.view.meta.title;
      if (save && next !== '' && next !== current) {
        ctx.command('retro.board.rename', { title: next, expected: current });
      }
      title.textContent = current;
      renameButton.focus();
    };
    input.addEventListener('keydown', (event) => {
      if (event.key === 'Enter') {
        event.preventDefault();
        finish(true);
      } else if (event.key === 'Escape') {
        event.preventDefault();
        finish(false);
      }
    });
    input.addEventListener('blur', () => finish(true));
    replaceContent(title, input);
    input.focus();
    input.select();
  }

  function columnDialog(column) {
    const view = room.view;
    const titleInput = h('input', { class: 'input', value: column?.title ?? '', maxlength: 100, required: true });
    const chosen = column?.color ?? NEW_COLUMN_COLORS[view.columns.length % NEW_COLUMN_COLORS.length];
    const swatches = COLUMN_COLORS.map((c) => h('label', { class: 'retro-swatch', dataset: { color: c } },
      h('input', { type: 'radio', name: 'color', value: c, checked: c === chosen }),
      h('span', { class: 'sr-only', text: capitalize(c) })));
    const pickedColor = () => swatches.map((swatch) => swatch.firstChild).find((input) => input.checked)?.value ?? chosen;
    const save = () => {
      const text = titleInput.value.trim();
      if (text === '') {
        titleInput.setAttribute('aria-invalid', 'true');
        titleInput.focus();
        return;
      }
      const color = pickedColor();
      if (!column) {
        ctx.command('retro.column.add', { columnId: uuidv7(ctx.now()), title: text, color });
      } else {
        const d = { columnId: column.id, baseVersion: column.version };
        if (text !== column.title) {
          d.title = text;
        }
        if (color !== column.color) {
          d.color = color;
        }
        if (d.title !== undefined || d.color !== undefined) {
          ctx.command('retro.column.update', d);
        }
      }
      dialog.close();
    };
    const form = h('form', {
      class: 'retro-column-form',
      on: {
        submit(event) {
          event.preventDefault();
          save();
        },
      },
    },
    h('label', { class: 'field' }, h('span', { class: 'field-label', text: 'Title' }), titleInput),
    h('fieldset', { class: 'field retro-swatches' }, h('legend', { class: 'field-label', text: 'Color' }), swatches));
    const dialog = createDialog({
      title: column ? 'Edit column' : 'Add column',
      content: form,
      actions: [
        createButton({ label: 'Cancel', variant: 'secondary', onClick: () => dialog.close() }),
        createButton({ label: column ? 'Save' : 'Add column', variant: 'primary', onClick: save }),
      ],
      onClose: () => dialog.destroy(),
    });
    dialog.open();
    titleInput.focus();
  }

  async function columnAction(action, columnId) {
    const column = room.view?.columns.find((c) => c.id === columnId);
    if (!column) {
      return;
    }
    if (action === 'edit-column') {
      columnDialog(column);
    } else if (action === 'set-action-items' || action === 'unset-action-items') {
      ctx.command('retro.column.setActionItems', { columnId: action === 'set-action-items' ? columnId : null });
    } else if (action === 'delete-column') {
      const ok = await confirmDialog({
        title: `Delete “${column.title}”?`,
        message: 'Its cards and their comments are deleted too. Action items created from them stay on the team page.',
        confirmLabel: 'Delete column',
        destructive: true,
      });
      if (ok) {
        ctx.command('retro.column.delete', { columnId });
      }
    }
  }

  function reorder(columnId, index) {
    const order = moveId(room.view.columns.map((c) => c.id), columnId, index);
    if (!order) {
      return;
    }
    const applyFor = (d) => (state) => intent(state, 'retro.column.reorder', d, {});
    room.send('retro.column.reorder', { order }, {
      apply: applyFor({ order }),
      applyFor,
      rebase(view) {
        const next = moveId(view.columns.map((c) => c.id), columnId, index);
        return next ? { order: next } : null;
      },
    });
  }

  const actions = {
    rename: startRename,
    vote(cardId) {
      const view = room.view;
      ctx.send('retro.vote.set', { cardId, on: !view.myVotes.has(cardId) });
    },
    comments: (cardId) => cardComponent(cardId)?.toggleComments(),
    'edit-card': (cardId) => cardComponent(cardId)?.startEdit(),
    async 'delete-card'(cardId) {
      const ok = await confirmDialog({ title: 'Delete this card?', message: 'Its comments are deleted too.', confirmLabel: 'Delete card', destructive: true });
      if (ok) {
        ctx.send('retro.card.delete', { cardId });
      }
    },
    'delete-comment': (commentId) => ctx.send('retro.comment.delete', { commentId }),
    'add-card': (columnId) => columns.get(columnId)?.addCard(),
    'add-column': () => columnDialog(null),
  };

  el.addEventListener('click', (event) => {
    const target = event.target.closest('[data-action]');
    if (target && el.contains(target) && !target.disabled && Object.hasOwn(actions, target.dataset.action)) {
      actions[target.dataset.action](target.dataset.id, target);
    }
  });

  // Column reorder: Pointer Events on the grip (long press on touch, 8 px on a mouse), so it works on
  // mobile where HTML5 drag and drop does not (DESIGN §g.8).
  list.addEventListener('pointerdown', (event) => {
    const grip = event.target.closest('.retro-column-grip');
    if (!grip || event.button !== 0) {
      return;
    }
    const columnEl = grip.closest('.retro-column');
    const columnId = columnEl.dataset.id;
    const startX = event.clientX;
    const startY = event.clientY;
    let active = false;
    let index = null;
    const touch = event.pointerType !== 'mouse';
    const timer = touch ? setTimeout(() => activate(), LONG_PRESS_MS) : 0;
    grip.setPointerCapture?.(event.pointerId);

    function activate() {
      active = true;
      columnEl.classList.add('retro-column-dragging');
    }

    function others() {
      return [...list.children].filter((node) => node !== columnEl && node.classList.contains('retro-column'));
    }

    function move(e) {
      const distance = Math.hypot(e.clientX - startX, e.clientY - startY);
      if (!active) {
        if (touch && distance > DRAG_THRESHOLD_PX) {
          end();
        } else if (!touch && distance > DRAG_THRESHOLD_PX) {
          activate();
        }
        if (!active) {
          return;
        }
      }
      e.preventDefault();
      const rest = others();
      index = rest.filter((node) => {
        const rect = node.getBoundingClientRect();
        return rect.left + rect.width / 2 < e.clientX;
      }).length;
      rest.forEach((node, i) => node.classList.toggle('retro-column-target', i === index || (i === index - 1 && index === rest.length)));
    }

    function end(commit = false) {
      clearTimeout(timer);
      grip.removeEventListener('pointermove', move);
      grip.removeEventListener('pointerup', up);
      grip.removeEventListener('pointercancel', cancel);
      columnEl.classList.remove('retro-column-dragging');
      for (const node of list.children) {
        node.classList.remove('retro-column-target');
      }
      if (commit && active && index !== null) {
        reorder(columnId, index);
      }
    }

    const up = () => end(true);
    const cancel = () => end(false);
    grip.addEventListener('pointermove', move);
    grip.addEventListener('pointerup', up);
    grip.addEventListener('pointercancel', cancel);
  });

  list.addEventListener('keydown', (event) => {
    const grip = event.target.closest('.retro-column-grip');
    if (!grip || (event.key !== 'ArrowLeft' && event.key !== 'ArrowRight')) {
      return;
    }
    event.preventDefault();
    const columnId = grip.dataset.id;
    const index = room.view.columns.findIndex((c) => c.id === columnId);
    reorder(columnId, Math.max(0, index + (event.key === 'ArrowLeft' ? -1 : 1)));
    requestAnimationFrame(() => columns.get(columnId)?.grip.focus());
  });

  // An entry still unanswered after 1.5 s shows a spinner on its element (§g.4).
  const pendingTimer = setInterval(() => {
    const now = Date.now();
    const slow = new Set(room.pending().filter((e) => now - e.sentAt > PENDING_SPINNER_MS)
      .map((e) => e.d.commentId ?? e.d.cardId ?? e.d.columnId).filter(Boolean));
    for (const node of el.querySelectorAll('[data-id].retro-pending')) {
      if (!slow.has(node.dataset.id)) {
        node.classList.remove('retro-pending');
        node.removeAttribute('aria-busy');
      }
    }
    for (const id of slow) {
      for (const node of el.querySelectorAll(`li[data-id="${CSS.escape(id)}"], section[data-id="${CSS.escape(id)}"]`)) {
        node.classList.add('retro-pending');
        node.setAttribute('aria-busy', 'true');
      }
    }
    const status = socket.status.get();
    offline.hidden = status.state === 'open' || status.offlineSince === null || now - status.offlineSince <= OFFLINE_BANNER_MS;
  }, 500);
  cleanups.push(() => clearInterval(pendingTimer));

  cleanups.push(room.select(() => [room.view, room.kicked], shallowEq, render));
  render([room.view, room.kicked]);
  // Nearly every visit types something, so the composer chunk is fetched once the board has painted.
  const prefetch = setTimeout(() => import('./composer.js').catch(() => {}), 0);
  cleanups.push(() => clearTimeout(prefetch));

  return {
    el,
    ctx,
    destroy() {
      for (const cleanup of cleanups) {
        cleanup();
      }
      for (const feature of mounted) {
        feature.destroy?.();
      }
      for (const column of columns.values()) {
        column.destroy();
      }
      el.remove();
    },
  };
}
