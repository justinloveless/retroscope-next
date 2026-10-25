// The demo's cast: "Demo User" and five teammates in Demo Team, two more in Platform Guild, the eleven
// others of Product All-Hands, and pixel-art avatars served as SVG from /blob/{id}. Everyone is a signed-in
// member of a team: there are no guests (UX18).
import { demoId } from './util.js';

const person = (n, fields) => ({
  pid: demoId('0002', n),
  avatar: demoId('00a0', n),
  kind: 'user',
  theme: 'system',
  emailVerified: true,
  isAdmin: false,
  ...fields,
});

export const DEMO = person(1, { key: 'demo', name: 'Demo User', nickname: 'Demo', email: 'demo@example.test', hue: 248, isAdmin: true });
export const PRIYA = person(2, { key: 'priya', name: 'Priya Raman', nickname: 'Priya', email: 'priya.raman@example.test', hue: 330 });
export const MARCUS = person(3, { key: 'marcus', name: 'Marcus Chen', nickname: 'Marcus', email: 'marcus.chen@example.test', hue: 200 });
export const SOFIA = person(4, { key: 'sofia', name: 'Sofia Alvarez', nickname: 'Sofia', email: 'sofia.alvarez@example.test', hue: 22 });
export const JONAS = person(5, { key: 'jonas', name: 'Jonas Weber', nickname: 'Jonas', email: 'jonas.weber@example.test', hue: 140 });
export const AISHA = person(6, { key: 'aisha', name: 'Aisha Okafor', nickname: 'Aisha', email: 'aisha.okafor@example.test', hue: 44 });
export const TOM = person(7, { key: 'tom', name: 'Tom Becker', nickname: 'Tom', email: 'tom.becker@example.test', hue: 270, emailVerified: false });
export const LENA = person(8, { key: 'lena', name: 'Lena Novak', nickname: 'Lena', email: 'lena.novak@example.test', hue: 175 });

// Product All-Hands: eleven more members around one table, so twelve players with Demo User. They have no
// avatar, so their seats show initials.
export const CROWD = ['Noor', 'Mateo', 'Hana', 'Felix', 'Ines', 'Kofi', 'Yara', 'Oskar', 'Mei', 'Ravi', 'Zoe']
  .map((name, i) => ({
    ...person(11 + i, { key: name.toLowerCase(), name, nickname: name, email: `${name.toLowerCase()}@example.test`, hue: (i * 33) % 360 }),
    avatar: undefined,
  }));

export const PEOPLE = [DEMO, PRIYA, MARCUS, SOFIA, JONAS, AISHA, TOM, LENA, ...CROWD];

export function personByKey(key) {
  const wanted = String(key ?? '').trim().toLowerCase();
  if (wanted === '') {
    return null;
  }
  return PEOPLE.find((p) => p.key === wanted || p.name.toLowerCase() === wanted || p.email === wanted)
    ?? PEOPLE.find((p) => p.name.toLowerCase().startsWith(wanted))
    ?? null;
}

export const personByPid = (pid) => PEOPLE.find((p) => p.pid === pid) ?? null;

/** PrincipalInfo as rooms list it. */
export function principalInfo(who) {
  return who.avatar ? { pid: who.pid, kind: who.kind, name: who.name, avatar: who.avatar } : { pid: who.pid, kind: who.kind, name: who.name };
}

/** AuthorInfo on cards and comments. */
export function authorInfo(who) {
  const author = { pid: who.pid, name: who.name, guest: who.kind === 'guest' };
  return who.avatar ? { ...author, avatar: who.avatar } : author;
}

// An 8×8 mirrored identicon on a tinted ground, crisp at any size.
export function avatarSvg(seed, hue) {
  let bits = 0;
  for (const ch of seed) {
    bits = (Math.imul(bits, 31) + ch.charCodeAt(0)) >>> 0;
  }
  const cells = [];
  for (let y = 1; y < 7; y++) {
    for (let x = 1; x < 4; x++) {
      bits = (Math.imul(bits, 1103515245) + 12345) >>> 0;
      if ((bits >>> 16) & 1) {
        cells.push(`<rect x="${x}" y="${y}" width="1" height="1"/>`, `<rect x="${7 - x}" y="${y}" width="1" height="1"/>`);
      }
    }
  }
  return [
    '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 8 8" width="128" height="128" shape-rendering="crispEdges">',
    `<rect width="8" height="8" fill="hsl(${hue} 70% 88%)"/>`,
    `<g fill="hsl(${hue} 55% 42%)">${cells.join('')}</g>`,
    '</svg>',
  ].join('');
}

// A hand-drawn-looking burndown chart for the card that shows an image.
export function burndownSvg() {
  const ideal = 'M10 20 L190 110';
  const actual = 'M10 20 L40 26 L70 24 L100 58 L130 64 L160 92 L190 104';
  return [
    '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 200 130" width="400" height="260">',
    '<rect width="200" height="130" rx="6" fill="#fffdf6" stroke="#d6d0bd"/>',
    '<g stroke="#ebe5d3">',
    ...[20, 50, 80, 110].map((y) => `<line x1="10" x2="190" y1="${y}" y2="${y}"/>`),
    '</g>',
    `<path d="${ideal}" stroke="#9aa5b1" stroke-dasharray="4 3" fill="none" stroke-width="1.5"/>`,
    `<path d="${actual}" stroke="#e05d44" fill="none" stroke-width="2.5" stroke-linejoin="round"/>`,
    '<text x="12" y="125" font-family="sans-serif" font-size="9" fill="#6b6455">Sprint 42 burndown · story points</text>',
    '</svg>',
  ].join('');
}
