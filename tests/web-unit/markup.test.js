import { test } from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, readFileSync, readdirSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { checkMarkup, parse } from '../../web/src/markup/parse.js';
import { isAllowedDoc } from '../../web/src/markup/allow.js';
import { colorValue, formatDay, jiraRenderer } from '../../web/src/markup/jira.js';
import { render } from '../../web/src/markup/render.js';
import {
  DIALECT_JIRA,
  DIALECT_JIRA_DESCRIPTION,
  DIALECT_RETRO,
  imageBlobIds,
  islandIds,
  mentionIds,
  toMarkup,
  walk,
} from '../../web/src/markup/doc.js';

const ROOT = fileURLToPath(new URL('../../', import.meta.url));
const FIXTURES = `${ROOT}tests/fixtures/markup/`;
const U1 = '0f8fad5b-d9cb-469f-a165-70867728950e';
const U2 = '7c9e6679-7425-40de-944b-e07fc1f90ae7';

// Minimal DOM: just the calls render.js may make. Every HTML-parsing sink throws.
class FakeNode {
  constructor(kind) {
    this.kind = kind;
    this.childNodes = [];
  }

  append(...nodes) {
    for (const node of nodes) {
      assert.ok(node instanceof FakeNode, 'append() takes nodes, never strings');
      if (node.kind === 'fragment') {
        this.childNodes.push(...node.childNodes);
        node.childNodes = [];
      } else {
        this.childNodes.push(node);
      }
    }
  }
}

// The CSSOM is the only way a style reaches the page (style-src 'self'); html() shows it as `cssom`.
class FakeStyle {
  constructor() {
    this.props = new Map();
  }

  setProperty(name, value) {
    assert.equal(typeof value, 'string');
    this.props.set(name, value);
  }
}

class FakeElement extends FakeNode {
  constructor(tag) {
    super('element');
    this.tagName = tag;
    this.attributes = new Map();
    this.style = new FakeStyle();
  }

  setAttribute(name, value) {
    assert.equal(typeof value, 'string');
    this.attributes.set(name, value);
  }
}

for (const sink of ['innerHTML', 'outerHTML']) {
  Object.defineProperty(FakeElement.prototype, sink, {
    set() {
      throw new Error(`${sink} used`);
    },
  });
}
FakeElement.prototype.insertAdjacentHTML = () => {
  throw new Error('insertAdjacentHTML used');
};

const fakeDocument = {
  createElement: (tag) => new FakeElement(tag),
  createTextNode: (text) => Object.assign(new FakeNode('text'), { data: String(text) }),
  createDocumentFragment: () => new FakeNode('fragment'),
  write() {
    throw new Error('document.write used');
  },
};

function html(node) {
  if (node.kind === 'text') {
    return node.data.replace(/&/g, '&amp;').replace(/</g, '&lt;');
  }
  const inner = node.childNodes.map(html).join('');
  if (node.kind === 'fragment') {
    return inner;
  }
  const cssom = [...node.style.props].map(([k, v]) => `${k}: ${v}`).join('; ');
  const attrs = [...node.attributes].map(([k, v]) => ` ${k}="${v.replace(/"/g, '&quot;')}"`).join('')
    + (cssom ? ` cssom="${cssom}"` : '');
  return node.tagName === 'br' || node.tagName === 'hr' || node.tagName === 'img' || node.tagName === 'input' || node.tagName === 'col'
    ? `<${node.tagName}${attrs}>`
    : `<${node.tagName}${attrs}>${inner}</${node.tagName}>`;
}

function renderHtml(doc, options = {}) {
  return html(render(doc, { document: fakeDocument, ...options }));
}

const BLOCK_CLASS = ['class'];
const ALLOWED_ELEMENTS = {
  p: BLOCK_CLASS, br: [], strong: [], em: [], s: [], u: [], sub: [], sup: [], code: ['data-lang'], pre: [], ul: ['class'],
  ol: ['start'], li: ['class'], blockquote: [], hr: [], h1: BLOCK_CLASS, h2: BLOCK_CLASS, h3: BLOCK_CLASS, h4: BLOCK_CLASS,
  h5: BLOCK_CLASS, h6: BLOCK_CLASS, a: ['href', 'rel', 'target', 'title', 'class'], img: ['src', 'alt', 'loading', 'width', 'height'],
  span: ['class', 'data-mention-id', 'aria-hidden'], div: ['class', 'data-island-id'], table: ['class'], colgroup: [], col: [],
  tbody: [], tr: [], th: ['colspan', 'rowspan', 'class'], td: ['colspan', 'rowspan', 'class'], details: ['class'], summary: [],
  input: ['type', 'disabled', 'checked'], figure: ['class'], figcaption: [], time: ['class', 'datetime'], i: ['data-icon'],
};

