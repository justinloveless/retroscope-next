// The editor's toolbar (UX12), laid out like Jira's: text style, bold and italic with more formatting behind "…",
// text colour and highlight from Jira's palettes, lists with indent and outdent, link, insert, undo and redo. It is
// built from the app's kit (buttons, menus, popovers); no native <select>, which WebKit's CSP reports.

import { redo, undo } from 'prosemirror-history';
import { h } from '../core/h.js';
import { createButton } from '../ui/button.js';
import { icon } from '../ui/icons.js';
import { createMenu } from '../ui/menu.js';
import { createPopover, pointAnchor } from '../ui/popover.js';
import {
  blockTypeOf,
  canOutdent,
  canRelabel,
  clearFormatting,
  indent,
  insertRule,
  linkAt,
  listTypeOf,
  markActive,
  markOf,
  normalizeHref,
  outdent,
  removeLink,
  setColor,
  setHeading,
  setLink,
  toggle,
  toggleCodeBlock,
  toggleList,
  toggleQuote,
  toggleTaskList,
} from './commands.js';
import palette from './palette.json' with { type: 'json' };
import { schema } from './schema.js';

const MAC = typeof navigator === 'object' && /Mac|iPhone|iPad/.test(navigator.platform ?? '');
const MOD = MAC ? '⌘' : 'Ctrl+';
const SHIFT = MAC ? '⇧' : 'Shift+';
const ALT = MAC ? '⌥' : 'Alt+';

const BLOCK_LABELS = { p: 'Normal text', h1: 'Heading 1', h2: 'Heading 2', h3: 'Heading 3', h4: 'Heading 4', h5: 'Heading 5', h6: 'Heading 6' };

/** Shortcut hints as Jira shows them, keyed like the keymap in rich-editor.js. */
export const SHORTCUTS = {
  strong: `${MOD}B`,
  em: `${MOD}I`,
  underline: `${MOD}U`,
  strike: `${MOD}${SHIFT}S`,
  code: `${MOD}${SHIFT}M`,
  clear: `${MOD}\\`,
  bulletList: `${MOD}${SHIFT}8`,
  orderedList: `${MOD}${SHIFT}7`,
  link: `${MOD}K`,
  undo: `${MOD}Z`,
  redo: MAC ? `${MOD}${SHIFT}Z` : `${MOD}Y`,
  indent: 'Tab',
  outdent: `${SHIFT}Tab`,
  heading: (level) => `${MOD}${ALT}${level}`,
};

const run = (view, command) => {
  command(view.state, view.dispatch, view);
  view.focus();
};

// A toolbar button keeps the editor's selection: it never takes focus on press.
function tool({ label, iconName, shortcut, onClick, className }) {
  const button = createButton({
    label: shortcut ? `${label} (${shortcut})` : label,
    icon: iconName,
    iconOnly: true,
    variant: 'ghost',
    size: 'icon',
    title: shortcut ? `${label} (${shortcut})` : label,
    className: ['rich-tool', className].filter(Boolean).join(' '),
    onClick,
  });
  button.el.addEventListener('mousedown', (event) => event.preventDefault());
  return button;
}

// Tools come in groups, as in Jira; on a narrow screen the toolbar wraps between groups, never inside one.
function group(...tools) {
  return h('span', { class: 'rich-toolbar-group', role: 'group' }, tools);
}

/**
 * @param {import('prosemirror-view').EditorView} view
 * @returns {{el: HTMLElement, update: (state: object) => void, openLink: () => void, destroy: () => void}}
 */
