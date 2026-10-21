// Small helpers shared by the demo scenario: stable ids, a seeded random source, cookies and masking.
import { uuidv7 } from '../../../web/src/poker/model.js';

// Fixed ids read as demo data in logs ("0d3e0000-…") and stay valid lower-case UUIDs.
export function demoId(group, n) {
  return `0d3e0000-${group}-7000-8000-${String(n).padStart(12, '0')}`;
}

export const newId = () => uuidv7();

// Who the browser is in the demo: a person's key, or SIGNED_OUT (then the guest cookie, if any).
export const AS_COOKIE = 'rs_demo_as';
export const GUEST_COOKIE = 'rs_demo_guest';
export const SIGNED_OUT = 'signed-out';

// mulberry32: small, fast and good enough to make the peers look human.
export function seededRandom(seed) {
  let a = seed >>> 0;
  const next = () => {
    a = (a + 0x6d2b79f5) >>> 0;
    let t = a;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
  next.pick = (list) => list[Math.floor(next() * list.length)];
  next.chance = (p) => next() < p;
  next.between = (low, high) => low + Math.floor(next() * (high - low + 1));
  return next;
}

export function parseCookies(header = '') {
  const cookies = {};
  for (const part of header.split(';')) {
    const at = part.indexOf('=');
    if (at > 0) {
      cookies[part.slice(0, at).trim()] = decodeURIComponent(part.slice(at + 1).trim());
    }
  }
  return cookies;
}

export function cookie(name, value, { maxAge } = {}) {
  const parts = [`${name}=${encodeURIComponent(value)}`, 'Path=/', 'SameSite=Lax', 'HttpOnly'];
  if (maxAge !== undefined) {
    parts.push(`Max-Age=${maxAge}`);
  }
  return parts.join('; ');
}

export function maskEmail(email) {
  const [local, domain] = email.split('@');
  return `${local[0]}•••@${domain}`;
}

/** Drops undefined and null members, as the wire omits empty optional members. */
export function compact(object) {
  return Object.fromEntries(Object.entries(object).filter(([, value]) => value !== undefined && value !== null));
}

export const byPid = (a, b) => (a.pid < b.pid ? -1 : a.pid > b.pid ? 1 : 0);

export const MINUTE = 60_000;
export const HOUR = 60 * MINUTE;
export const DAY = 24 * HOUR;
