// Copy link (UX18 decision 5): boards are team-only, so sharing one is sending its link to another member of the
// team. There is nothing to choose about who may open it, so the button copies the link at once.

import { createButton } from '../ui/button.js';
import { toast } from '../ui/toast.js';

export function boardUrl(code) {
  return `${location.origin}/retro/${encodeURIComponent(code)}`;
}

export const shareFeature = {
  board(ctx, { toolbar }) {
    const button = createButton({
      label: 'Copy link',
      icon: 'link',
      iconOnly: true,
      variant: 'outline',
      size: 'sm',
      className: 'btn-icon retro-share-open',
      onClick() {
        const code = ctx.view()?.code;
        if (!code) {
          return;
        }
        navigator.clipboard?.writeText(boardUrl(code))
          .then(() => toast('Link copied!', { variant: 'success' }), () => toast('Could not copy the link.', { variant: 'error' }))
          ?? toast('Could not copy the link.', { variant: 'error' });
      },
    });
    toolbar.append(button.el);
    return {
      destroy() {
        button.destroy();
      },
    };
  },
};
