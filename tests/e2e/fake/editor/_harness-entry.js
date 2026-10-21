// Bundled into the editor test page: mounts one editor and records what it reports.
import { createEditor } from '../../../../web/src/markup/editor.js';

const UPLOADED = '9b2e4f1c-6c1d-4f7a-9a43-0d1f2a3b4c5d';

window.mountEditor = (options = {}) => {
  window.editor?.destroy();
  const log = { changes: [], submits: [], uploads: 0 };
  window.log = log;
  const islands = new Map(Object.entries(options.islands ?? {}));
  window.editor = createEditor({
    ...options,
    islands,
    onChange: (markup) => log.changes.push(markup),
    onSubmit: options.submit === false ? undefined : (markup) => log.submits.push(markup),
    uploadImage: async () => {
      log.uploads++;
      await new Promise((resolve) => setTimeout(resolve, 10));
      return UPLOADED;
    },
  });
  document.getElementById('root').append(window.editor.el);
  window.editor.focus();
};
