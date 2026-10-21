// One card: rendered markup, author, votes, comments thread, and in-place editing with the merge prompt
// for a `stale` edit (R5). Clicks reach the board's delegated handler through data-action.

import { h, replaceContent } from '../core/h.js';
import { plural } from '../core/fmt.js';
import { checkMarkup, parse } from '../markup/parse.js';
import { render } from '../markup/render.js';
import { createAvatar } from '../ui/avatar.js';
import { createButton } from '../ui/button.js';
import { createDialog } from '../ui/dialog.js';
import { icon } from '../ui/icons.js';
import { allowsContent, allowsVoting, isFacilitator, votesLeft } from './model.js';
import { createComments } from './comments.js';

const MAX_VOTER_AVATARS = 3;

export function renderMarkup(markup, ctx) {
  return render(parse(markup), { resolveMention: ctx.personName });
}

/**
 * Re-renders `host` when the markup changed, or when a body with mentions may show other names
 * (pill names come from the current roster, never from the token).
 */
export function paintMarkup(host, markup, view, ctx) {
  const names = markup.includes('[[mention:') ? [view.members, view.presence] : null;
  const last = host.painted;
  if (last && last.markup === markup && (names === null || (last.names?.[0] === names[0] && last.names?.[1] === names[1]))) {
    return;
  }
  host.painted = { markup, names };
  replaceContent(host, renderMarkup(markup, ctx));
}

export function avatarSrc(avatar) {
  return avatar ? `/blob/${avatar}` : null;
}

export function authorLine(entity) {
  if (entity.author) {
    return h('span', { class: 'retro-author' },
      createAvatar({ name: entity.author.name, src: avatarSrc(entity.author.avatar), size: 'sm' }),
      h('span', { class: 'retro-author-name', text: entity.author.name }),
      entity.author.guest ? h('span', { class: 'badge retro-guest', text: 'guest' }) : null);
  }
  return entity.anonymous ? h('span', { class: 'retro-author retro-anonymous', text: 'Anonymous' }) : null;
}

/** Mounts a composer into `host` once its lazily loaded chunk has arrived. */
export function lazyComposer(host, ctx, props) {
  let composer = null;
  let gone = false;
  const ready = import('./composer.js').then(({ createComposer }) => {
    if (gone) {
      return null;
    }
    composer = createComposer({
      drafts: ctx.drafts,
      mentions: ctx.mentions,
      upload: ctx.upload,
      uploadFailed: ctx.uploadFailed,
      ...props,
    });
    host.replaceChildren(composer.el);
    return composer;
  });
  return {
    ready,
    refresh: () => composer?.refresh(),
    focus: () => ready.then((c) => c?.focus()),
    restore: (body, anonymous) => ready.then((c) => c?.restore(body, anonymous)),
    destroy() {
      gone = true;
      composer?.destroy();
      host.replaceChildren();
    },
  };
}

function iconButton(action, id, name, label) {
  return h('button', { type: 'button', class: 'btn btn-ghost btn-icon btn-sm', 'aria-label': label, title: label, dataset: { action, id } }, icon(name));
}