export function createToolbar(view) {
  const disposables = [];
  const marks = schema.marks;

  // Text style: "Normal text" and headings, the label showing the current one.
  const blockButton = h('button', {
    type: 'button',
    class: ['btn', 'btn-ghost', 'btn-sm', 'rich-tool', 'rich-block-type'],
    'aria-label': 'Text styles',
    title: 'Text styles',
    on: { mousedown: (event) => event.preventDefault() },
  }, h('span', { class: 'rich-block-label', text: 'Normal text' }), icon('chevron-down'));
  const blockMenu = createMenu({
    label: 'Text styles',
    items: Object.entries(BLOCK_LABELS).map(([action, label]) => ({ action, label })),
    onSelect: (action) => run(view, setHeading(action === 'p' ? 0 : Number(action.slice(1)))),
  });
  blockMenu.attach(blockButton);
  disposables.push(blockMenu);

  const bold = tool({ label: 'Bold', iconName: 'bold', shortcut: SHORTCUTS.strong, onClick: () => run(view, toggle('strong')) });
  const italic = tool({ label: 'Italic', iconName: 'italic', shortcut: SHORTCUTS.em, onClick: () => run(view, toggle('em')) });
  const more = tool({ label: 'More formatting', iconName: 'ellipsis' });
  const moreMenu = createMenu({
    label: 'More formatting',
    items: [],
    onSelect(action) {
      const commands = {
        underline: toggle('underline'),
        strike: toggle('strike'),
        code: toggle('code'),
        clear: clearFormatting,
      };
      run(view, commands[action]);
    },
  });
  moreMenu.attach(more.el);
  disposables.push(moreMenu);
  // A ticked entry shows the mark is on, as in Jira's menu.
  const moreItems = (state) => [
    ...[['underline', 'Underline', 'underline'], ['strike', 'Strikethrough', 'strikethrough'], ['code', 'Code', 'code']].map(([action, label, iconName]) => ({
      action,
      label: `${label}  ${SHORTCUTS[action]}`,
      icon: markActive(state, marks[action]) ? 'check' : iconName,
      disabled: !toggle(action)(state),
    })),
    'separator',
    { action: 'clear', label: `Clear formatting  ${SHORTCUTS.clear}`, icon: 'remove-formatting' },
  ];

  const textColor = colorTool({ name: 'textColor', label: 'Text color', iconName: 'baseline', entries: palette.textMenu, kind: 'text', none: 'Default' });
  const highlight = colorTool({ name: 'backgroundColor', label: 'Highlight', iconName: 'highlighter', entries: palette.highlightMenu, kind: 'background', none: 'No highlight' });

  const bullet = tool({ label: 'Bullet list', iconName: 'list', shortcut: SHORTCUTS.bulletList, onClick: () => run(view, toggleList('bulletList')) });
  const ordered = tool({ label: 'Numbered list', iconName: 'list-ordered', shortcut: SHORTCUTS.orderedList, onClick: () => run(view, toggleList('orderedList')) });
  const tasks = tool({ label: 'Action item', iconName: 'list-todo', onClick: () => run(view, toggleTaskList) });
  const outdentTool = tool({ label: 'Outdent', iconName: 'list-indent-decrease', shortcut: SHORTCUTS.outdent, onClick: () => run(view, outdent) });
  const indentTool = tool({ label: 'Indent', iconName: 'list-indent-increase', shortcut: SHORTCUTS.indent, onClick: () => run(view, indent) });

  const link = tool({ label: 'Link', iconName: 'link', shortcut: SHORTCUTS.link, onClick: () => openLink() });

  const insert = tool({ label: 'Insert', iconName: 'plus' });
  const insertMenu = createMenu({
    label: 'Insert',
    items: [],
    onSelect(action) {
      run(view, { quote: toggleQuote, code: toggleCodeBlock, rule: insertRule }[action]);
    },
  });
  insertMenu.attach(insert.el);
  disposables.push(insertMenu);
  const insertItems = (state) => [
    { action: 'quote', label: 'Quote', icon: 'text-quote', disabled: !toggleQuote(state) },
    { action: 'code', label: 'Code block', icon: 'square-code', disabled: !toggleCodeBlock(state) },
    { action: 'rule', label: 'Divider', icon: 'minus', disabled: !insertRule(state) },
  ];

  const undoTool = tool({ label: 'Undo', iconName: 'undo-2', shortcut: SHORTCUTS.undo, onClick: () => run(view, undo) });
  const redoTool = tool({ label: 'Redo', iconName: 'redo-2', shortcut: SHORTCUTS.redo, onClick: () => run(view, redo) });

  const el = h('div', { class: 'rich-toolbar', role: 'toolbar', 'aria-label': 'Formatting' },
    group(blockButton),
    group(bold.el, italic.el, more.el),
    group(textColor.el, highlight.el),
    group(bullet.el, ordered.el, tasks.el, outdentTool.el, indentTool.el),
    group(link.el, insert.el),
    group(undoTool.el, redoTool.el));

  // Arrow keys move between tools, as in Jira's toolbar; Tab leaves it.
  el.addEventListener('keydown', (event) => {
    if (event.key !== 'ArrowLeft' && event.key !== 'ArrowRight') {
      return;
    }
    const items = [...el.querySelectorAll('button:not([disabled])')];
    const at = items.indexOf(document.activeElement);
    if (at >= 0) {
      event.preventDefault();
      items[(at + (event.key === 'ArrowRight' ? 1 : items.length - 1)) % items.length].focus();
    }
  });

  function colorTool({ name, label, iconName, entries, kind, none }) {
    let popover = null;
    const button = tool({ label, iconName, className: `rich-color-tool rich-${kind}-tool`, onClick: () => (popover.isOpen ? popover.close() : popover.open(button.el)) });
    const bar = h('span', { class: 'rich-color-bar', 'aria-hidden': 'true' });
    button.el.append(bar);
    const swatch = (hex, title) => {
      const b = h('button', {
        type: 'button',
        class: ['rich-swatch', hex === null && 'is-none'],
        'aria-label': title,
        title,
        dataset: { color: hex ?? '' },
        on: {
          mousedown: (event) => event.preventDefault(),
          click: () => {
            popover.close();
            run(view, setColor(name, hex));
          },
        },
      });
      if (hex !== null) {
        const token = palette[kind][hex];
        b.style.setProperty('background-color', token ? `var(--jc-${token})` : hex);
      }
      return b;
    };
    const grid = h('div', { class: 'rich-swatches', role: 'group', 'aria-label': label },
      swatch(null, none), entries.map((entry) => swatch(entry.hex, entry.name)));
    popover = createPopover({ anchor: button.el, label, content: grid, className: 'rich-color-popover', placement: 'bottom-start' });
    disposables.push(popover);
    return {
      el: button.el,
      paint(state) {
        const mark = markOf(state, marks[name]);
        const hex = mark?.attrs.color ?? null;
        const token = hex ? palette[kind][hex.toLowerCase()] : null;
        if (hex) {
          bar.style.setProperty('background-color', token ? `var(--jc-${token})` : hex);
        } else {
          bar.style.removeProperty('background-color');
        }
        for (const s of grid.querySelectorAll('.rich-swatch')) {
          s.setAttribute('aria-pressed', String((s.dataset.color || null) === (hex?.toLowerCase() ?? null)));
        }
        button.el.disabled = !setColor(name, null)(state);
      },
    };
  }

  // The link editor: a URL and its text, anchored at the selection. Opens on the toolbar button and Mod-K.
  let linkPopover = null;
  function openLink() {
    linkPopover?.destroy();
    const state = view.state;
    const existing = linkAt(state);
    const from = existing?.from ?? state.selection.from;
    const to = existing?.to ?? state.selection.to;
    if (!setLink({ href: 'https://example.com', from, to })(state)) {
      return;
    }
    const url = h('input', { class: 'input', name: 'href', type: 'text', inputmode: 'url', autocomplete: 'off', placeholder: 'Paste or type a link', 'aria-label': 'Link address', value: existing?.href ?? '' });
    const text = h('input', { class: 'input', name: 'text', type: 'text', autocomplete: 'off', placeholder: 'Text to display', 'aria-label': 'Text to display', value: existing?.text ?? state.doc.textBetween(from, to, ' ') });
    if (!canRelabel(state, from, to)) {
      text.disabled = true;
      text.title = 'This text holds more than words, so change it in the editor.';
    }
    const error = h('p', { class: 'rich-link-error', role: 'alert', hidden: true, text: 'Enter a web or email address (https://… or name@example.com).' });
    const close = () => {
      linkPopover?.destroy();
      linkPopover = null;
      view.focus();
    };
    const form = h('form', {
      class: 'rich-link-form',
      on: {
        submit(event) {
          event.preventDefault();
          const href = normalizeHref(url.value);
          if (!href) {
            url.setAttribute('aria-invalid', 'true');
            error.hidden = false;
            url.focus();
            return;
          }
          setLink({ href, text: text.value, from, to })(view.state, view.dispatch);
          close();
        },
        keydown(event) {
          if (event.key === 'Escape') {
            event.preventDefault();
            close();
          }
        },
      },
    }, url, text, error, h('div', { class: 'rich-link-actions' },
      existing ? createButton({ label: 'Remove link', icon: 'unlink', variant: 'ghost', size: 'sm', onClick: () => { removeLink(view.state, view.dispatch); close(); } }).el : null,
      createButton({ label: existing ? 'Save' : 'Insert', variant: 'primary', size: 'sm', type: 'submit' }).el));
    const coords = view.coordsAtPos(from);
    linkPopover = createPopover({ anchor: pointAnchor(coords.left, coords.bottom), label: existing ? 'Edit link' : 'Insert link', content: form, className: 'rich-link-popover', placement: 'bottom-start', onClose: () => { linkPopover = null; } });
    linkPopover.open();
    url.focus();
    url.select();
  }

  function update(state) {
    const type = blockTypeOf(state);
    blockButton.querySelector('.rich-block-label').textContent = BLOCK_LABELS[type] ?? 'Normal text';
    blockButton.disabled = !setHeading(0)(state) && type === null;
    for (const [button, name] of [[bold, 'strong'], [italic, 'em']]) {
      button.el.setAttribute('aria-pressed', String(markActive(state, marks[name])));
      button.el.disabled = !toggle(name)(state);
    }
    const formatted = ['underline', 'strike', 'code'].some((name) => markActive(state, marks[name]));
    more.el.classList.toggle('is-active', formatted);
    moreMenu.update({ items: moreItems(state) });
    textColor.paint(state);
    highlight.paint(state);
    const list = listTypeOf(state);
    bullet.el.setAttribute('aria-pressed', String(list === 'bulletList'));
    ordered.el.setAttribute('aria-pressed', String(list === 'orderedList'));
    tasks.el.setAttribute('aria-pressed', String(list === 'taskList'));
    bullet.el.disabled = !toggleList('bulletList')(state);
    ordered.el.disabled = !toggleList('orderedList')(state);
    tasks.el.disabled = !toggleTaskList(state);
    outdentTool.el.disabled = !canOutdent(state);
    indentTool.el.disabled = !indent(state);
    link.el.setAttribute('aria-pressed', String(linkAt(state) !== null));
    link.el.disabled = !setLink({ href: 'https://example.com' })(state);
    insertMenu.update({ items: insertItems(state) });
    undoTool.el.disabled = !undo(state);
    redoTool.el.disabled = !redo(state);
  }

  update(view.state);
  return {
    el,
    update,
    openLink,
    destroy() {
      linkPopover?.destroy();
      disposables.forEach((d) => d.destroy());
      el.remove();
    },
  };
}