// Every CSSOM value the renderers write: a palette variable, a strict hex colour, a size or a flex weight.
const SAFE_STYLE = /^(?:var\(--jc-[a-z]+(?:-[a-z]+)?\)|#[0-9a-f]{6}|\d+(?:\.\d+)?(?:px|%)?)$/;

function assertSafeDom(node) {
  if (node.kind === 'element') {
    assert.ok(Object.hasOwn(ALLOWED_ELEMENTS, node.tagName), `element <${node.tagName}>`);
    for (const [name, value] of node.style.props) {
      assert.match(value, SAFE_STYLE, `<${node.tagName}> ${name}: ${value}`);
    }
    for (const [name, value] of node.attributes) {
      assert.ok(ALLOWED_ELEMENTS[node.tagName].includes(name), `<${node.tagName} ${name}>`);
      if (name === 'href') {
        assert.match(value, /^(https?:\/\/|mailto:)/i);
      }
      if (name === 'src') {
        assert.match(value, /^\/(?![/\\])/);
      }
    }
  }
  node.childNodes?.forEach(assertSafeDom);
}

// Node builders keep the corpus readable; the JSON they produce is the DocNode wire shape.
const doc = (...c) => ({ t: 'doc', c });
const p = (...c) => ({ t: 'p', c });
const t = (v) => ({ t: 'text', v });
const br = { t: 'br' };
const b = (...c) => ({ t: 'b', c });
const i = (...c) => ({ t: 'i', c });
const s = (...c) => ({ t: 's', c });
const code = (v) => ({ t: 'code', v });
const a = (href, ...c) => ({ t: 'a', href, c });
const mention = (id, name) => ({ t: 'mention', id, name });
const img = (blob, alt = 'image') => ({ t: 'img', blob, alt });
const li = (...c) => ({ t: 'li', c });
const ul = (...items) => ({ t: 'ul', c: items });
const ol = (start, ...items) => ({ t: 'ol', start, c: items });
const quote = (...c) => ({ t: 'quote', c });
const pre = (v, lang) => (lang === undefined ? { t: 'pre', v } : { t: 'pre', lang, v });
const h = (level, ...c) => ({ t: 'h', level, c });
const hr = { t: 'hr' };
const island = (id, ...c) => ({ t: 'island', id, c });
const nest = (depth, inner) => (depth === 0 ? inner : quote(nest(depth - 1, inner)));

const RETRO = DIALECT_RETRO;
const JIRA = DIALECT_JIRA;
const JIRA_DESCRIPTION = DIALECT_JIRA_DESCRIPTION;

// `written` is the canonical form toMarkup gives the parsed doc, when it differs from `input`.
const CORPUS = [
  { name: 'empty', input: '', doc: doc() },
  { name: 'blank lines only', input: '\n  \n\t\n', doc: doc(), written: '' },
  { name: 'plain text', input: 'hello world', doc: doc(p(t('hello world'))) },
  { name: 'newline is a line break', input: 'a\nb', doc: doc(p(t('a'), br, t('b'))) },
  { name: 'CR and CRLF normalised', input: 'a\r\nb\r\rc', doc: doc(p(t('a'), br, t('b')), p(t('c'))), written: 'a\nb\n\nc' },
  { name: 'paragraph lines are trimmed', input: '  a  \n\tb', doc: doc(p(t('a'), br, t('b'))), written: 'a\nb' },
  { name: 'blank lines separate paragraphs', input: 'a\n\n\nb', doc: doc(p(t('a')), p(t('b'))), written: 'a\n\nb' },
  { name: 'bold', input: '**bold**', doc: doc(p(b(t('bold')))) },
  { name: 'italic with star', input: '*it*', doc: doc(p(i(t('it')))), written: '_it_' },
  { name: 'italic with underscore', input: '_it_', doc: doc(p(i(t('it')))) },
  { name: 'strike', input: '~~gone~~', doc: doc(p(s(t('gone')))) },
  { name: 'single tilde is literal', input: '~a~', doc: doc(p(t('~a~'))), written: '\\~a\\~' },
  { name: 'opener before whitespace is literal', input: '** a**', doc: doc(p(t('** a**'))), written: '\\*\\* a\\*\\*' },
  { name: 'unclosed bold is literal', input: '**a', doc: doc(p(t('**a'))), written: '\\*\\*a' },
  { name: 'snake_case stays literal', input: 'snake_case_name', doc: doc(p(t('snake_case_name'))), written: 'snake\\_case\\_name' },
  { name: 'intraword star italic', input: 'x*y*z', doc: doc(p(t('x'), i(t('y')), t('z'))) },
  { name: 'italic inside bold', input: '**a *b* c**', doc: doc(p(b(t('a '), i(t('b')), t(' c')))), written: '**a _b_ c**' },
  { name: 'bold inside italic', input: '*a **b** c*', doc: doc(p(i(t('a '), b(t('b')), t(' c')))), written: '_a **b** c_' },
  { name: 'triple star is italic in bold', input: '***a***', doc: doc(p(b(i(t('a'))))), written: '**_a_**' },
  { name: 'italic inside italic takes the other delimiter', input: '_a *b* c_', doc: doc(p(i(t('a '), i(t('b')), t(' c')))) },
  { name: 'adjacent italics', input: '_a_*b*', doc: doc(p(i(t('a')), i(t('b')))) },
  { name: 'emphasis spans a line break', input: '**a\nb**', doc: doc(p(b(t('a'), br, t('b')))) },
  { name: 'code span', input: 'run `npm *test*` now', doc: doc(p(t('run '), code('npm *test*'), t(' now'))) },
  { name: 'double backtick code span', input: '``a ` b``', doc: doc(p(code('a ` b'))) },
  { name: 'code span strips one padding space', input: '`` `x` ``', doc: doc(p(code('`x`'))) },
  { name: 'unclosed backtick is literal', input: 'a ` b', doc: doc(p(t('a ` b'))), written: 'a \\` b' },
  { name: 'code span hides closer', input: '*a `*` b*', doc: doc(p(i(t('a '), code('*'), t(' b')))), written: '_a `*` b_' },
  { name: 'escapes', input: '\\*not\\* \\_em\\_ \\\\ \\a', doc: doc(p(t('*not* _em_ \\ \\a'))), written: '\\*not\\* \\_em\\_ \\\\ \\\\a' },
  { name: 'https link', input: '[site](https://example.com/a?b=1#c)', doc: doc(p(a('https://example.com/a?b=1#c', t('site')))) },
  { name: 'mailto link', input: '[mail](mailto:a@b.c)', doc: doc(p(a('mailto:a@b.c', t('mail')))) },
  { name: 'link label with emphasis', input: '[**big** news](http://x.io)', doc: doc(p(a('http://x.io', b(t('big')), t(' news')))) },
  { name: 'uppercase scheme accepted', input: '[x](HTTPS://A.B)', doc: doc(p(a('HTTPS://A.B', t('x')))) },
  { name: 'javascript link is literal', input: '[x](javascript:alert(1))', doc: doc(p(t('[x](javascript:alert(1))'))), written: '\\[x\\](javascript:alert(1))' },
  { name: 'data link is literal', input: '[x](data:text/html,hi)', doc: doc(p(t('[x](data:text/html,hi)'))), written: '\\[x\\](data:text/html,hi)' },
  { name: 'scheme-relative link is literal', input: '[x](//evil.com)', doc: doc(p(t('[x](//evil.com)'))), written: '\\[x\\](//evil.com)' },
  { name: 'link with a space is literal', input: '[x](https://a b)', doc: doc(p(t('[x](https://a b)'))), written: '\\[x\\](https://a b)' },
  { name: 'empty label is literal', input: '[](https://a.b)', doc: doc(p(t('[](https://a.b)'))), written: '\\[\\](https://a.b)' },
  { name: 'mention', input: `hi [[mention:${U1}:Ann Lee]]!`, doc: doc(p(t('hi '), mention(U1, 'Ann Lee'), t('!'))) },
  { name: 'mention id lower-cased', input: `[[mention:${U1.toUpperCase()}:A]]`, doc: doc(p(mention(U1, 'A'))), written: `[[mention:${U1}:A]]` },
  { name: 'mention with a bad uuid is literal', input: '[[mention:abc:A]]', doc: doc(p(t('[[mention:abc:A]]'))), written: '\\[\\[mention:abc:A\\]\\]' },
  { name: 'mention is literal in Jira text', input: `[[mention:${U1}:A]]`, dialect: JIRA, doc: doc(p(t(`[[mention:${U1}:A]]`))), written: `\\[\\[mention:${U1}:A\\]\\]` },
  { name: 'image', input: `![image](blob:${U2})`, doc: doc(p(img(U2))) },
  { name: 'image keeps its alt', input: `![cat pic](blob:${U2.toUpperCase()})`, doc: doc(p(img(U2, 'cat pic'))), written: `![cat pic](blob:${U2})` },
  { name: 'remote image is a link', input: '![x](https://evil.com/a.png)', doc: doc(p(t('!'), a('https://evil.com/a.png', t('x')))), written: '\\![x](https://evil.com/a.png)' },
  { name: 'raw HTML is literal text', input: '<img src=x onerror=alert(1)>', doc: doc(p(t('<img src=x onerror=alert(1)>'))) },
  { name: 'entity text stays literal', input: '&lt;div&gt;', doc: doc(p(t('&lt;div&gt;'))) },
  { name: 'bullet list', input: '- one\n- **two**', doc: doc(ul(li(p(t('one'))), li(p(b(t('two')))))) },
  { name: 'star bullet is not a list', input: '* one', doc: doc(p(t('* one'))), written: '\\* one' },
  { name: 'dash without space is text', input: '-one', doc: doc(p(t('-one'))) },
  { name: 'bare dash is an empty item', input: '-', doc: doc(ul(li())) },
  { name: 'ordered list keeps its first number', input: '3. a\n7. b', doc: doc(ol(3, li(p(t('a'))), li(p(t('b'))))), written: '3. a\n4. b' },
  { name: 'ordered list from zero', input: '0. a', doc: doc(ol(0, li(p(t('a'))))) },
  { name: 'list interrupts a paragraph', input: 'intro\n- a\nafter', doc: doc(p(t('intro')), ul(li(p(t('a')))), p(t('after'))), written: 'intro\n- a\n\nafter' },
  { name: 'indented continuation joins the item', input: '- a\n  b\n\n  c', doc: doc(ul(li(p(t('a'), br, t('b')), p(t('c'))))) },
  { name: 'nested list', input: '- a\n  - b', doc: doc(ul(li(p(t('a')), ul(li(p(t('b'))))))) },
  { name: 'indented dash is a paragraph', input: '  - a', doc: doc(p(t('- a'))), written: '\\- a' },
  { name: 'quote', input: '> a\n> b', doc: doc(quote(p(t('a'), br, t('b')))) },
  { name: 'quote with blocks', input: '> - a\n>\n> b', doc: doc(quote(ul(li(p(t('a')))), p(t('b')))) },
  { name: 'nested quote', input: '>> deep', doc: doc(quote(quote(p(t('deep'))))), written: '> > deep' },
  { name: 'bare quote marker', input: '>', doc: doc(quote()) },
  { name: 'quote depth is capped', input: `${'>'.repeat(17)}x`, doc: doc(nest(16, p(t('>x')))), written: `${'> '.repeat(16)}\\>x` },
  { name: 'fenced code', input: '```js\nlet a = **b**;\n\n<x>\n```', doc: doc(pre('let a = **b**;\n\n<x>', 'js')) },
  { name: 'unclosed fence runs to the end', input: '```\na\nb', doc: doc(pre('a\nb')), written: '```\na\nb\n```' },
  { name: 'longer fence needs a longer closer', input: '````\n```\n````', doc: doc(pre('```')) },
  { name: 'fence with a bad info string is text', input: '```js x\na', doc: doc(p(t('```js x'), br, t('a'))), written: '\\`\\`\\`js x\na' },
  { name: 'heading is text in retro', input: '# Title', doc: doc(p(t('# Title'))), written: '\\# Title' },
  { name: 'heading in Jira text', input: '## Title', dialect: JIRA, doc: doc(h(2, t('Title'))) },
  { name: 'rule in Jira text', input: 'a\n---\nb', dialect: JIRA, doc: doc(p(t('a')), hr, p(t('b'))), written: 'a\n\n---\n\nb' },
  { name: 'rule is text in retro', input: '---', doc: doc(p(t('---'))), written: '\\---' },
  { name: 'island is literal outside Jira descriptions', input: '[[island:abc123]]', dialect: JIRA, doc: doc(p(t('[[island:abc123]]'))), written: '\\[\\[island:abc123\\]\\]' },
  { name: 'island in a Jira description', input: 'a\n[[island:abc123def456]]\nb', dialect: JIRA_DESCRIPTION, doc: doc(p(t('a')), island('abc123def456'), p(t('b'))), written: 'a\n\n[[island:abc123def456]]\n\nb' },
  { name: 'island ref inside a line is literal', input: 'x [[island:abc]]', dialect: JIRA_DESCRIPTION, doc: doc(p(t('x [[island:abc]]'))), written: 'x \\[\\[island:abc\\]\\]' },
  { name: 'island is never nested', input: '> [[island:abc]]', dialect: JIRA_DESCRIPTION, doc: doc(quote(p(t('[[island:abc]]')))), written: '> \\[\\[island:abc\\]\\]' },
  { name: 'non-ASCII letters do not block underscore emphasis', input: 'é_a_', doc: doc(p(t('é'), i(t('a')))) },
  { name: 'unicode text', input: '**日本語** 😀', doc: doc(p(b(t('日本語')), t(' 😀'))) },
];

const DIALECT_NAMES = new Map([
  ['retro', RETRO],
  ['jira', JIRA],
  ['jiradescription', JIRA_DESCRIPTION],
]);

// tests/fixtures/markup (U4) is the corpus both parsers must agree on; it is read whenever present.
function loadSharedFixtures() {
  if (!existsSync(FIXTURES)) {
    return [];
  }
  return readdirSync(FIXTURES)
    .filter((name) => name.endsWith('.json'))
    .sort()
    .flatMap((name) => {
      const file = JSON.parse(readFileSync(`${FIXTURES}${name}`, 'utf8'));
      const cases = Array.isArray(file) ? file : file.cases;
      assert.ok(Array.isArray(cases), `${name}: expected an array of cases or {cases: [...]}`);
      return cases.map((c, index) => {
        const input = c.input ?? c.markup;
        // U4's files carry the raw text in `input` and the canonical written form in `markup`.
        const written = c.written ?? c.canonical ?? (c.input === undefined ? undefined : c.markup);
        const dialect = DIALECT_NAMES.get(String(c.dialect ?? 'retro').toLowerCase().replace(/[^a-z]/g, ''));
        assert.equal(typeof input, 'string', `${name}#${index}: no markup`);
        assert.ok(dialect, `${name}#${index}: unknown dialect ${c.dialect}`);
        assert.ok(c.doc, `${name}#${index}: no doc`);
        return { name: `${name}: ${c.name ?? index}`, input, dialect, doc: c.doc, written };
      });
    });
}

const ALL_CASES = [...CORPUS, ...loadSharedFixtures()];

test('corpus parses to the expected DocNode', () => {
  for (const c of ALL_CASES) {
    assert.deepEqual(parse(c.input, { dialect: c.dialect }), c.doc, c.name);
  }
});

test('corpus writes canonically and reparses to the same DocNode', () => {
  for (const c of ALL_CASES) {
    const written = toMarkup(c.doc);
    assert.equal(written, c.written ?? c.input, c.name);
    assert.deepEqual(parse(written, { dialect: c.dialect }), c.doc, c.name);
  }
});

test('every corpus DocNode is allow-listed and renders safely', () => {
  for (const c of ALL_CASES) {
    assert.ok(isAllowedDoc(c.doc), c.name);
    assertSafeDom(render(c.doc, { document: fakeDocument }));
  }
});

test('ADF-only nodes have no markup form', () => {
  assert.throws(() => toMarkup(doc({ t: 'panel', kind: 'info', c: [] })), TypeError);
  assert.throws(() => toMarkup(doc(p({ t: 'u', c: [t('x')] }))), TypeError);
});

test('writer picks a fence longer than any backtick run', () => {
  const block = doc(pre('```\n````', 'sh'));
  assert.equal(toMarkup(block), '`````sh\n```\n````\n`````');
  assert.deepEqual(parse(toMarkup(block)), block);
  assert.equal(toMarkup(doc(p(code('a``b')))), '```a``b```');
});

test('renderer output for each markup construct', () => {
  const names = new Map([[U1, 'Ann']]);
  const input = `**b** *i* ~~s~~ \`c\` [l](https://x.y) [[mention:${U1}:Forged Name]] ![pic](blob:${U2})\nnext\n\n`
    + '- a\n\n3. b\n\n> q\n\n```py\nx\n```';
  assert.equal(
    renderHtml(parse(input), { resolveMention: (id) => names.get(id) }),
    '<p><strong>b</strong> <em>i</em> <s>s</s> <code>c</code> '
      + '<a href="https://x.y" rel="noopener noreferrer nofollow" target="_blank">l</a> '
      + `<span class="mention" data-mention-id="${U1}">@Ann</span> `
      + `<img src="/blob/${U2}" alt="pic" loading="lazy"><br>next</p>`
      + '<ul><li><p>a</p></li></ul><ol start="3"><li><p>b</p></li></ol>'
      + '<blockquote><p>q</p></blockquote><pre><code data-lang="py">x</code></pre>',
  );
});

test('mention names come from the resolver, never from the token', () => {
  const tokenOnly = parse(`[[mention:${U1}:Admin says click here]]`);
  assert.equal(renderHtml(tokenOnly), `<p><span class="mention" data-mention-id="${U1}">@unknown</span></p>`);
  assert.equal(
    renderHtml(tokenOnly, { resolveMention: () => '<img onerror=x>' }),
    `<p><span class="mention" data-mention-id="${U1}">@&lt;img onerror=x></span></p>`,
  );
});

const fakeIcon = (name) => {
  const i = fakeDocument.createElement('i');
  i.setAttribute('data-icon', name);
  return i;
};
const extend = jiraRenderer({ icon: fakeIcon, fileHref: () => 'https://example.atlassian.net/browse/K-1' });
const jiraHtml = (d, options = {}) => renderHtml(d, { extend, ...options });

test('Jira DocNodes render with every field re-checked', () => {
  const jira = doc(
    h(2, t('Title')),
    hr,
    { t: 'panel', kind: 'warning', c: [p(t('careful'))] },
    { t: 'table', c: [{ t: 'tr', c: [{ t: 'th', c: [p(t('h'))] }, { t: 'td', c: [p(t('c'))] }] }] },
    { t: 'expand', title: 'More', c: [p(t('hidden'))] },
    { t: 'tasks', c: [{ t: 'task', done: true, c: [t('done')] }, { t: 'task', done: false, c: [t('open')] }] },
    p({ t: 'u', c: [t('u')] }, { t: 'status', v: 'IN PROGRESS', color: 'blue' }, { t: 'attachment', id: '10042', alt: 'shot.png' }),
  );
  assert.ok(isAllowedDoc(jira));
  const imageSrc = (n) => (n.t === 'attachment' ? `/api/teams/t/jira/issues/K-1/attachments/${n.id}` : null);
  assert.equal(
    jiraHtml(jira, { imageSrc }),
    '<h2>Title</h2><hr><div class="panel panel-warning"><span class="panel-icon" aria-hidden="true"><i data-icon="triangle-alert"></i></span>'
      + '<div class="panel-body"><p>careful</p></div></div>'
      + '<div class="table-wrap"><table><tbody><tr><th><p>h</p></th><td><p>c</p></td></tr></tbody></table></div>'
      + '<details class="expand"><summary>More</summary><div class="expand-body"><p>hidden</p></div></details>'
      + '<ul class="tasks"><li class="task"><input type="checkbox" disabled="" checked="">done</li>'
      + '<li class="task"><input type="checkbox" disabled="">open</li></ul>'
      + '<p><u>u</u><span class="status status-blue">IN PROGRESS</span>'
      + '<img src="/api/teams/t/jira/issues/K-1/attachments/10042" alt="shot.png" loading="lazy"></p>',
  );
  assert.equal(
    renderHtml(doc({ t: 'panel', kind: 'info', c: [p(t('kept'))] }, p({ t: 'status', v: 'S', color: 'red' }))),
    '<p>kept</p><p>S</p>',
    'without the Jira extension, ADF-only kinds keep only their content',
  );
  assert.equal(
    renderHtml(doc(p({ t: 'attachment', id: '1', alt: '' }))),
    '<p><span class="image-missing">image (open in Jira)</span></p>',
  );
});

test('Jira colour marks, sub/sup, link titles, alignment and indentation', () => {
  const jira = doc(
    { t: 'p', align: 'center', c: [
      { t: 'fg', color: 'blue-bolder', c: [t('blue')] },
      { t: 'bg', color: '#a1b2c3', c: [{ t: 'sup', c: [t('2')] }, { t: 'sub', c: [t('n')] }] },
      { t: 'a', href: 'https://example.com', title: 'Tip', c: [t('link')] },
    ] },
    { t: 'p', indent: 3, c: [t('in')] },
    { t: 'h', level: 3, align: 'end', indent: 1, c: [t('Head')] },
    { t: 'p', align: 'left', indent: 9, c: [t('plain')] },
  );
  assert.ok(isAllowedDoc(doc(...jira.c.slice(0, 3))));
  assert.equal(
    jiraHtml(jira),
    '<p class="align-center"><span class="text-color" cssom="color: var(--jc-blue-bolder)">blue</span>'
      + '<span class="highlight" cssom="background-color: #a1b2c3"><sup>2</sup><sub>n</sub></span>'
      + '<a href="https://example.com" rel="noopener noreferrer nofollow" target="_blank" title="Tip">link</a></p>'
      + '<p class="indent-3">in</p><h3 class="align-end indent-1">Head</h3><p>plain</p>',
  );
  assert.equal(colorValue('surface'), 'var(--jc-surface)');
  for (const bad of ['#ABCDEF', '#abc', 'red', 'blue-bright', 'var(--x)', '#fff;x', null, 7]) {
    assert.equal(colorValue(bad), null, String(bad));
  }
});

test('Jira panels of every kind, custom colour and emoji', () => {
  const kinds = ['info', 'note', 'tip', 'success', 'warning', 'error'];
  const icons = ['info', 'clipboard-list', 'lightbulb', 'circle-check', 'triangle-alert', 'circle-x'];
  assert.equal(
    jiraHtml(doc(...kinds.map((kind) => ({ t: 'panel', kind, c: [] })))),
    kinds.map((kind, i) => `<div class="panel panel-${kind}"><span class="panel-icon" aria-hidden="true"><i data-icon="${icons[i]}"></i></span>`
      + '<div class="panel-body"></div></div>').join(''),
  );
  assert.equal(
    jiraHtml(doc(
      { t: 'panel', kind: 'custom', color: 'green-subtlest', icon: '🚀', c: [p(t('go'))] },
      { t: 'panel', kind: 'custom', color: 'url(x)', c: [] },
      { t: 'panel', kind: 'bogus', c: [] },
    )),
    '<div class="panel panel-custom" cssom="background-color: var(--jc-green-subtlest)"><span class="panel-icon panel-emoji" aria-hidden="true">🚀</span>'
      + '<div class="panel-body"><p>go</p></div></div>'
      + '<div class="panel panel-custom"><div class="panel-body"></div></div>'
      + '<div class="panel panel-info"><span class="panel-icon" aria-hidden="true"><i data-icon="info"></i></span><div class="panel-body"></div></div>',
  );
});

test('Jira tables keep spans, widths, cell colours, layout and the number column', () => {
  const cell = (kind, text, extra = {}) => ({ t: kind, ...extra, c: [p(t(text))] });
  const table = {
    t: 'table', layout: 'center', numbered: true, width: 400,
    c: [
      { t: 'tr', c: [cell('th', 'A', { colwidth: [100] }), cell('th', 'B', { colspan: 2, colwidth: [80, 120], bg: 'blue-subtlest' })] },
      { t: 'tr', c: [cell('th', 'r', { rowspan: 2 }), cell('td', 'x', { bg: '#fafafa' }), cell('td', 'y')] },
      { t: 'tr', c: [cell('td', 'z', { colspan: 99, rowspan: 1, bg: 'nope' })] },
    ],
  };
  assert.ok(isAllowedDoc(doc({ ...table, c: table.c.slice(0, 2) })));
  assert.equal(
    jiraHtml(doc(table)),
    '<div class="table-wrap"><table class="layout-center" cssom="width: 342px">'
      + '<colgroup><col cssom="width: 42px"><col cssom="width: 100px"><col cssom="width: 80px"><col cssom="width: 120px"></colgroup><tbody>'
      + '<tr><th class="number"></th><th><p>A</p></th><th colspan="2" cssom="background-color: var(--jc-blue-subtlest)"><p>B</p></th></tr>'
      + '<tr><td class="number">1</td><th rowspan="2"><p>r</p></th><td cssom="background-color: #fafafa"><p>x</p></td><td><p>y</p></td></tr>'
      + '<tr><td class="number">2</td><td><p>z</p></td></tr></tbody></table></div>',
  );
  assert.equal(
    jiraHtml(doc({ t: 'table', c: [{ t: 'tr', c: [cell('td', 'a', { colwidth: [50, 60] })] }] })),
    '<div class="table-wrap"><table><tbody><tr><td><p>a</p></td></tr></tbody></table></div>',
    'column widths that do not match the spans leave the table to size itself',
  );
});

test('Jira layouts, expands, nested tasks and decisions', () => {
  const jira = doc(
    { t: 'layout', c: [{ t: 'col', width: 33.33, c: [p(t('one'))] }, { t: 'col', width: 0, c: [p(t('two'))] }] },
    { t: 'expand', title: 'Details', c: [p(t('inside'))] },
    { t: 'tasks', c: [{ t: 'task', done: false, c: [t('parent')] }, { t: 'tasks', c: [{ t: 'task', done: true, c: [t('child')] }] }] },
    { t: 'decisions', c: [{ t: 'decision', done: true, c: [t('yes')] }, { t: 'decision', done: false, c: [t('maybe')] }] },
  );
  assert.ok(isAllowedDoc(jira));
  assert.equal(
    jiraHtml(jira),
    '<div class="layout"><div class="layout-col" cssom="flex-grow: 33.33"><p>one</p></div><div class="layout-col"><p>two</p></div></div>'
      + '<details class="expand"><summary>Details</summary><div class="expand-body"><p>inside</p></div></details>'
      + '<ul class="tasks"><li class="task"><input type="checkbox" disabled="">parent</li>'
      + '<li class="task-nest"><ul class="tasks"><li class="task"><input type="checkbox" disabled="" checked="">child</li></ul></li></ul>'
      + '<ul class="decisions"><li class="decision is-decided"><span class="decision-icon" aria-hidden="true"><i data-icon="split"></i></span>yes</li>'
      + '<li class="decision"><span class="decision-icon" aria-hidden="true"><i data-icon="split"></i></span>maybe</li></ul>',
  );
});

test('Jira media keeps its layout, width and caption; files are chips', () => {
  const imageSrc = (n) => (n.t === 'attachment' ? `/att/${n.id}` : null);
  const jira = doc(
    { t: 'media', layout: 'wrap-left', width: 320, unit: 'px', caption: [t('The '), b(t('flow'))], c: [{ t: 'attachment', id: '7', alt: 'flow.png', w: 1280, h: 720 }] },
    { t: 'media', layout: 'center', width: 50, unit: '%', c: [{ t: 'attachment', id: '8', alt: 'b.png' }] },
    { t: 'files', c: [{ t: 'attachment', id: '9', alt: 'c.png' }, { t: 'file', id: '10', name: 'spec.pdf' }] },
    { t: 'media', layout: 'sideways', width: 99999, unit: 'px', c: [t('image (open in Jira)')] },
  );
  assert.ok(isAllowedDoc(doc(...jira.c.slice(0, 3))));
  assert.equal(
    jiraHtml(jira, { imageSrc }),
    '<figure class="media layout-wrap-left" cssom="width: 320px"><img src="/att/7" alt="flow.png" loading="lazy" width="1280" height="720">'
      + '<figcaption>The <strong>flow</strong></figcaption></figure>'
      + '<figure class="media layout-center" cssom="width: 50%"><img src="/att/8" alt="b.png" loading="lazy"></figure>'
      + '<div class="media-group"><img src="/att/9" alt="c.png" loading="lazy">'
      + '<a class="file-chip" href="https://example.atlassian.net/browse/K-1" rel="noopener noreferrer nofollow" target="_blank" title="Open in Jira">'
      + '<span class="file-icon" aria-hidden="true"><i data-icon="paperclip"></i></span>spec.pdf</a></div>'
      + '<figure class="media">image (open in Jira)</figure>',
  );
  const unlinked = renderHtml(doc(p({ t: 'file', id: '10', name: 'a.txt' })), { extend: jiraRenderer({ fileHref: () => 'javascript:x' }) });
  assert.equal(unlinked, '<p><span class="file-chip"><span class="file-icon" aria-hidden="true"></span>a.txt</span></p>');
});

test('Jira lozenges: status styles, mention pill, date, cards', () => {
  const jira = doc(p(
    { t: 'status', v: 'DONE', color: 'green', bold: true },
    { t: 'status', v: 'Odd', color: 'pink' },
    { t: 'user', v: '@Ann Lee' },
    { t: 'date', v: '2026-01-02' },
    { t: 'date', v: '2026-1-2' },
    { t: 'card', href: 'https://example.atlassian.net/browse/RS-3' },
    { t: 'card', href: 'https://example.com/a/b', block: true },
    { t: 'card', href: 'javascript:alert(1)' },
  ));
  assert.equal(
    jiraHtml(jira),
    '<p><span class="status status-green is-bold">DONE</span><span class="status status-neutral">Odd</span>'
      + '<span class="jira-user">@Ann Lee</span>'
      + `<time class="date-lozenge" datetime="2026-01-02">${formatDay('2026-01-02')}</time>`
      + '<a class="smart-card" href="https://example.atlassian.net/browse/RS-3" rel="noopener noreferrer nofollow" target="_blank">'
      + '<span class="card-icon" aria-hidden="true"><i data-icon="link"></i></span>RS-3</a>'
      + '<a class="smart-card is-block" href="https://example.com/a/b" rel="noopener noreferrer nofollow" target="_blank">'
      + '<span class="card-icon" aria-hidden="true"><i data-icon="link"></i></span>example.com/a/b</a></p>',
  );
  assert.equal(formatDay('2026-01-02'), new Date(Date.UTC(2026, 0, 2)).toLocaleDateString(undefined, { timeZone: 'UTC', dateStyle: 'medium' }));
  assert.equal(formatDay('2026-02-30T00'), null);
});

test('hostile Jira fields never reach the DOM or the CSSOM', () => {
  const hostile = doc(
    { t: 'p', align: '" onclick="x', indent: '1;x', c: [{ t: 'fg', color: 'red;background:url(//evil.com)', c: [t('fg')] }] },
    { t: 'panel', kind: 'custom', color: 'expression(alert(1))', icon: '<img src=x onerror=alert(1)>'.repeat(3), c: [] },
    { t: 'table', layout: '"><script>', width: '1px;x', numbered: 'yes', c: [{ t: 'tr', c: [{ t: 't d', c: [] }, { t: 'td', colspan: '2" onclick="x', rowspan: 1e9, colwidth: ['9px;x'], bg: '#12345g', c: [] }] }] },
    { t: 'media', layout: 'x onclick', width: '50%;x', unit: 'vh', caption: 'not an array', c: [{ t: 'attachment', id: '1/../x', alt: '', w: '1" onload="x', h: 2 }] },
    { t: 'col', width: '1;x', c: [] },
    p({ t: 'card', href: '//evil.com' }, { t: 'date', v: '"><script>' }, { t: 'a', href: 'javascript:x', title: 't', c: [t('js')] }, { t: 'file', id: '1', name: '<b>' }),
  );
  assert.equal(isAllowedDoc(hostile), false);
  const fragment = render(hostile, { document: fakeDocument, extend, imageSrc: (n) => `/a/${n.id}` });
  assertSafeDom(fragment);
  const out = html(fragment);
  assert.doesNotMatch(out, /<script|<img src="x|javascript:|onclick|onload|onerror=alert|evil\.com|\.\.\/|expression|url\(/);
});

test('islands render their content, a supplied subtree, or a placeholder', () => {
  const subtree = island('abc123def456', p(t('table here')));
  assert.equal(renderHtml(doc(subtree)), '<div class="island" data-island-id="abc123def456"><p>table here</p></div>');
  const ref = parse('[[island:abc123def456]]', { dialect: JIRA_DESCRIPTION });
  assert.equal(
    renderHtml(ref, { islands: new Map([['abc123def456', subtree]]) }),
    '<div class="island" data-island-id="abc123def456"><p>table here</p></div>',
  );
  assert.equal(renderHtml(ref), '<div class="island island-missing" data-island-id="abc123def456">content unavailable</div>');
});

test('hostile DocNodes from the wire cannot produce scriptable DOM', () => {
  const hostile = {
    t: 'doc',
    onclick: 'alert(1)',
    c: [
      { t: 'script', c: [t('alert(1)')] },
      { t: 'iframe', v: 'frame text' },
      p(
        a('javascript:alert(1)', t('js')),
        a(' https://x.y', t('space')),
        a('//evil.com', t('relative')),
        { t: 'a', href: 'https://ok.io', onmouseover: 'x', c: [t('ok')] },
        { t: 'img', blob: '../../admin', alt: 'x' },
        { t: 'img', blob: U1, alt: '" onerror="alert(1)' },
        { t: 'attachment', id: '1/../../x', alt: '' },
        { t: 'mention', id: '" onmouseover="x', name: 'n' },
        { t: 'status', v: 's', color: 'red;background:url(x)' },
        { t: 'code', v: '<script>alert(1)</script>' },
        { t: 'h', level: '1 onclick=x', c: [] },
      ),
      { t: 'pre', lang: '" onclick="x', v: 'x' },
      { t: 'panel', kind: '"><script>', c: [] },
      { t: 'island', id: '"><img>', c: [p(t('hidden'))] },
      { t: 'ol', start: '2" onclick="x', c: [] },
      null,
      'raw <b>string</b>',
    ],
  };
  assert.equal(isAllowedDoc(hostile), false);
  const imageSrc = (n) => (n.t === 'img' ? `//evil.com/${n.blob}` : `/a/${n.id}`);
  const fragment = render(hostile, { document: fakeDocument, imageSrc });
  assertSafeDom(fragment);
  const out = html(fragment);
  assert.doesNotMatch(out, /<script|<iframe|javascript:|onclick|onmouseover|evil\.com|\.\.\//);
  assert.match(out, /alert\(1\)/, 'unknown kinds keep their text content');
});

test('renderer depth is capped', () => {
  const out = renderHtml(doc(nest(100, p(t('leaf')))));
  assert.match(out, /content truncated/);
  assert.doesNotMatch(out, /leaf/);
});

test('renderer and parser source use no banned sinks', () => {
  const banned = /\b(innerHTML|outerHTML|insertAdjacentHTML|document\.write|setHTMLUnsafe|createContextualFragment|DOMParser|eval)\b|new Function/;
  for (const file of ['parse.js', 'render.js', 'doc.js', 'jira.js', 'allow.js']) {
    assert.doesNotMatch(readFileSync(`${ROOT}web/src/markup/${file}`, 'utf8'), banned, file);
  }
});

test('mention, image and island extraction', () => {
  const retro = parse(`[[mention:${U1}:a]] [[mention:${U2}:b]] [[mention:${U1}:c]] ![image](blob:${U2})`);
  assert.deepEqual(mentionIds(retro), [U1, U2]);
  assert.deepEqual(imageBlobIds(retro), [U2]);
  assert.deepEqual(islandIds(parse('[[island:i1]]\n\nx\n\n[[island:i2]]', { dialect: JIRA_DESCRIPTION })), ['i1', 'i2']);
  assert.deepEqual(islandIds(parse('[[island:i1]]')), []);
});

test('checkMarkup enforces the stored limits', () => {
  assert.deepEqual(checkMarkup('ok', 'card').doc, doc(p(t('ok'))));
  assert.equal(checkMarkup('  \r\n ', 'card').error, 'empty');
  assert.equal(checkMarkup('a\u0000b', 'card').error, 'invalid_character');
  assert.equal(checkMarkup('a\ud83d', 'card').error, 'invalid_character');
  assert.equal(checkMarkup('😀'.repeat(8000), 'card').error, undefined, 'lengths count code points, as char_length does');
  assert.equal(checkMarkup('a'.repeat(8001), 'card').error, 'too_long');
  assert.equal(checkMarkup('a'.repeat(4001), 'comment').error, 'too_long');
  assert.equal(checkMarkup('a'.repeat(32000), 'jiraText').error, undefined);
  const images = (n) => Array.from({ length: n }, () => `![image](blob:${U1})`).join(' ');
  assert.equal(checkMarkup(images(8), 'card').error, undefined);
  assert.equal(checkMarkup(images(9), 'comment').error, 'too_many_images');
});

// Deterministic PRNG so a failing fuzz input is reproducible from its iteration number.
function mulberry32(seed) {
  return () => {
    seed = (seed + 0x6d2b79f5) | 0;
    let r = Math.imul(seed ^ (seed >>> 15), 1 | seed);
    r = (r + Math.imul(r ^ (r >>> 7), 61 | r)) ^ r;
    return ((r ^ (r >>> 14)) >>> 0) / 4294967296;
  };
}

// The same pieces as the C# fuzz test (MarkupFuzzTests), so both parsers see the same shapes.
const FUZZ_PIECES = [
  '*', '**', '***', '_', '__', '~', '~~', '`', '``', '```', '\\', '[', ']', '(', ')', '!', '!',
  '-', '- ', '1. ', '10. ', '> ', '>', '#', '## ', '---', '\n', '\n', '\n\n', '\r\n', '\r', ' ', '  ', '   ', '\t',
  'a', 'b', 'word', 'x1', 'é', '😀', '\ud83d', ':', '.', '7', '999999999. ', '\0', '<b>', '&lt;div&gt;',
  '](https://example.com/a_b)', '](mailto:a@b.c)', '](javascript:alert(1))', '](http://x y)',
  '[[mention:0b0e7d4e-5a7c-4f5e-9a51-3a0d9c1e2f01:Ann]]', '[[mention:', '[[mention:0b0e7d4e-5a7c-4f5e-9a51-3a0d9c1e2f01:',
  '![image](blob:6f1c2d3e-4b5a-4c6d-8e7f-901a2b3c4d5e)', '](blob:6f1c2d3e-4b5a-4c6d-8e7f-901a2b3c4d5e)', '![',
  '[[island:abc123def456]]', '[[island:', '```js', '```\n', '\n```\n',
];

const DIALECTS = [RETRO, JIRA, JIRA_DESCRIPTION];

// The kinds each dialect may produce; anything else in a parsed doc is a grammar leak.
function disallowedKind(parsed, dialect) {
  for (const node of walk(parsed)) {
    const allowed = ['doc', 'p', 'ul', 'ol', 'li', 'quote', 'pre', 'text', 'code', 'a', 'br', 'b', 'i', 's'].includes(node.t)
      || (['mention', 'img'].includes(node.t) && dialect === RETRO)
      || (['h', 'hr'].includes(node.t) && dialect !== RETRO)
      || (node.t === 'island' && dialect === JIRA_DESCRIPTION && parsed.c.includes(node));
    if (!allowed) {
      return node.t;
    }
  }
  return null;
}

test('fuzz: parse never throws, stays in its dialect, writes to a fixed point and renders safely', () => {
  const random = mulberry32(20261020);
  for (let iteration = 0; iteration < 30000; iteration++) {
    const pieces = Math.floor(random() * 40);
    let input = '';
    for (let k = 0; k < pieces; k++) {
      input += FUZZ_PIECES[Math.floor(random() * FUZZ_PIECES.length)];
    }
    const dialect = DIALECTS[iteration % 3];
    const context = `iteration ${iteration} (${dialect}): ${JSON.stringify(input)}`;
    const parsed = parse(input, { dialect });
    assert.equal(disallowedKind(parsed, dialect), null, context);
    assert.ok(isAllowedDoc(parsed), context);
    const written = toMarkup(parsed);
    const reparsed = parse(written, { dialect });
    assert.deepEqual(reparsed, parsed, `${context} written ${JSON.stringify(written)}`);
    assert.equal(toMarkup(reparsed), written, context);
    assertSafeDom(render(parsed, { document: fakeDocument }));
  }
});

test('pathological input parses and writes in linear-ish time', () => {
  const inputs = [
    '*a'.repeat(4000), '_'.repeat(8000), '[a'.repeat(4000), '**a *b _c ~~d `'.repeat(500), '>'.repeat(8000), '*a*_b_'.repeat(1300),
  ];
  for (const input of inputs) {
    const start = performance.now();
    const parsed = parse(input);
    assert.ok(isAllowedDoc(parsed));
    assert.deepEqual(parse(toMarkup(parsed)), parsed);
    assert.ok(performance.now() - start < 1000, `${input.slice(0, 12)}… took too long`);
  }
});

// The rows of MarkupJiraLengthTimingTests: Jira text is capped at 32,000 characters and parsed on the request
// path, so each hostile body must parse in under 200 ms. The fastest of three runs absorbs a GC pause.
const JIRA_TEXT_LENGTH = 32000;
const TIMING_MENTION = '[[mention:00112233-4455-6677-8899-aabbccddeeff:Ada]]';
const repeatTo = (piece, length) => piece.repeat(Math.floor(length / piece.length) + 1).slice(0, length);
const nestedTo = (opener, closer, length) => {
  const half = repeatTo(opener, Math.floor(length / 2));
  return half + repeatTo(closer, length - half.length);
};
const JIRA_LENGTH_BODIES = [
  nestedTo('**_~~*', '*~~_**', JIRA_TEXT_LENGTH),
  nestedTo('**_~~*a ', ' a*~~_**', JIRA_TEXT_LENGTH),
  repeatTo('**_~~*', JIRA_TEXT_LENGTH),
  repeatTo('[a](https://example.com/', JIRA_TEXT_LENGTH),
  repeatTo('[a](', JIRA_TEXT_LENGTH),
  repeatTo('[a]', JIRA_TEXT_LENGTH),
  repeatTo('[', JIRA_TEXT_LENGTH),
  repeatTo('[**a', JIRA_TEXT_LENGTH),
  `[${repeatTo('a ', JIRA_TEXT_LENGTH - 1)}`,
  repeatTo(TIMING_MENTION, JIRA_TEXT_LENGTH),
  repeatTo(TIMING_MENTION.slice(0, -2), JIRA_TEXT_LENGTH),
  repeatTo('[[mention:', JIRA_TEXT_LENGTH),
  repeatTo(`*${TIMING_MENTION}`, JIRA_TEXT_LENGTH),
];

for (const dialect of DIALECTS) {
  for (const body of JIRA_LENGTH_BODIES) {
    test(`Jira-length body ${JSON.stringify(body.slice(0, 16))}… parses within 200 ms (${dialect})`, () => {
      assert.equal(body.length, JIRA_TEXT_LENGTH);
      parse('**warm** [up](https://example.com)', { dialect });
      let fastest = Infinity;
      for (let run = 0; run < 3; run++) {
        const start = performance.now();
        parse(body, { dialect });
        fastest = Math.min(fastest, performance.now() - start);
      }
      assert.ok(fastest < 200, `took ${fastest.toFixed(1)} ms`);
    });
  }
}
