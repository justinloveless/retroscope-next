// Copy, cut, paste and drop without ProseMirror's HTML path: under trusted-types 'none' no HTML parser can run, and
// the build removes the one ProseMirror ships (web/build.mjs, VENDOR_REWRITES). A copy writes plain text and a
// private JSON type holding ADF; a paste reads that type back through the paste-mode reader, or plain text.
//
// The private type is not private: Chromium hands it to any origin, so any page can plant one. It is parsed as
// hostile ADF, and the server checks the save again.

import { Fragment, Slice } from 'prosemirror-model';
import { adfFragmentToPm, nodeToAdf } from './adf.js';
import { schema } from './schema.js';

export const CLIPBOARD_TYPE = 'application/x-retroscope-adf+json';
const MAX_PAYLOAD = 1024 * 1024;

export function serializeSlice(slice) {
  const content = [];
  slice.content.forEach((node) => content.push(nodeToAdf(node)));
  return JSON.stringify({ rsClip: 1, openStart: slice.openStart, openEnd: slice.openEnd, content });
}

// How deep a fragment's edge can be opened: through non-leaf, non-atom first (or last) children only.
function openable(fragment, side, wanted) {
  const edge = (f) => (side < 0 ? f.firstChild : f.lastChild);
  let depth = 0;
  for (let node = edge(fragment); depth < wanted && node && !node.isLeaf && !node.isAtom; node = edge(node.content)) {
    depth++;
  }
  return depth;
}

/**
 * A clipboard payload -> a Slice of what survives the paste rules, or null when nothing usable is in it.
 * session: { media: Set of media ids this session may place, mentions: Set of account ids picked in it }.
 */
export function parseClipboard(text, session) {
  if (typeof text !== 'string' || text.length === 0 || text.length > MAX_PAYLOAD) {
    return null;
  }
  let payload;
  try {
    payload = JSON.parse(text);
  } catch {
    return null;
  }
  if (payload === null || typeof payload !== 'object' || payload.rsClip !== 1 || !Array.isArray(payload.content)) {
    return null;
  }
  const fragment = adfFragmentToPm(payload.content, { session });
  if (fragment.size === 0) {
    return null;
  }
  const open = (v) => (Number.isInteger(v) && v >= 0 && v <= 64 ? v : 0);
  return new Slice(fragment, openable(fragment, -1, open(payload.openStart)), openable(fragment, 1, open(payload.openEnd)));
}

const CONTROLS = /[\u0000-\u0008\u000b-\u001f\u007f-\u009f]/g;

/** Plain text -> a Slice: one paragraph per line, or raw text inside a code block. */
export function plainTextSlice(text, inCode) {
  const clean = String(text ?? '').replace(/\r\n?/g, '\n').replace(CONTROLS, ' ');
  if (clean === '') {
    return null;
  }
  if (inCode) {
    return new Slice(Fragment.from(schema.text(clean)), 0, 0);
  }
  const paragraphs = clean.split('\n').map((line) => schema.nodes.paragraph.create(null, line ? schema.text(line) : null));
  return new Slice(Fragment.from(paragraphs), 1, 1);
}

function clipboardText(slice) {
  return slice.content.textBetween(0, slice.content.size, '\n\n', (leaf) => leaf.attrs?.text ?? (leaf.type.name === 'hardBreak' ? '\n' : ''));
}

/** handleDOMEvents for the EditorView: ProseMirror's own clipboard handlers never run. */
export function clipboardHandlers(session) {
  const copy = (view, event) => {
    const { selection } = view.state;
    if (selection.empty || !event.clipboardData) {
      return false;
    }
    const slice = selection.content();
    event.preventDefault();
    event.clipboardData.clearData();
    event.clipboardData.setData('text/plain', clipboardText(slice));
    event.clipboardData.setData(CLIPBOARD_TYPE, serializeSlice(slice));
    if (event.type === 'cut') {
      view.dispatch(view.state.tr.deleteSelection().scrollIntoView().setMeta('uiEvent', 'cut'));
    }
    return true;
  };
  return {
    copy,
    cut: copy,
    paste(view, event) {
      event.preventDefault();
      const data = event.clipboardData;
      if (!data) {
        return true;
      }
      const inCode = view.state.selection.$from.parent.type.spec.code === true;
      const slice = (inCode ? null : parseClipboard(data.getData(CLIPBOARD_TYPE), session))
        ?? plainTextSlice(data.getData('text/plain'), inCode);
      if (slice) {
        view.dispatch(view.state.tr.replaceSelection(slice).scrollIntoView().setMeta('uiEvent', 'paste'));
      }
      return true;
    },
    // A drag inside the editor moves the slice ProseMirror stored at dragstart; anything dropped from outside is
    // refused until media upload lands.
    drop(view, event) {
      if (view.dragging) {
        return false;
      }
      event.preventDefault();
      return true;
    },
  };
}
