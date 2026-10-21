import { createButton } from './button.js';
import { createDialog } from './dialog.js';

// Resolves true on confirm, false on cancel, Escape or backdrop click.
export function confirmDialog({ title, message, confirmLabel = 'Confirm', cancelLabel = 'Cancel', destructive = false }) {
  return new Promise((resolve) => {
    let result = false;
    const cancel = createButton({ label: cancelLabel, variant: 'secondary', onClick: () => dialog.close() });
    const confirm = createButton({
      label: confirmLabel,
      variant: destructive ? 'destructive' : 'primary',
      onClick() {
        result = true;
        dialog.close();
      },
    });
    const dialog = createDialog({
      title,
      description: message,
      actions: [cancel.el, confirm.el],
      onClose() {
        dialog.destroy();
        resolve(result);
      },
    });
    dialog.open();
    confirm.el.focus();
  });
}
