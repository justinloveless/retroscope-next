// A minimal Retroscope client for the drills against deploy/compose.test.yml: HTTPS requests and a raw WebSocket over
// TLS (so close codes, PING and timing are all visible), speaking the wire protocol in docs/protocol.md, and psql in
// the stack's db container. The stack's certificate is self-signed, so verification is off; never point this at
// anything else.
import { execFileSync } from 'node:child_process';
import crypto from 'node:crypto';
import https from 'node:https';
import tls from 'node:tls';

export const BASE = new URL(process.env.RS_E2E_BASE_URL ?? `https://localhost:${process.env.RS_TEST_HTTPS_PORT ?? 18443}`);
const ORIGIN = BASE.origin;
const PASSWORD = 'correct horse battery staple';

export const clientIp = () => `10.${crypto.randomInt(256)}.${crypto.randomInt(256)}.${crypto.randomInt(1, 255)}`;

const COMPOSE = ['compose', '-f', new URL('../compose.test.yml', import.meta.url).pathname];

/** One statement through psql in compose.test.yml's db container, as the owner; returns the output's lines. */
export function psql(sql) {
  return execFileSync('docker', [...COMPOSE, 'exec', '-T', 'db', 'psql', '-U', 'retroscope_owner', '-d', 'retroscope', '-Atc', sql])
    .toString().trim().split('\n').filter(Boolean);
}

export function request(method, path, { body, cookie, ip } = {}) {
  const payload = Buffer.from(body === undefined ? '' : JSON.stringify(body));
  const headers = { 'X-RS': '1', Origin: ORIGIN, 'X-Test-Client-Ip': ip ?? clientIp() };
  if (body !== undefined) Object.assign(headers, { 'Content-Type': 'application/json', 'Content-Length': payload.length });
  if (cookie) headers.Cookie = cookie;
  return new Promise((resolve, reject) => {
    const req = https.request(new URL(path, BASE), { method, headers, rejectUnauthorized: false }, (res) => {
      const chunks = [];
      res.on('data', (c) => chunks.push(c));
      res.on('end', () => resolve({ status: res.statusCode, headers: res.headers, text: Buffer.concat(chunks).toString() }));
    });
    req.on('error', reject);
    req.end(payload);
  });
}

// The bcrypt gate answers 503 busy with Retry-After when hashes queue up (§e); a browser would try again.
async function gated(send) {
  for (let attempt = 0; ; attempt++) {
    const response = await send();
    if (response.status !== 503 || attempt === 10) return response;
    await sleep(Number(response.headers['retry-after'] ?? 2) * 1000);
  }
}

/** Signs a new user up and in; returns the session cookie and the client address it uses throughout. */
export async function user(name) {
  const ip = clientIp();
  const email = `drill-${crypto.randomUUID()}@example.test`;
  const signup = await gated(() => request('POST', '/api/auth/signup', { body: { email, password: PASSWORD, fullName: name }, ip }));
  if (signup.status !== 202) throw new Error(`signup ${signup.status} ${signup.text}`);
  const login = await gated(() => request('POST', '/api/auth/login', { body: { email, password: PASSWORD }, ip }));
  if (login.status !== 200) throw new Error(`login ${login.status} ${login.text}`);
  const cookie = (login.headers['set-cookie'] ?? []).map((c) => c.split(';')[0]).find((c) => c.startsWith('__Host-rs_sid='));
  return { name, email, cookie, ip };
}

function frame(opcode, payload) {
  const mask = crypto.randomBytes(4);
  const length = payload.length;
  const header = length < 126 ? Buffer.from([0x80 | opcode, 0x80 | length])
    : Buffer.from([0x80 | opcode, 0x80 | 126, length >> 8, length & 0xff]);
  const masked = Buffer.from(payload.map((byte, i) => byte ^ mask[i % 4]));
  return Buffer.concat([header, mask, masked]);
}