export function createCard(card, ctx) {
  const id = card.id;
  let current = null;
  let view = null;
  let editor = null;
  let comments = null;
  let extras = [];

  const body = h('div', { class: 'retro-card-body' });
  const author = h('div', { class: 'retro-card-author' });
  const voters = h('span', { class: 'retro-voters' });
  const emoji = h('span', { class: 'retro-vote-emoji', 'aria-hidden': 'true' });
  const count = h('span', { class: 'retro-vote-count' });
  const vote = h('button', { type: 'button', class: 'btn btn-ghost btn-sm retro-vote', dataset: { action: 'vote', id } }, emoji, count);
  const commentCount = h('span', { class: 'retro-comment-count' });
  const toggle = h('button', {
    type: 'button', class: 'btn btn-ghost btn-sm retro-comments-toggle', 'aria-expanded': 'false', dataset: { action: 'comments', id },
  }, icon('message-square'), commentCount);
  const edit = iconButton('edit-card', id, 'pencil', 'Edit card');
  const remove = iconButton('delete-card', id, 'trash', 'Delete card');
  const extraHost = h('span', { class: 'retro-card-extras' });
  const thread = h('div', { class: 'retro-card-thread' });
  const el = h('li', { class: 'retro-card', dataset: { id } },
    body,
    h('div', { class: 'retro-card-meta' }, author,
      h('div', { class: 'retro-card-actions' }, voters, vote, toggle, extraHost, edit, remove)),
    thread);

  for (const feature of ctx.features) {
    const extra = feature.card?.(ctx, card);
    if (extra) {
      extras.push(extra);
      extraHost.append(extra.el);
    }
  }

  function column() {
    return view.columns.find((c) => c.id === current.columnId);
  }

  function paintBody() {
    paintMarkup(body, current.body, view, ctx);
  }

  function paintVotes() {
    const entry = view.votes.get(id);
    const mine = view.myVotes.has(id);
    const isOwn = view.mineCards.has(id);
    emoji.textContent = view.config.voteEmoji;
    count.textContent = String(entry?.count ?? 0);
    let reason = null;
    if (!allowsVoting(view)) {
      reason = 'Voting is closed';
    } else if (!mine && isOwn && !view.config.allowSelfVotes) {
      reason = 'You cannot vote for your own card';
    } else if (!mine && votesLeft(view) === 0) {
      reason = 'You have used all your votes';
    }
    vote.disabled = reason !== null;
    vote.title = reason ?? (mine ? 'Remove your vote' : 'Vote');
    vote.setAttribute('aria-pressed', String(mine));
    vote.setAttribute('aria-label', `${mine ? 'Remove vote' : 'Vote'} (${plural(entry?.count ?? 0, 'vote')})`);
    vote.hidden = !view.config.votingEnabled;
    const ids = entry?.voters ?? [];
    voters.hidden = ids.length === 0;
    voters.title = ids.map((pid) => ctx.personName(pid) ?? 'Someone').join(', ');
    replaceContent(voters,
      ids.slice(0, MAX_VOTER_AVATARS).map((pid) => createAvatar({ name: ctx.personName(pid) ?? 'Someone', src: avatarSrc(ctx.person(pid)?.avatar), size: 'sm' })),
      ids.length > MAX_VOTER_AVATARS ? h('span', { class: 'retro-voters-more', text: `+${ids.length - MAX_VOTER_AVATARS}` }) : null);
  }

  function update(next, nextView) {
    current = next;
    view = nextView;
    const own = view.mineCards.has(id);
    const content = allowsContent(view, column());
    el.classList.toggle('retro-card-mine', own);
    el.classList.toggle('retro-card-focused', view.focus === id);
    if (!editor) {
      paintBody();
    }
    replaceContent(author, authorLine(current));
    paintVotes();
    const n = ctx.commentCount(view, id);
    commentCount.textContent = String(n);
    toggle.setAttribute('aria-label', `Comments (${n})`);
    edit.hidden = !(own && content) || editor !== null;
    remove.hidden = !((own && content) || (isFacilitator(view, ctx.me()) && column()?.actionItems && !view.meta.archived));
    comments?.update(view, content);
    for (const extra of extras) {
      extra.update?.(current, view);
    }
  }

  function toggleComments() {
    if (comments) {
      comments.destroy();
      comments = null;
    } else {
      comments = createComments(id, ctx);
      thread.append(comments.el);
      comments.update(view, allowsContent(view, column()));
      comments.focus();
    }
    toggle.setAttribute('aria-expanded', String(comments !== null));
  }

  function closeEditor() {
    editor?.destroy();
    editor = null;
    body.hidden = false;
    el.querySelector('.retro-card-edit')?.remove();
    update(current, view);
  }

  function sendEdit(markup, baseVersion) {
    ctx.send('retro.card.edit', { cardId: id, body: markup, baseVersion }, {
      onNack(nack) {
        if (nack.code !== 'stale') {
          return false;
        }
        mergePrompt(nack.d.current, markup);
        return true;
      },
    });
  }

  async function startEdit(markup = current.body, baseVersion = current.version) {
    if (editor) {
      editor.focus();
      return;
    }
    const { createEditor } = await import('../markup/editor.js');
    if (editor) {
      return;
    }
    const error = h('p', { class: 'retro-composer-error', role: 'alert', hidden: true });
    function save(value) {
      const check = checkMarkup(value, 'card');
      if (check.error) {
        error.hidden = check.error === 'empty';
        error.textContent = check.error === 'too_long' ? 'Cards can be at most 8,000 characters.' : 'This text cannot be saved.';
        return;
      }
      if (value !== current.body || baseVersion !== current.version) {
        sendEdit(value, baseVersion);
      }
      closeEditor();
    }
    editor = createEditor({
      value: markup,
      label: 'Edit card',
      mentions: ctx.mentions,
      onSubmit: save,
      uploadImage: ctx.upload,
      onError: ctx.uploadFailed,
    });
    const form = h('form', {
      class: 'retro-card-edit',
      on: {
        submit(event) {
          event.preventDefault();
          save(editor.getValue());
        },
        keydown(event) {
          if (event.key === 'Escape') {
            event.preventDefault();
            closeEditor();
          }
        },
      },
    }, editor.el, error, h('div', { class: 'retro-composer-row' },
      h('span', { class: 'retro-spacer' }),
      createButton({ label: 'Cancel', variant: 'ghost', size: 'sm', onClick: closeEditor }),
      createButton({ label: 'Save', variant: 'primary', size: 'sm', type: 'submit' })));
    body.hidden = true;
    body.after(form);
    edit.hidden = true;
    editor.focus();
  }

  function mergePrompt(latest, mine) {
    const pane = (title, markup) => h('section', { class: 'retro-merge-pane' },
      h('h3', { class: 'retro-merge-title', text: title }),
      h('div', { class: 'retro-card-body' }, renderMarkup(markup, ctx)));
    const choose = (fn) => () => {
      dialog.close();
      fn();
    };
    const dialog = createDialog({
      title: 'This card changed while you were editing',
      description: 'Keep the latest version, replace it with yours, or edit yours further.',
      className: 'retro-merge',
      content: h('div', { class: 'retro-merge-panes' }, pane('Latest version', latest.body), pane('Your version', mine)),
      actions: [
        createButton({ label: 'Keep latest', variant: 'secondary', onClick: choose(() => {}) }),
        createButton({ label: 'Edit mine', variant: 'secondary', onClick: choose(() => startEdit(mine, latest.version)) }),
        createButton({ label: 'Use mine', variant: 'primary', onClick: choose(() => sendEdit(mine, latest.version)) }),
      ],
      onClose: () => dialog.destroy(),
    });
    dialog.open();
  }

  update(card, ctx.view());
  return {
    el,
    update,
    toggleComments,
    startEdit: () => startEdit(),
    destroy() {
      editor?.destroy();
      comments?.destroy();
      for (const extra of extras) {
        extra.destroy?.();
      }
      el.remove();
    },
  };
}
