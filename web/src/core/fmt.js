// Display formatting. Locale comes from the browser; under the server's InvariantGlobalization the
// server never formats for display.

const MINUTE = 60_000;
const HOUR = 60 * MINUTE;
const DAY = 24 * HOUR;

const relative = new Intl.RelativeTimeFormat(undefined, { numeric: 'auto' });
const dateFormat = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium' });
const dateTimeFormat = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' });

export function formatDate(ms) {
  return dateFormat.format(ms);
}

export function formatDateTime(ms) {
  return dateTimeFormat.format(ms);
}

export function timeAgo(ms, now = Date.now()) {
  const diff = ms - now;
  const abs = Math.abs(diff);
  if (abs < MINUTE) {
    return relative.format(0, 'second');
  }
  if (abs < HOUR) {
    return relative.format(Math.round(diff / MINUTE), 'minute');
  }
  if (abs < DAY) {
    return relative.format(Math.round(diff / HOUR), 'hour');
  }
  if (abs < 30 * DAY) {
    return relative.format(Math.round(diff / DAY), 'day');
  }
  return formatDate(ms);
}

export function plural(count, one, other = `${one}s`) {
  return `${count} ${count === 1 ? one : other}`;
}

export function initials(name) {
  const words = String(name ?? '').trim().split(/\s+/).filter(Boolean);
  if (words.length === 0) {
    return '?';
  }
  const first = [...words[0]][0];
  const last = words.length > 1 ? [...words[words.length - 1]][0] : '';
  return (first + last).toUpperCase();
}

export function duration(ms) {
  const total = Math.max(0, Math.ceil(ms / 1000));
  const minutes = Math.floor(total / 60);
  const seconds = total % 60;
  return `${minutes}:${String(seconds).padStart(2, '0')}`;
}
