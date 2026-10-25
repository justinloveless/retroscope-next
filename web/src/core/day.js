// A day as lists and breadcrumbs name it. Kept out of fmt.js, which the entry chunk carries, because only the
// poker list and the poker screen use it.

import { formatDate } from './fmt.js';

const DAY = 24 * 60 * 60_000;
const relative = new Intl.RelativeTimeFormat(undefined, { numeric: 'auto' });
const dayFormat = new Intl.DateTimeFormat(undefined, { month: 'short', day: 'numeric' });

const startOfDay = (ms) => {
  const date = new Date(ms);
  return new Date(date.getFullYear(), date.getMonth(), date.getDate()).getTime();
};

/** "Today, Oct 25", "Yesterday, Oct 24", "Oct 15", or "Oct 15, 2025" in another year. */
export function formatDay(ms, now = Date.now()) {
  const days = Math.round((startOfDay(now) - startOfDay(ms)) / DAY);
  const day = new Date(ms).getFullYear() === new Date(now).getFullYear() ? dayFormat.format(ms) : formatDate(ms);
  if (days !== 0 && days !== 1) {
    return day;
  }
  const word = relative.format(-days, 'day');
  return `${word.charAt(0).toUpperCase()}${word.slice(1)}, ${day}`;
}
