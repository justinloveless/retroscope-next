// The ADF-only DocNode kinds (§f "ADF rendering", UX8): panels, tables, layouts, media, task and decision
// lists, lozenges, cards and colour marks. Passed to render() as options.extend by the issue drawer, so only
// the jira-drawer chunk carries it. Every field is re-checked here as render.js does: colours and sizes reach
// the CSSOM only after a strict pattern, and nothing is ever parsed as HTML. jira.css styles the classes.

import { isAllowedHref } from './doc.js';

export const PANEL_KINDS = new Set(['info', 'note', 'tip', 'success', 'warning', 'error', 'custom']);
export const STATUS_COLORS = new Set(['neutral', 'purple', 'blue', 'red', 'yellow', 'green']);
export const MEDIA_LAYOUTS = new Set(['center', 'wide', 'full-width', 'align-start', 'align-end', 'wrap-left', 'wrap-right']);

const PALETTE = /^(?:(?:gray|blue|teal|green|lime|yellow|orange|red|magenta|purple)-(?:subtlest|subtler|subtle|bold|bolder)|text|inverse|surface)$/;

// A Jira colour (Core/Adf/AdfColor): an editor palette token or a strict lower-case #rrggbb.
export function isDocColor(v) {
  return typeof v === 'string' && (PALETTE.test(v) || /^#[0-9a-f]{6}$/.test(v));
}

export function isDay(v) {
  return typeof v === 'string' && /^\d{4}-\d{2}-\d{2}$/.test(v);
}

const PANEL_ICONS = {
  info: 'info',
  note: 'clipboard-list',
  tip: 'lightbulb',
  success: 'circle-check',
  warning: 'triangle-alert',
  error: 'circle-x',
};
const MAX_SPAN = 64;
const MAX_PIXELS = 10_000;
const NUMBER_COLUMN_PX = 42;

/** A palette token as its theme variable (jira.css), a strict #rrggbb as itself, anything else null. */
export function colorValue(value) {
  if (!isDocColor(value)) {
    return null;
  }
  return value.startsWith('#') ? value : `var(--jc-${value})`;
}

/** "2026-01-02" → the reader's medium date for that UTC day; null for anything else. */
export function formatDay(value) {
  if (!isDay(value)) {
    return null;
  }
  const date = new Date(`${value}T00:00:00Z`);
  return Number.isNaN(date.getTime()) ? null : date.toLocaleDateString(undefined, { timeZone: 'UTC', dateStyle: 'medium' });
}

const isInt = (v, min, max) => Number.isInteger(v) && v >= min && v <= max;

/**
 * @param {object} [options]
 * @param {(name: string) => Node} [options.icon] a sprite icon (ui/icons.js); none in unit tests
 * @param {(node: object) => string | null} [options.fileHref] where a file chip opens (the issue in Jira)
 * @returns {(node: object, api: object) => Node | null | undefined} render()'s options.extend
 */
export function jiraRenderer({ icon, fileHref } = {}) {
  const glyph = (api, name, className) => {
    const span = api.el('span', className);
    span.setAttribute('aria-hidden', 'true');
    if (icon) {
      span.append(icon(name));
    }
    return span;
  };
  const style = (e, property, value) => {
    if (value !== null && value !== undefined) {
      e.style.setProperty(property, value);
    }
    return e;
  };
  const link = (api, href, ...classes) => {
    const a = api.el('a', ...classes);
    a.setAttribute('href', href);
    a.setAttribute('rel', 'noopener noreferrer nofollow');
    a.setAttribute('target', '_blank');
    return a;
  };

  // Alignment and indentation classes for a paragraph or heading; null when it has neither.
  const blockClasses = (node) => {
    const classes = [];
    if (node.align === 'center' || node.align === 'end') {
      classes.push(`align-${node.align}`);
    }
    if (isInt(node.indent, 1, 6)) {
      classes.push(`indent-${node.indent}`);
    }
    return classes.length > 0 ? classes : null;
  };

  const panel = (node, api) => {
    const kind = PANEL_KINDS.has(node.kind) ? node.kind : 'info';
    const div = api.el('div', 'panel', `panel-${kind}`);
    if (kind === 'custom') {
      style(div, 'background-color', colorValue(node.color));
      if (typeof node.icon === 'string' && node.icon.length > 0 && node.icon.length <= 16) {
        const emoji = api.el('span', 'panel-icon', 'panel-emoji');
        emoji.setAttribute('aria-hidden', 'true');
        emoji.append(api.text(node.icon));
        div.append(emoji);
      }
    } else {
      div.append(glyph(api, PANEL_ICONS[kind], 'panel-icon'));
    }
    div.append(api.append(api.el('div', 'panel-body')));
    return div;
  };

  const cell = (node, api, header) => {
    const td = api.el(header ? 'th' : 'td');
    if (isInt(node.colspan, 2, MAX_SPAN)) {
      td.setAttribute('colspan', String(node.colspan));
    }
    if (isInt(node.rowspan, 2, MAX_SPAN)) {
      td.setAttribute('rowspan', String(node.rowspan));
    }
    style(td, 'background-color', colorValue(node.bg));
    return api.append(td);
  };

  // Column widths come from the first row's cells, as Jira lays them out; a gap leaves the table to size itself.
  const columnWidths = (rows) => {
    const widths = [];
    for (const c of rows[0]?.c ?? []) {
      const span = isInt(c?.colspan, 2, MAX_SPAN) ? c.colspan : 1;
      const own = Array.isArray(c?.colwidth) && c.colwidth.length === span && c.colwidth.every((w) => isInt(w, 1, MAX_PIXELS)) ? c.colwidth : null;
      if (!own) {
        return null;
      }
      widths.push(...own);
    }
    return widths.length > 0 ? widths : null;
  };

  const table = (node, api) => {
    const rows = Array.isArray(node.c) ? node.c.filter((r) => r?.t === 'tr') : [];
    const numbered = node.numbered === true;
    const t = api.el('table');
    const widths = columnWidths(rows);
    if (widths) {
      const colgroup = api.el('colgroup');
      for (const w of numbered ? [NUMBER_COLUMN_PX, ...widths] : widths) {
        colgroup.append(style(api.el('col'), 'width', `${w}px`));
      }
      t.append(colgroup);
      style(t, 'width', `${widths.reduce((sum, w) => sum + w, numbered ? NUMBER_COLUMN_PX : 0)}px`);
    } else if (isInt(node.width, 1, MAX_PIXELS)) {
      style(t, 'width', `${node.width}px`);
    }
    const body = api.el('tbody');
    let number = 0;
    for (const row of rows) {
      const tr = api.el('tr');
      const cells = Array.isArray(row.c) ? row.c : [];
      if (numbered) {
        const headerRow = cells.length > 0 && cells.every((c) => c?.t === 'th');
        const num = api.el(headerRow ? 'th' : 'td', 'number');
        if (!headerRow) {
          num.append(api.text(String(++number)));
        }
        tr.append(num);
      }
      body.append(api.append(tr, cells));
    }
    t.append(body);
    const wrap = api.el('div', 'table-wrap');
    wrap.append(t);
    return wrap;
  };

  const tasks = (node, api) => {
    const ul = api.el('ul', 'tasks');
    for (const item of Array.isArray(node.c) ? node.c : []) {
      if (item?.t === 'tasks') {
        ul.append(api.append(api.el('li', 'task-nest'), [item]));
      } else {
        api.append(ul, [item]);
      }
    }
    return ul;
  };

  const task = (node, api) => {
    const box = api.el('input');
    box.setAttribute('type', 'checkbox');
    box.setAttribute('disabled', '');
    if (node.done === true) {
      box.setAttribute('checked', '');
    }
    const li = api.el('li', 'task');
    li.append(box);
    return api.append(li);
  };

  const decision = (node, api) => {
    const li = api.el('li', 'decision', ...(node.done === true ? ['is-decided'] : []));
    li.append(glyph(api, 'split', 'decision-icon'));
    return api.append(li);
  };

  const media = (node, api, group) => {
    const figure = api.el(group ? 'div' : 'figure', group ? 'media-group' : 'media', ...(!group && MEDIA_LAYOUTS.has(node.layout) ? [`layout-${node.layout}`] : []));
    if (!group && typeof node.width === 'number') {
      if (node.unit === 'px' && isInt(node.width, 1, MAX_PIXELS)) {
        style(figure, 'width', `${node.width}px`);
      } else if (node.unit === '%' && node.width > 0 && node.width <= 100) {
        style(figure, 'width', `${node.width}%`);
      }
    }
    api.append(figure);
    if (!group && Array.isArray(node.caption) && node.caption.length > 0) {
      figure.append(api.append(api.el('figcaption'), node.caption));
    }
    return figure;
  };

  const file = (node, api) => {
    const href = fileHref?.(node);
    const linked = typeof href === 'string' && isAllowedHref(href);
    const chip = linked ? link(api, href, 'file-chip') : api.el('span', 'file-chip');
    if (linked) {
      chip.setAttribute('title', 'Open in Jira');
    }
    chip.append(glyph(api, 'paperclip', 'file-icon'), api.text(typeof node.name === 'string' ? node.name : 'file'));
    return chip;
  };

  const card = (node, api) => {
    if (!isAllowedHref(node.href)) {
      return null;
    }
    const a = link(api, node.href, 'smart-card', ...(node.block === true ? ['is-block'] : []));
    a.append(glyph(api, 'link', 'card-icon'), api.text(cardLabel(node.href)));
    return a;
  };

  const colored = (node, api, property, className) => {
    const value = colorValue(node.color);
    if (value === null) {
      return api.append(api.el('span'));
    }
    return api.append(style(api.el('span', className), property, value));
  };

  return (node, api) => {
    switch (node.t) {
      case 'p':
      case 'h': {
        const classes = blockClasses(node);
        if (!classes) {
          return undefined;
        }
        const tag = node.t === 'h' && isInt(node.level, 1, 6) ? `h${node.level}` : 'p';
        return api.append(api.el(tag, ...classes));
      }
      case 'a': {
        if (typeof node.title !== 'string' || !isAllowedHref(node.href)) {
          return undefined;
        }
        const a = link(api, node.href);
        a.setAttribute('title', node.title);
        return api.append(a);
      }
      case 'panel':
        return panel(node, api);
      case 'table':
        return table(node, api);
      case 'th':
      case 'td':
        return cell(node, api, node.t === 'th');
      case 'expand': {
        const details = api.el('details', 'expand');
        const summary = api.el('summary');
        summary.append(api.text(node.title));
        details.append(summary, api.append(api.el('div', 'expand-body')));
        return details;
      }
      case 'layout':
        return api.append(api.el('div', 'layout'));
      case 'col': {
        const col = api.el('div', 'layout-col');
        style(col, 'flex-grow', typeof node.width === 'number' && node.width > 0 && node.width <= 100 ? String(node.width) : null);
        return api.append(col);
      }
      case 'tasks':
        return tasks(node, api);
      case 'task':
        return task(node, api);
      case 'decisions':
        return api.append(api.el('ul', 'decisions'));
      case 'decision':
        return decision(node, api);
      case 'media':
        return media(node, api, false);
      case 'files':
        return media(node, api, true);
      case 'file':
        return file(node, api);
      case 'status': {
        const color = STATUS_COLORS.has(node.color) ? node.color : 'neutral';
        const span = api.el('span', 'status', `status-${color}`, ...(node.bold === true ? ['is-bold'] : []));
        span.append(api.text(node.v));
        return span;
      }
      case 'user': {
        const span = api.el('span', 'jira-user');
        span.append(api.text(node.v));
        return span;
      }
      case 'date': {
        const label = formatDay(node.v);
        if (label === null) {
          return null;
        }
        const time = api.el('time', 'date-lozenge');
        time.setAttribute('datetime', node.v);
        time.append(api.text(label));
        return time;
      }
      case 'card':
        return card(node, api);
      case 'fg':
        return colored(node, api, 'color', 'text-color');
      case 'bg':
        return colored(node, api, 'background-color', 'highlight');
      case 'sub':
      case 'sup':
        return api.append(api.el(node.t));
      default:
        return undefined;
    }
  };
}

// A Jira issue link shows its key, any other link its host and path; Jira's titled previews need its servers.
function cardLabel(href) {
  try {
    const url = new URL(href);
    const issue = /^\/browse\/([A-Z][A-Z0-9_]*-\d+)\/?$/.exec(url.pathname);
    if (issue) {
      return issue[1];
    }
    const path = url.pathname === '/' ? '' : url.pathname;
    return `${url.host}${path}` || href;
  } catch {
    return href;
  }
}
