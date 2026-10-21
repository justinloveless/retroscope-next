import { test } from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, readFileSync, readdirSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { checkMarkup, parse } from '../../web/src/markup/parse.js';
import { render } from '../../web/src/markup/render.js';
import {
  DIALECT_JIRA,
  DIALECT_JIRA_DESCRIPTION,
  DIALECT_RETRO,
  imageBlobIds,
  isAllowedDoc,
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

class FakeElement extends FakeNode {
  constructor(tag) {
    super('element');
    this.tagName = tag;
    this.attributes = new Map();
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
  const attrs = [...node.attributes].map(([k, v]) => ` ${k}="${v.replace(/"/g, '&quot;')}"`).join('');
  return node.tagName === 'br' || node.tagName === 'hr' || node.tagName === 'img' || node.tagName === 'input'
    ? `<${node.tagName}${attrs}>`
    : `<${node.tagName}${attrs}>${inner}</${node.tagName}>`;
}

function renderHtml(doc, options = {}) {
  return html(render(doc, { document: fakeDocument, ...options }));
}

const ALLOWED_ELEMENTS = {
  p: [], br: [], strong: [], em: [], s: [], u: [], code: ['data-lang'], pre: [], ul: ['class'], ol: ['start'],
  li: ['class'], blockquote: [], hr: [], h1: [], h2: [], h3: [], h4: [], h5: [], h6: [],
  a: ['href', 'rel', 'target'], img: ['src', 'alt', 'loading'], span: ['class', 'data-mention-id'],
  div: ['class', 'data-island-id'], table: [], tbody: [], tr: [], th: ['colspan', 'rowspan'], td: ['colspan', 'rowspan'],
  details: ['class'], summary: [], input: ['type', 'disabled', 'checked'],
};

function assertSafeDom(node) {
  if (node.kind === 'element') {
    assert.ok(Object.hasOwn(ALLOWED_ELEMENTS, node.tagName), `element <${node.tagName}>`);
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
    renderHtml(jira, { imageSrc }),
    '<h2>Title</h2><hr><div class="panel panel-warning"><p>careful</p></div>'
      + '<table><tbody><tr><th><p>h</p></th><td><p>c</p></td></tr></tbody></table>'
      + '<details class="expand"><summary>More</summary><p>hidden</p></details>'
      + '<ul class="tasks"><li class="task"><input type="checkbox" disabled="" checked="">done</li>'
      + '<li class="task"><input type="checkbox" disabled="">open</li></ul>'
      + '<p><u>u</u><span class="status status-blue">IN PROGRESS</span>'
      + '<img src="/api/teams/t/jira/issues/K-1/attachments/10042" alt="shot.png" loading="lazy"></p>',
  );
  assert.equal(
    renderHtml(doc(p({ t: 'attachment', id: '1', alt: '' }))),
    '<p><span class="image-missing">image (open in Jira)</span></p>',
  );
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
  for (const file of ['parse.js', 'render.js', 'doc.js']) {
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
