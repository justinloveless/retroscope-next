// Local pixel-art glyphs for Jira issue types, priorities and status categories (§f "Images and icons"):
// browsers never load Jira's icon URLs, so img-src stays 'self'. Each glyph is an 8×8 grid drawn as one
// SVG path per colour, chosen by name; unknown names get a neutral square.

import { h, s } from '../core/h.js';

const TYPES = {
  story: ['#36b37e', ['.######.', '.######.', '.######.', '.######.', '.######.', '.##..##.', '.#....#.', '........']],
  bug: ['#e5493a', ['..####..', '.######.', '##.##.##', '########', '########', '##.##.##', '.######.', '..####..']],
  task: ['#4bade8', ['########', '#......#', '#.....##', '#....#.#', '##..#..#', '#.##...#', '#......#', '########']],
  subtask: ['#4bade8', ['####....', '#..#....', '####....', '..#.....', '..#.####', '..###..#', '....####', '........']],
  epic: ['#904ee2', ['....##..', '...##...', '..##....', '.######.', '....##..', '...##...', '..##....', '.##.....']],
  spike: ['#ff991f', ['...##...', '...##...', '..####..', '########', '########', '..####..', '...##...', '...##...']],
};

const OTHER_TYPE = ['#97a0af', ['........', '.######.', '.######.', '.######.', '.######.', '.######.', '.######.', '........']];

const UP_TWICE = ['...##...', '..####..', '.##..##.', '##....##', '...##...', '..####..', '.##..##.', '##....##'];
const UP = ['........', '...##...', '..####..', '.##..##.', '##....##', '........', '........', '........'];
const DOWN = ['........', '........', '........', '##....##', '.##..##.', '..####..', '...##...', '........'];
const DOWN_TWICE = ['##....##', '.##..##.', '..####..', '...##...', '##....##', '.##..##.', '..####..', '...##...'];

const PRIORITIES = {
  highest: ['#cd1317', UP_TWICE],
  high: ['#e9494a', UP],
  medium: ['#e97f33', ['........', '........', '########', '........', '........', '########', '........', '........']],
  low: ['#2d8738', DOWN],
  lowest: ['#57a55a', DOWN_TWICE],
};

// Status category → badge colours, readable on both themes.
const CATEGORIES = {
  new: { background: '#dfe1e6', color: '#42526e' },
  indeterminate: { background: '#deebff', color: '#0747a6' },
  done: { background: '#e3fcef', color: '#006644' },
};

function pathOf(rows) {
  let d = '';
  rows.forEach((row, y) => {
    for (let x = 0; x < row.length; x++) {
      if (row[x] === '#') {
        d += `M${x} ${y}h1v1h-1z`;
      }
    }
  });
  return d;
}

function glyph([fill, rows], label, size) {
  return s('svg', {
    class: 'glyph',
    viewBox: '0 0 8 8',
    width: String(size),
    height: String(size),
    role: 'img',
    'aria-label': label,
    focusable: 'false',
    style: { 'shape-rendering': 'crispEdges', flex: 'none' },
  }, s('path', { d: pathOf(rows), fill }));
}

function typeKey(name) {
  const key = String(name ?? '').toLowerCase().replace(/[\s-]+/g, '');
  return key === 'subtask' || key === 'subtasks' ? 'subtask' : key;
}

/** The glyph for an issue type name ("Story", "Bug", "Sub-task", …), labelled with that name. */
export function issueTypeGlyph(name, { size = 14 } = {}) {
  return glyph(TYPES[typeKey(name)] ?? OTHER_TYPE, name || 'Issue', size);
}

/** The glyph for a priority name ("Highest" … "Lowest"); null for no priority. */
export function priorityGlyph(name, { size = 14 } = {}) {
  if (!name) {
    return null;
  }
  return glyph(PRIORITIES[String(name).toLowerCase()] ?? OTHER_TYPE, `Priority: ${name}`, size);
}

/** A status pill coloured by its category (`new`, `indeterminate`, `done`). */
export function statusBadge(name, category) {
  return h('span', {
    class: 'badge jira-status',
    dataset: { category: category ?? 'new' },
    style: { ...(CATEGORIES[category] ?? CATEGORIES.new), 'white-space': 'nowrap' },
    text: name ?? '',
  });
}
