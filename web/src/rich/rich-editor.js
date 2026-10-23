// The rich description editor (UX11): its own lazy chunk, loaded by the issue drawer. Phase 5 delivers the core:
// schema, ADF conversion, token guard and the safe clipboard. Toolbars, node views and the save path come in
// later phases; createRichEditor is the seam they fill.

import { baseKeymap, chainCommands, exitCode } from 'prosemirror-commands';
import { dropCursor } from 'prosemirror-dropcursor';
import { gapCursor } from 'prosemirror-gapcursor';
import { history, redo, undo } from 'prosemirror-history';
import { inputRules, textblockTypeInputRule, wrappingInputRule } from 'prosemirror-inputrules';
import { keymap } from 'prosemirror-keymap';
import { liftListItem, sinkListItem, splitListItem } from 'prosemirror-schema-list';
import { EditorState } from 'prosemirror-state';
import { goToNextCell, tableEditing } from 'prosemirror-tables';
import { EditorView } from 'prosemirror-view';
import { adfToPm, pmToAdf } from './adf.js';
import { blockMarkGuard } from './block-marks.js';
import { clipboardHandlers } from './clipboard.js';
import { schema } from './schema.js';
import { tokenGuard } from './tokens.js';

export { AdfError, adfToPm, canonicalAdf, pmToAdf } from './adf.js';

const { nodes } = schema;

function rules() {
  return inputRules({
    rules: [
      textblockTypeInputRule(/^(#{1,6})\s$/, nodes.heading, (m) => ({ level: m[1].length })),
      wrappingInputRule(/^\s*>\s$/, nodes.blockquote),
      wrappingInputRule(/^\s*([-*])\s$/, nodes.bulletList),
      wrappingInputRule(/^(\d+)[.)]\s$/, nodes.orderedList, (m) => ({ order: Number(m[1]) === 1 ? null : Number(m[1]) })),
      textblockTypeInputRule(/^```$/, nodes.codeBlock),
    ],
  });
}

function keys() {
  const item = nodes.listItem;
  return keymap({
    'Mod-z': undo,
    'Shift-Mod-z': redo,
    'Mod-y': redo,
    Enter: splitListItem(item),
    Tab: chainCommands(goToNextCell(1), sinkListItem(item)),
    'Shift-Tab': chainCommands(goToNextCell(-1), liftListItem(item)),
    'Shift-Enter': chainCommands(exitCode, (state, dispatch) => {
      dispatch?.(state.tr.replaceSelectionWith(nodes.hardBreak.create()).scrollIntoView());
      return true;
    }),
  });
}

/**
 * @param {object} options
 * @param {HTMLElement} options.host where the editor mounts
 * @param {object} options.adf the projected description (GET …/description `adf`)
 * @param {{media: Set<string>, mentions: Set<string>}} options.session ids this session may place by paste
 * @returns {{view: EditorView, getAdf: () => object, destroy: () => void}}
 */
export function createRichEditor({ host, adf, session }) {
  const doc = adfToPm(adf);
  const state = EditorState.create({
    doc,
    plugins: [rules(), keys(), keymap(baseKeymap), history(), tableEditing(), gapCursor(), dropCursor(), tokenGuard(doc), blockMarkGuard()],
  });
  const view = new EditorView(host, {
    state,
    handleDOMEvents: clipboardHandlers(session),
    attributes: { class: 'rich-editor jira-prose' },
  });
  return {
    view,
    getAdf: () => pmToAdf(view.state.doc),
    destroy: () => view.destroy(),
  };
}
