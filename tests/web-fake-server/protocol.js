// Frame validation against the generated catalog (web/src/net/protocol.gen.js, U2). Envelope members
// follow docs/protocol.md "Frames"; kinds, payload members, required command members, nack retry
// flags and kick/bye codes come from protocol.gen.js itself, so a scripted or replayed frame the real
// server could never send, or a client frame the real server would refuse, is caught here.
import {
  BYE_CODES,
  COMMANDS,
  ENUMS,
  EVENTS,
  KICK_CODES,
  NACK,
  PROTOCOL_VERSION,
  UNSEQUENCED,
} from '../../web/src/net/protocol.gen.js';

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
const EPOCH = /^[0-9a-f]{16}$/;
const ROOM = /^(poker|board|team):([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$/;

const isObject = (v) => typeof v === 'object' && v !== null && !Array.isArray(v);
const isCount = (v) => Number.isSafeInteger(v) && v >= 0;
const isUuid = (v) => typeof v === 'string' && UUID.test(v);
const isEpoch = (v) => typeof v === 'string' && EPOCH.test(v);
const isRoom = (v) => typeof v === 'string' && ROOM.test(v);
const isString = (v) => typeof v === 'string';
const isBool = (v) => typeof v === 'boolean';
const isMs = (v) => Number.isSafeInteger(v);

// Member → validator for every frame type; a trailing '?' marks an optional member.
const CLIENT_FRAMES = {
  hello: { v: (v) => v === PROTOCOL_VERSION, cid: isUuid, subs: (v) => Array.isArray(v) && v.every(isSubEntry) },
  sub: { r: isRoom, 'e?': isEpoch, 's?': isCount },
  unsub: { r: isRoom },
  cmd: { r: isRoom, q: (v) => Number.isSafeInteger(v) && v > 0, k: isString, d: isObject },
  ping: { c: isMs },
};

const SERVER_FRAMES = {
  welcome: { conn: isUuid, me: isMe, n: isMs },
  snap: { r: isRoom, e: isEpoch, s: isCount, lastQ: isCount, d: isObject, 'o?': isObject },
  replay: { r: isRoom, e: isEpoch, from: isCount, lastQ: isCount, txs: (v) => Array.isArray(v) },
  tx: { r: isRoom, s: isCount, ev: (v) => Array.isArray(v) },
  ack: { r: isRoom, q: isCount, s: isCount, 'dup?': isBool },
  nack: { r: isRoom, q: isCount, code: (v) => Object.hasOwn(NACK, v), retry: isBool, 'msg?': isString, 'd?': isObject },
  x: { r: isRoom, k: isString, d: isObject },
  pong: { c: isMs, n: isMs },
  kick: { r: isRoom, code: (v) => KICK_CODES.includes(v) },
  bye: { code: (v) => BYE_CODES.includes(v), retryMs: isCount },
};

export const CLIENT_FRAME_TYPES = Object.freeze(Object.keys(CLIENT_FRAMES));
export const SERVER_FRAME_TYPES = Object.freeze(Object.keys(SERVER_FRAMES));

function isSubEntry(v) {
  return checkMembers(v, { r: isRoom, 'e?': isEpoch, 's?': isCount }, 'subs[]').length === 0;
}

function isMe(v) {
  const shape = { pid: isUuid, kind: (k) => ENUMS.principalKind.includes(k), name: isString, 'avatar?': isUuid };
  return checkMembers(v, shape, 'me').length === 0;
}

export function roomKind(room) {
  return ROOM.exec(room)?.[1] ?? null;
}

function checkMembers(value, shape, where) {
  if (!isObject(value)) {
    return [`${where} must be an object`];
  }
  const errors = [];
  const known = new Set();
  for (const [spec, valid] of Object.entries(shape)) {
    const optional = spec.endsWith('?');
    const name = optional ? spec.slice(0, -1) : spec;
    known.add(name);
    if (!Object.hasOwn(value, name) || value[name] === undefined) {
      if (!optional) {
        errors.push(`${where} is missing "${name}"`);
      }
    } else if (!valid(value[name])) {
      errors.push(`${where} has an invalid "${name}"`);
    }
  }
  for (const name of Object.keys(value)) {
    if (!known.has(name)) {
      errors.push(`${where} has unknown member "${name}"`);
    }
  }
  return errors;
}

function checkPayload(kind, d, fields, required, where) {
  if (!isObject(d)) {
    return [`${where} "${kind}" payload must be an object`];
  }
  const errors = [];
  for (const name of required) {
    if (!Object.hasOwn(d, name)) {
      errors.push(`${where} "${kind}" is missing "${name}"`);
    }
  }
  for (const name of Object.keys(d)) {
    if (!fields.includes(name)) {
      errors.push(`${where} "${kind}" has unknown member "${name}"`);
    }
  }
  return errors;
}

function checkEvent(ev, room, where) {
  if (!isObject(ev)) {
    return [`${where} must be an object`];
  }
  const errors = checkMembers(ev, { k: isString, d: isObject }, where);
  if (errors.length > 0) {
    return errors;
  }
  const spec = EVENTS[ev.k];
  if (!spec) {
    return [`${where} has unknown event kind "${ev.k}"`];
  }
  if (!spec.rooms.includes(room)) {
    return [`${where} event "${ev.k}" is not emitted by ${room} rooms`];
  }
  return checkPayload(ev.k, ev.d, spec.fields, [], where);
}

function checkTxList(txs, room, where) {
  const errors = [];
  txs.forEach((tx, i) => {
    const entry = `${where}[${i}]`;
    const shape = checkMembers(tx, { s: isCount, ev: (v) => Array.isArray(v) }, entry);
    errors.push(...shape);
    if (shape.length === 0) {
      tx.ev.forEach((ev, j) => errors.push(...checkEvent(ev, room, `${entry}.ev[${j}]`)));
    }
  });
  return errors;
}

// Returns the list of problems with a client → server frame (empty when valid). `envelopeOk` tells
// the caller whether the real server would answer with a nack `invalid` (envelope fine, kind or
// payload wrong) rather than close with 4400.
export function checkClientFrame(frame) {
  if (!isObject(frame) || !Object.hasOwn(CLIENT_FRAMES, frame.t)) {
    return { errors: [`unknown client frame type ${JSON.stringify(frame?.t)}`], envelopeOk: false };
  }
  const { t, ...members } = frame;
  const errors = checkMembers(members, CLIENT_FRAMES[t], t);
  if (errors.length > 0 || t !== 'cmd') {
    return { errors, envelopeOk: false };
  }
  const spec = COMMANDS[frame.k];
  if (!spec) {
    return { errors: [`cmd has unknown kind "${frame.k}"`], envelopeOk: true };
  }
  if (spec.room !== roomKind(frame.r)) {
    return { errors: [`cmd "${frame.k}" targets ${spec.room} rooms, not ${frame.r}`], envelopeOk: true };
  }
  return { errors: checkPayload(frame.k, frame.d, spec.fields, spec.required, 'cmd'), envelopeOk: true };
}

// Returns the list of problems with a server → client frame (empty when valid).
export function checkServerFrame(frame) {
  if (!isObject(frame) || !Object.hasOwn(SERVER_FRAMES, frame.t)) {
    return [`unknown server frame type ${JSON.stringify(frame?.t)}`];
  }
  const { t, ...members } = frame;
  const errors = checkMembers(members, SERVER_FRAMES[t], t);
  if (errors.length > 0) {
    return errors;
  }
  const room = frame.r === undefined ? null : roomKind(frame.r);
  switch (t) {
    case 'snap':
      if (frame.o !== undefined && room === 'team') {
        errors.push('snap for a team room carries no overlay');
      }
      break;
    case 'replay':
      errors.push(...checkTxList(frame.txs, room, 'replay.txs'));
      break;
    case 'tx':
      frame.ev.forEach((ev, i) => errors.push(...checkEvent(ev, room, `tx.ev[${i}]`)));
      break;
    case 'nack':
      if (frame.retry !== NACK[frame.code].retry) {
        errors.push(`nack "${frame.code}" must have retry:${NACK[frame.code].retry}`);
      }
      break;
    case 'x': {
      const spec = UNSEQUENCED[frame.k];
      if (!spec) {
        errors.push(`x has unknown kind "${frame.k}"`);
      } else if (!spec.rooms.includes(room)) {
        errors.push(`x "${frame.k}" is not sent to ${room} rooms`);
      } else {
        errors.push(...checkPayload(frame.k, frame.d, spec.fields, [], 'x'));
      }
      break;
    }
    default:
  }
  return errors;
}

export function assertServerFrame(frame) {
  const errors = checkServerFrame(frame);
  if (errors.length > 0) {
    throw new Error(`invalid server frame ${JSON.stringify(frame).slice(0, 200)}: ${errors.join('; ')}`);
  }
  return frame;
}
