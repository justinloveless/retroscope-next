// Comments of an action item's source card (they live on its retro board). Loaded with the shared
// editor when a panel first opens. The list is refetched whenever the team room says the count moved.

import { h, replaceContent } from '../core/h.js';
import { reconcile } from '../core/list.js';
import { http } from '../core/http.js';
import { timeAgo } from '../core/fmt.js';
import { createEditor } from '../markup/editor.js';
import { createAvatar } from '../ui/avatar.js';
import { createButton } from '../ui/button.js';
import { toast } from '../ui/toast.js';
import { renderBody } from './action-items.js';
import { avatarSrc, errorMessage, toastError, uuidv7 } from './util.js';

const UPLOAD_ERRORS = {
  board_quota: 'This board has no image space left.',
  too_large: 'Images can be at most 2 MB.',
  unsupported_media_type: 'Only PNG, JPEG, GIF and WebP images can be added.',
  storage_full: 'Image storage is full right now.',
};
// A proxy in front of the server may answer these statuses without a problem body.
const UPLOAD_STATUS = { 409: 'board_quota', 413: 'too_large', 415: 'unsupported_media_type', 507: 'storage_full' };

export function createComments(ctx, item, members) {
  const list = h('ul', { class: 'team-comments', 'aria-label': 'Comments' });
  const composer = h('div', { class: 'team-composer' });
  const el = h('div', { class: 'team-comments-panel', dataset: { item: item.id } }, list, composer);
  let token = 0;
  const path = `/action-items/${item.id}/comments`;

  async function refresh() {
    const run = ++token;
    if (!list.firstChild) {
      list.append(h('li', { class: 'team-muted', text: 'Loading comments…' }));
    }
    try {
      const { items } = await ctx.api.get(path);
      if (run === token) {
        renderComments(items);
      }
    } catch (error) {
      if (run === token) {
        replaceContent(list, h('li', { class: 'team-muted', text: errorMessage(error, 'The comments could not be loaded.') }));
      }
    }
  }

  function renderComments(comments) {
    if (comments.length === 0) {
      replaceContent(list, h('li', { class: 'team-muted', text: 'No comments yet.' }));
      return;
    }
    reconcile(list, comments, (c) => c.id, (comment) => h('li', { class: 'team-comment', dataset: { id: comment.id } },
      createAvatar({ name: comment.author?.name ?? 'Anonymous', src: avatarSrc(comment.author?.avatar), size: 'sm' }).el,
      h('div', { class: 'team-comment-body' },
        h('p', { class: 'team-comment-meta' },
          h('strong', { text: comment.author?.name ?? 'Anonymous' }),
          h('span', { class: 'team-muted', text: ` · ${timeAgo(comment.createdAt)}` })),
        renderBody(comment.body, members()))));
  }

  async function submit(markup) {
    const body = markup.trim();
    if (!body) {
      return;
    }
    try {
      await ctx.api.post(path, { commentId: uuidv7(), body });
      editor.setValue('');
      refresh();
    } catch (error) {
      toastError(error, 'The comment could not be added.');
    }
  }

  async function uploadImage(file) {
    try {
      const { blobId } = await http.post(`/api/retro/${item.sourceBoardId}/images`, file);
      return blobId;
    } catch (error) {
      const detail = UPLOAD_ERRORS[error.code] ?? UPLOAD_ERRORS[UPLOAD_STATUS[error.status]];
      throw Object.assign(new Error(error.code), { detail: detail ?? errorMessage(error, 'The image could not be uploaded.') });
    }
  }

  const editor = createEditor({
    label: 'Add a comment',
    placeholder: 'Add a comment… (Enter sends, Shift+Enter adds a line)',
    mentions: () => members().map((m) => ({ id: m.pid, name: m.name })),
    onSubmit: submit,
    uploadImage: item.sourceBoardId ? uploadImage : undefined,
    onError: (error) => toast(error?.detail ?? 'The image could not be uploaded.', { variant: 'error', duration: 6000 }),
  });
  composer.append(editor.el, createButton({ label: 'Comment', icon: 'send', size: 'sm', variant: 'primary', onClick: () => submit(editor.getValue()) }).el);

  refresh();
  return {
    el,
    refresh,
    destroy() {
      token++;
      editor.destroy();
      el.remove();
    },
  };
}
