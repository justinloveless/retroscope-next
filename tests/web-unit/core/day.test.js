import { test } from 'node:test';
import assert from 'node:assert/strict';
import { formatDay } from '../../../web/src/core/day.js';

// Local times, as the page shows them; the words and month names follow the runtime's locale.
const at = (y, m, d, h = 12) => new Date(y, m, d, h).getTime();
const rtf = new Intl.RelativeTimeFormat(undefined, { numeric: 'auto' });
const capital = (text) => text.charAt(0).toUpperCase() + text.slice(1);
const short = (ms) => new Intl.DateTimeFormat(undefined, { month: 'short', day: 'numeric' }).format(ms);
const medium = (ms) => new Intl.DateTimeFormat(undefined, { dateStyle: 'medium' }).format(ms);

test('today and yesterday are named, by calendar day rather than by 24 hours', () => {
  const now = at(2026, 9, 25, 9);
  assert.equal(formatDay(at(2026, 9, 25, 0), now), `${capital(rtf.format(0, 'day'))}, ${short(now)}`);
  assert.equal(formatDay(at(2026, 9, 24, 23), now), `${capital(rtf.format(-1, 'day'))}, ${short(at(2026, 9, 24))}`);
  assert.equal(formatDay(at(2026, 9, 23, 23), now), short(at(2026, 9, 23)));
});

test('an earlier day this year has no year, and one from another year has it', () => {
  const now = at(2026, 9, 25);
  assert.equal(formatDay(at(2026, 0, 2), now), short(at(2026, 0, 2)));
  assert.equal(formatDay(at(2025, 11, 31), now), medium(at(2025, 11, 31)));
  assert.notEqual(formatDay(at(2025, 11, 31), now), short(at(2025, 11, 31)));
});
