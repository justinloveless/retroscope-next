// A minimal RFC 6455 WebSocket codec on top of node:http upgrades, for both ends: the fake server
// accepts browser sockets with it, and self.test.js drives the server with its client half. Text and
// close frames are all the protocol uses; binary frames are refused like the real server does.
import { createHash, randomBytes } from 'node:crypto';
import { EventEmitter } from 'node:events';
import { request } from 'node:http';

const GUID = '258EAFA5-E914-47DA-95CA-C5AB0DC85B11';
const OP = { continuation: 0, text: 1, binary: 2, close: 8, ping: 9, pong: 10 };
const CLOSE_TIMEOUT_MS = 1000;

export function acceptKey(key) {
  return createHash('sha1').update(key + GUID).digest('base64');
}

export function encodeFrame(opcode, payload, { mask = false, fin = true } = {}) {
  const length = payload.length;
  const lengthBytes = length < 126 ? 0 : length < 65536 ? 2 : 8;
  const header = Buffer.alloc(2 + lengthBytes + (mask ? 4 : 0));
  header[0] = (fin ? 0x80 : 0) | opcode;
  if (lengthBytes === 0) {
    header[1] = length;
  } else if (lengthBytes === 2) {
    header[1] = 126;
    header.writeUInt16BE(length, 2);
  } else {
    header[1] = 127;
    header.writeBigUInt64BE(BigInt(length), 2);
  }
  if (!mask) {
    return Buffer.concat([header, payload]);
  }
  header[1] |= 0x80;
  const key = randomBytes(4);
  key.copy(header, 2 + lengthBytes);
  const masked = Buffer.from(payload);
  for (let i = 0; i < masked.length; i++) {
    masked[i] ^= key[i & 3];
  }
  return Buffer.concat([header, masked]);
}

function closePayload(code, reason = '') {
  if (code === undefined) {
    return Buffer.alloc(0);
  }
  const text = Buffer.from(reason, 'utf8');
  const payload = Buffer.alloc(2 + text.length);
  payload.writeUInt16BE(code, 0);
  text.copy(payload, 2);
  return payload;
}

export class WsConnection extends EventEmitter {
  constructor(socket, { role, maxMessageBytes = Infinity, head } = {}) {
    super();
    this.socket = socket;
    this.role = role;
    this.maxMessageBytes = maxMessageBytes;
    this.buffer = Buffer.alloc(0);
    this.fragments = null;
    this.closeSent = false;
    this.closed = false;
    this.closeCode = undefined;
    this.failed = false;
    socket.setNoDelay(true);
    socket.on('data', (chunk) => this.onData(chunk));
    socket.on('close', () => this.finish(this.closeCode ?? 1006, ''));
    socket.on('error', () => socket.destroy());
    // Bytes that arrived with the handshake (a close sent right after accepting, say) are processed
    // once the caller has had a chance to attach its listeners.
    if (head && head.length > 0) {
      setImmediate(() => this.onData(head));
    }
  }

  get open() {
    return !this.closeSent && !this.closed;
  }

  send(text) {
    if (!this.open) {
      return false;
    }
    this.socket.write(encodeFrame(OP.text, Buffer.from(text, 'utf8'), { mask: this.role === 'client' }));
    return true;
  }

  sendFrame(opcode, payload, options = {}) {
    this.socket.write(encodeFrame(opcode, payload, { mask: this.role === 'client', ...options }));
  }

  close(code = 1000, reason = '') {
    if (this.closeSent || this.closed) {
      return;
    }
    this.closeSent = true;
    this.closeCode ??= code;
    this.socket.write(encodeFrame(OP.close, closePayload(code, reason), { mask: this.role === 'client' }));
    setTimeout(() => this.socket.destroy(), CLOSE_TIMEOUT_MS).unref();
  }

  terminate() {
    this.socket.destroy();
  }

  finish(code, reason) {
    if (this.closed) {
      return;
    }
    this.closed = true;
    this.emit('close', code, reason);
  }

  // A protocol error: close and stop parsing (the rest of the stream may be the tail of a refused frame).
  fail(code, reason) {
    this.close(code, reason);
    this.failed = true;
    this.buffer = Buffer.alloc(0);
  }

  onData(chunk) {
    if (this.failed) {
      return;
    }
    this.buffer = this.buffer.length === 0 ? chunk : Buffer.concat([this.buffer, chunk]);
    while (!this.closed && !this.failed) {
      const frame = this.readFrame();
      if (!frame) {
        return;
      }
      this.onFrame(frame);
    }
  }

