// Bundled into the editor test page: mounts one editor and records what it reports.
import { createEditor } from '../../../../web/src/markup/editor.js';

const UPLOADED = '9b2e4f1c-6c1d-4f7a-9a43-0d1f2a3b4c5d';

window.mountEditor = (options = {}) => {
  window.editor?.destroy();
  const log = { changes: [], submits: [], uploads: 0, errors: [] };
  window.log = log;
  window.pendingUploads = [];
  const islands = new Map(Object.entries(options.islands ?? {}));
  window.editor = createEditor({
    ...options,
    islands,
    onChange: (markup) => log.changes.push(markup),
    onSubmit: options.submit === false ? undefined : (markup) => log.submits.push(markup),
    onError: (error, file) => log.errors.push({ message: error.message, file: file.name }),
    uploadImage: (file) => {
      log.uploads++;
      if (options.uploads === 'held') {
        return new Promise((resolve, reject) => {
          window.pendingUploads.push({ file: file.name, resolve: () => resolve(UPLOADED), reject: () => reject(new Error('upload failed')) });
        });
      }
      return new Promise((resolve) => setTimeout(() => resolve(UPLOADED), 10));
    },
  });
  document.getElementById('root').append(window.editor.el);
  window.editor.focus();
};
