// The rich description editor (UX11, UX12): its own lazy chunk, loaded by the issue drawer for users who can edit.
// createRichEditor mounts a ProseMirror editor with Jira's toolbar on a description's edit view (GET …/description)
// and hands back its ADF for the save. The schema, ADF conversion, token guard and clipboard are UX11's core.

import { baseKeymap, chainCommands, exitCode } from 'prosemirror-commands';
import { dropCursor } from 'prosemirror-dropcursor';
import { gapCursor } from 'prosemirror-gapcursor';
import { history, redo, undo } from 'prosemirror-history';
import { undoInputRule } from 'prosemirror-inputrules';
import { keymap } from 'prosemirror-keymap';
import { EditorState, Plugin } from 'prosemirror-state';
import { tableEditing } from 'prosemirror-tables';
import { EditorView } from 'prosemirror-view';
import { adfToPm, canonicalAdf, pmToAdf } from './adf.js';
import { blockMarkGuard } from './block-marks.js';
import { clipboardHandlers } from './clipboard.js';
import { arrowUpFromItem, clearFormatting, enter, indent, outdent, setHeading, stayInTable, toggle, toggleList } from './commands.js';
import { autoformat } from './input-rules.js';
import { schema } from './schema.js';
import { tokenGuard } from './tokens.js';
import { createToolbar } from './toolbar.js';
import { markViews, nodeViews } from './views.js';

export { AdfError, adfToPm, canonicalAdf, pmToAdf } from './adf.js';

const { nodes } = schema;

// Jira's bindings (adf-inventory §7.3) for what this phase offers.
function keys(openLink) {
  const link = (state) => {
    const parent = state.selection.$from.parent;
    if (!parent.inlineContent || parent.type.spec.code) {
      return false;
    }
    openLink();
    return true;
  };
  const bindings = {
    'Mod-z': undo,
    'Shift-Mod-z': redo,
    'Mod-y': redo,
    'Mod-b': toggle('strong'),
    'Mod-i': toggle('em'),
    'Mod-u': toggle('underline'),
    'Shift-Mod-s': toggle('strike'),
    'Shift-Mod-m': toggle('code'),
    'Mod-\\': clearFormatting,
    'Mod-k': link,
    'Shift-Mod-k': link,
    'Shift-Mod-7': toggleList('orderedList'),
    'Shift-Mod-8': toggleList('bulletList'),
    'Mod-Alt-0': setHeading(0),
    Enter: enter,
    Backspace: undoInputRule,
    Tab: chainCommands(indent, stayInTable),
    'Shift-Tab': chainCommands(outdent, stayInTable),
    ArrowUp: arrowUpFromItem,
    'Shift-Enter': chainCommands(exitCode, (state, dispatch) => {
      dispatch?.(state.tr.replaceSelectionWith(nodes.hardBreak.create()).scrollIntoView());
      return true;
    }),
  };
  for (let level = 1; level <= 6; level++) {
    bindings[`Mod-Alt-${level}`] = setHeading(level);
  }
  return keymap(bindings);
}

// A key pressed right after a scripted or fast caret move can arrive before the browser's selectionchange, and
// ProseMirror would act on the old selection (the design's stale-selection race). Reading the DOM selection first
// makes the key act where the caret is.
export const selectionFlush = new Plugin({
  props: {
    handleDOMEvents: {
      keydown(view) {
        view.domObserver?.flush?.();
        return false;
      },
    },
  },
});

/**
 * The editor's plugins, in order: the selection flush runs before any keymap reads the selection. `loaded` is the
 * edit view's document, whose tokens bound what the session may hold.
 */
export function editorPlugins(loaded, { openLink, update }) {
  return [
    selectionFlush,
    autoformat(),
    keys(openLink),
    keymap(baseKeymap),
    history(),
    tableEditing(),
    gapCursor(),
    dropCursor(),
    tokenGuard(loaded),
    blockMarkGuard(),
    new Plugin({ view: () => ({ update: (v) => update(v.state) }) }),
  ];
}

/** The account ids a document mentions: a paste may keep those mentions (the server accepts them). */
function mentionIds(doc) {
  const ids = new Set();
  doc.descendants((node) => {
    if (node.type === nodes.mention) {
      ids.add(node.attrs.id);
    }
  });
  return ids;
}

/**
 * @param {object} options
 * @param {HTMLElement} options.host where the editor (toolbar and content) mounts
 * @param {object} options.edit the edit view (GET …/description): adf, media, opaque, marks
 * @param {object} [options.draft] unsaved ADF from an earlier session on the same edit view, to start from instead
 * @param {(attachmentId: string) => string} options.imageSrc the attachment proxy URL
 * @param {(doc: object) => Node} [options.renderDoc] renders an opaque part's read-only preview
 * @param {(name: string) => Node} [options.icon] a sprite icon
 * @param {string} [options.label] the editor's accessible name
 * @param {() => void} [options.onChange] called after each document change
 * @returns {{el: HTMLElement, view: EditorView, getAdf: () => object, isDirty: () => boolean, focus: () => void, destroy: () => void}}
 */
export function createRichEditor({ host, edit, draft = null, imageSrc, renderDoc, icon, label = 'Description', onChange }) {
  const original = adfToPm(edit.adf);
  const doc = draft ? adfToPm(draft) : original;
  const loaded = canonicalAdf(edit.adf);
  const media = new Map((edit.media ?? []).map((m) => [m.id, m]));
  const session = { media: new Set(media.keys()), mentions: mentionIds(original) };
  const context = {
    opaque: new Map((edit.opaque ?? []).map((part) => [part.ref, part])),
    media,
    imageSrc,
    renderDoc,
    icon,
  };

  const el = document.createElement('div');
  el.className = 'rich-frame';
  const content = document.createElement('div');
  content.className = 'rich-content';
  let toolbar = null;

  const view = new EditorView(content, {
    state: EditorState.create({
      doc,
      plugins: editorPlugins(original, { openLink: () => toolbar?.openLink(), update: (state) => toolbar?.update(state) }),
    }),
    nodeViews: nodeViews(context),
    markViews: markViews(),
    handleDOMEvents: clipboardHandlers(session),
    attributes: { class: 'rich-editor jira-prose', role: 'textbox', 'aria-multiline': 'true', 'aria-label': label },
    dispatchTransaction(tr) {
      view.updateState(view.state.apply(tr));
      if (tr.docChanged) {
        onChange?.();
      }
    },
  });
  toolbar = createToolbar(view);
  el.append(toolbar.el, content);
  host.append(el);

  const getAdf = () => pmToAdf(view.state.doc);
  return {
    el,
    view,
    getAdf,
    /** False when the document is canonical-equal to the one loaded, so a save can skip the PATCH. */
    isDirty: () => canonicalAdf(getAdf()) !== loaded,
    focus: () => view.focus(),
    destroy() {
      toolbar.destroy();
      view.destroy();
      el.remove();
    },
  };
}
