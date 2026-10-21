// A Node protocol client for node --test suites that talk to the fake server without a browser.
import { connectWebSocket } from './ws.js';

export class TestClient {
  static async connect(server, { origin = server.origin } = {}) {
    const ws = await connectWebSocket(server.wsUrl, { origin });
    return new TestClient(ws);
  }

  constructor(ws) {
    this.ws = ws;
    this.queue = [];
    this.waiters = [];
    this.closed = new Promise((done) => {
      ws.on('close', (code, reason) => {
        done({ code, reason });
        for (const waiter of this.waiters.splice(0)) {
          waiter.fail(new Error(`socket closed (${code}) while waiting for a frame`));
        }
      });
    });
    ws.on('message', (text) => {
      const frame = JSON.parse(text);
      const waiter = this.waiters.shift();
      if (waiter) {
        waiter.done(frame);
      } else {
        this.queue.push(frame);
      }
    });
  }

  send(frame) {
    this.ws.send(typeof frame === 'string' ? frame : JSON.stringify(frame));
  }

  next(timeout = 2000) {
    if (this.queue.length > 0) {
      return Promise.resolve(this.queue.shift());
    }
    if (this.ws.closed) {
      return Promise.reject(new Error('socket is closed'));
    }
    return new Promise((done, fail) => {
      const waiter = {
        done: (frame) => {
          clearTimeout(timer);
          done(frame);
        },
        fail: (error) => {
          clearTimeout(timer);
          fail(error);
        },
      };
      const timer = setTimeout(() => {
        this.waiters = this.waiters.filter((w) => w !== waiter);
        fail(new Error(`no frame within ${timeout} ms`));
      }, timeout);
      this.waiters.push(waiter);
    });
  }

  // Resolves with no frame pending after `ms`, or fails with the unexpected frame.
  async expectSilence(ms = 100) {
    await new Promise((done) => setTimeout(done, ms));
    if (this.queue.length > 0) {
      throw new Error(`unexpected frame ${JSON.stringify(this.queue[0])}`);
    }
  }

  close(code = 1000) {
    this.ws.close(code);
    return this.closed;
  }
}