  readFrame() {
    const b = this.buffer;
    if (b.length < 2) {
      return null;
    }
    const fin = (b[0] & 0x80) !== 0;
    const opcode = b[0] & 0x0f;
    const masked = (b[1] & 0x80) !== 0;
    let length = b[1] & 0x7f;
    let offset = 2;
    if (length === 126) {
      if (b.length < 4) {
        return null;
      }
      length = b.readUInt16BE(2);
      offset = 4;
    } else if (length === 127) {
      if (b.length < 10) {
        return null;
      }
      const big = b.readBigUInt64BE(2);
      length = big > BigInt(Number.MAX_SAFE_INTEGER) ? Infinity : Number(big);
      offset = 10;
    }
    if (length > this.maxMessageBytes) {
      this.fail(4413, 'frame too large');
      return null;
    }
    const maskOffset = offset;
    if (masked) {
      offset += 4;
    }
    if (b.length < offset + length) {
      return null;
    }
    const payload = Buffer.from(b.subarray(offset, offset + length));
    if (masked) {
      for (let i = 0; i < payload.length; i++) {
        payload[i] ^= b[maskOffset + (i & 3)];
      }
    }
    this.buffer = b.subarray(offset + length);
    return { fin, opcode, masked, payload };
  }

  onFrame({ fin, opcode, masked, payload }) {
    if (masked !== (this.role === 'server')) {
      this.fail(1002, this.role === 'server' ? 'client frames must be masked' : 'server frames must not be masked');
      return;
    }
    switch (opcode) {
      case OP.text:
      case OP.continuation: {
        if (this.closeSent) {
          return;
        }
        if ((opcode === OP.text) === (this.fragments !== null)) {
          this.fail(1002, 'unexpected continuation');
          return;
        }
        this.fragments = opcode === OP.text ? [payload] : [...this.fragments, payload];
        const size = this.fragments.reduce((n, p) => n + p.length, 0);
        if (size > this.maxMessageBytes) {
          this.fail(4413, 'frame too large');
          return;
        }
        if (fin) {
          const message = Buffer.concat(this.fragments);
          this.fragments = null;
          let text;
          try {
            text = new TextDecoder('utf-8', { fatal: true }).decode(message);
          } catch {
            this.fail(1007, 'invalid UTF-8');
            return;
          }
          this.emit('message', text);
        }
        return;
      }
      case OP.binary:
        this.fail(1003, 'binary frames are not supported');
        return;
      case OP.ping:
        this.sendFrame(OP.pong, payload);
        this.emit('ping', payload);
        return;
      case OP.pong:
        this.emit('pong', payload);
        return;
      case OP.close: {
        const code = payload.length >= 2 ? payload.readUInt16BE(0) : 1005;
        const reason = payload.length > 2 ? payload.subarray(2).toString('utf8') : '';
        this.closeCode ??= code;
        if (!this.closeSent) {
          this.closeSent = true;
          this.socket.write(encodeFrame(OP.close, payload.subarray(0, 2), { mask: this.role === 'client' }));
        }
        this.socket.end();
        this.finish(code, reason);
        return;
      }
      default:
        this.fail(1002, `unknown opcode ${opcode}`);
    }
  }
}

// Completes the server half of the opening handshake; returns null (and answers 400) for a request
// that is not a WebSocket upgrade.
export function acceptUpgrade(req, socket, head, options) {
  const key = req.headers['sec-websocket-key'];
  const isUpgrade = (req.headers.upgrade ?? '').toLowerCase() === 'websocket';
  if (!isUpgrade || !key || req.headers['sec-websocket-version'] !== '13') {
    socket.end('HTTP/1.1 400 Bad Request\r\nConnection: close\r\n\r\n');
    return null;
  }
  socket.write([
    'HTTP/1.1 101 Switching Protocols',
    'Upgrade: websocket',
    'Connection: Upgrade',
    `Sec-WebSocket-Accept: ${acceptKey(key)}`,
    '',
    '',
  ].join('\r\n'));
  return new WsConnection(socket, { role: 'server', head, ...options });
}

// Client half, for tests: resolves once the handshake is accepted.
export function connectWebSocket(url, { origin, headers = {} } = {}) {
  const target = new URL(url);
  const key = randomBytes(16).toString('base64');
  return new Promise((resolve, reject) => {
    const req = request({
      host: target.hostname,
      port: target.port,
      path: target.pathname + target.search,
      headers: {
        Connection: 'Upgrade',
        Upgrade: 'websocket',
        'Sec-WebSocket-Key': key,
        'Sec-WebSocket-Version': '13',
        ...(origin ? { Origin: origin } : {}),
        ...headers,
      },
    });
    req.on('upgrade', (res, socket, head) => {
      if (res.headers['sec-websocket-accept'] !== acceptKey(key)) {
        socket.destroy();
        reject(new Error('bad Sec-WebSocket-Accept'));
        return;
      }
      resolve(new WsConnection(socket, { role: 'client', head }));
    });
    req.on('response', (res) => reject(new Error(`upgrade refused with HTTP ${res.statusCode}`)));
    req.on('error', reject);
    req.end();
  });
}

export { OP };
