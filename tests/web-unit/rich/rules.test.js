import { test } from 'node:test';
import assert from 'node:assert/strict';
import { isAllowedHref } from '../../../web/src/markup/doc.js';
import { compileRule, contextMarks, isHref } from '../../../web/src/rich/rules.js';

const C1 = /[\u0080-\u009f]/;

test('the editor link rule is the read view rule plus the control check', () => {
  const corpus = [
    'https://e.example', 'http://e.example/a?b=c#d', 'mailto:a@example.com', 'HTTPS://E.EXAMPLE', 'MailTo:x',
    'https://', 'http:/x', 'mailto:', '', 'javascript:alert(1)', 'data:text/html,x', 'ftp://e.example', '//e.example',
    'https://e.example/a b', 'https://e.example/(x)', 'https://e.example/<x>', 'https://e.example/\u007f',
    'https://e.example/\u0085', 'https://e.example/\t', 'https://e.example/​', 'https://e.example/\u{1F600}',
    'https://e.example/\ud800', ` https://e.example`, `https://e.example/${'a'.repeat(2048)}`,
    `https://${'a'.repeat(2040)}`, `https://${'a'.repeat(2041)}`,
  ];
  for (let i = 0; i < 0x100; i++) {
    corpus.push(`https://e.example/${String.fromCharCode(i)}`);
  }
  for (const href of corpus) {
    assert.equal(isHref(href), isAllowedHref(href) && !C1.test(href), JSON.stringify(href));
  }
  assert.equal(isHref(null), false);
  assert.equal(isHref('mailto:a@example.com', ['http', 'https']), false);
});

test('string lengths count code points and every string refuses control characters', () => {
  const max2 = compileRule({ type: 'string', max: 2 });
  assert.equal(max2('\u{1F600}\u{1F600}'), true, 'two astral characters are two code points');
  assert.equal(max2('abc'), false);
  const min1 = compileRule({ type: 'string', min: 1 });
  assert.equal(min1(''), false);
  for (const c of ['\u0000', '\t', '\n', '\u001f', '\u007f', '\u0080', '\u009f']) {
    assert.equal(compileRule({ type: 'string', max: 10 })(`a${c}`), false, JSON.stringify(c));
  }
  assert.equal(compileRule({ type: 'string', max: 10 })('a b'), true);
});

test('block marks follow the parent context', () => {
  assert.deepEqual(contextMarks('doc', 'paragraph'), ['alignment', 'indentation']);
  assert.deepEqual(contextMarks('layoutColumn', 'heading'), ['alignment', 'indentation']);
  assert.deepEqual(contextMarks('tableCell', 'paragraph'), ['alignment']);
  assert.deepEqual(contextMarks('tableHeader', 'heading'), ['alignment', 'indentation']);
  for (const parent of ['listItem', 'panel', 'blockquote', 'expand', 'nestedExpand', 'taskList', '__proto__', 'constructor']) {
    assert.deepEqual(contextMarks(parent, 'paragraph'), [], parent);
  }
  assert.deepEqual(contextMarks('doc', 'codeBlock'), []);
  assert.deepEqual(contextMarks(null, 'paragraph'), ['alignment', 'indentation'], 'a pasted fragment is settled by the guard');
});