/** One WebSocket. Text frames arrive as parsed JSON through onFrame; PINGs are answered; onClose gets the code. */
export class Socket {
  static open({ cookie, ip }, { onFrame, onClose }) {
    const key = crypto.randomBytes(16).toString('base64');
    const expected = crypto.createHash('sha1').update(`${key}258EAFA5-E914-47DA-95CA-C5AB0DC85B11`).digest('base64');
    return new Promise((resolve, reject) => {
      const socket = tls.connect({ host: BASE.hostname, port: Number(BASE.port), servername: BASE.hostname, rejectUnauthorized: false, ALPNProtocols: ['http/1.1'] }, () => {
        socket.write([
          'GET /ws HTTP/1.1', `Host: ${BASE.host}`, 'Upgrade: websocket', 'Connection: Upgrade',
          `Sec-WebSocket-Key: ${key}`, 'Sec-WebSocket-Version: 13', `Origin: ${ORIGIN}`, `Cookie: ${cookie}`,
          `X-Test-Client-Ip: ${ip}`, '', '',
        ].join('\r\n'));
      });
      let head = Buffer.alloc(0);
      const onData = (data) => {
        head = Buffer.concat([head, data]);
        const end = head.indexOf('\r\n\r\n');
        if (end < 0) return;
        socket.off('data', onData);
        const lines = head.subarray(0, end).toString().split('\r\n');
        const accept = lines.find((l) => l.toLowerCase().startsWith('sec-websocket-accept:'));
        if (!lines[0].startsWith('HTTP/1.1 101') || accept?.split(':')[1].trim() !== expected) {
          socket.destroy();
          reject(new Error(`upgrade refused: ${lines[0]}`));
          return;
        }
        resolve(new Socket(socket, head.subarray(end + 4), onFrame, onClose));
      };
      socket.on('data', onData);
      socket.on('error', reject);
    });
  }

  constructor(socket, rest, onFrame, onClose) {
    this.socket = socket;
    this.buffer = rest;
    this.closed = false;
    this.onFrame = onFrame;
    this.onClose = onClose;
    socket.on('data', (data) => { this.buffer = Buffer.concat([this.buffer, data]); this.pump(); });
    socket.on('error', () => {});
    socket.on('close', () => this.finish(1006));
    this.pump();
  }

  pump() {
    while (this.buffer.length >= 2) {
      let length = this.buffer[1] & 0x7f;
      let offset = 2;
      if (length === 126) { if (this.buffer.length < 4) return; length = this.buffer.readUInt16BE(2); offset = 4; }
      if (length === 127) { if (this.buffer.length < 10) return; length = Number(this.buffer.readBigUInt64BE(2)); offset = 10; }
      if (this.buffer.length < offset + length) return;
      const opcode = this.buffer[0] & 0x0f;
      const payload = this.buffer.subarray(offset, offset + length);
      this.buffer = this.buffer.subarray(offset + length);
      if (opcode === 0x1) this.onFrame(JSON.parse(payload));
      else if (opcode === 0x9) this.write(0xa, payload);
      else if (opcode === 0x8) this.finish(payload.length >= 2 ? payload.readUInt16BE(0) : 1005);
    }
  }

  finish(code) {
    if (this.closed) return;
    this.closed = true;
    this.socket.destroy();
    this.onClose(code);
  }

  write(opcode, payload) {
    if (!this.closed) this.socket.write(frame(opcode, Buffer.from(payload)));
  }

  send(message) { this.write(0x1, JSON.stringify(message)); }
}

export const sleep = (ms) => new Promise((done) => setTimeout(done, ms));

export function percentile(values, p) {
  if (values.length === 0) return NaN;
  const sorted = [...values].sort((a, b) => a - b);
  return sorted[Math.min(sorted.length - 1, Math.ceil((p / 100) * sorted.length) - 1)];
}

/** `--name value` pairs into an object. */
export function parseArgs(argv) {
  const args = {};
  for (let i = 0; i < argv.length; i += 2) args[argv[i].replace(/^--/, '')] = argv[i + 1];
  return args;
}
