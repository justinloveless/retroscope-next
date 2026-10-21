import { test } from 'node:test';
import assert from 'node:assert/strict';
import { ApiError, createHttp, parseRetryAfter } from '../../../web/src/core/http.js';

function response(status, { body, headers = {} } = {}) {
  const h = new Headers(headers);
  if (body !== undefined && !h.has('content-type')) {
    h.set('content-type', 'application/json');
  }
  return new Response(body === undefined ? null : JSON.stringify(body), { status, headers: h });
}

function harness(responses) {
  const calls = [];
  const sleeps = [];
  const queue = [...responses];
  const http = createHttp({
    fetch: async (url, init) => {
      calls.push({ url, init });
      const next = queue.shift();
      if (next instanceof Error) {
        throw next;
      }
      return next;
    },
    sleep: async (ms) => {
      sleeps.push(ms);
    },
    random: () => 0.5,
  });
  return { http, calls, sleeps };
}

test('GET on 503 with Retry-After waits that long and retries', async () => {
  const { http, calls, sleeps } = harness([
    response(503, { headers: { 'retry-after': '2' } }),
    response(200, { body: { ok: true } }),
  ]);
  assert.deepEqual(await http.get('/api/x'), { ok: true });
  assert.equal(calls.length, 2);
  assert.deepEqual(sleeps, [2000]);
});

test('PUT and DELETE are retried on 502, without Retry-After using backoff', async () => {
  for (const method of ['put', 'del']) {
    const { http, calls, sleeps } = harness([response(502), response(502), response(204)]);
    const result = method === 'put' ? await http.put('/api/x', { a: 1 }) : await http.del('/api/x');
    assert.equal(result, null);
    assert.equal(calls.length, 3);
    assert.equal(sleeps.length, 2);
    assert.ok(sleeps[1] > sleeps[0], 'backoff grows');
  }
});

test('at most three tries, then the last error is thrown', async () => {
  const { http, calls } = harness([
    response(503, { headers: { 'retry-after': '0' } }),
    response(503, { headers: { 'retry-after': '0' } }),
    response(503, { body: { code: 'busy', title: 'Busy' }, headers: { 'retry-after': '0', 'content-type': 'application/problem+json' } }),
    response(200, { body: {} }),
  ]);
  await assert.rejects(http.get('/api/x'), (e) => e instanceof ApiError && e.status === 503 && e.code === 'busy');
  assert.equal(calls.length, 3);
});

test('POST and PATCH are never retried', async () => {
  for (const method of ['post', 'patch']) {
    const { http, calls } = harness([response(503, { headers: { 'retry-after': '1' } }), response(200, { body: {} })]);
    await assert.rejects(http[method]('/api/x', {}), (e) => e.status === 503 && e.retryAfterMs === 1000);
    assert.equal(calls.length, 1);
  }
});

test('other statuses are not retried', async () => {
  for (const status of [500, 504, 429, 404]) {
    const { http, calls } = harness([response(status), response(200, { body: {} })]);
    await assert.rejects(http.get('/api/x'), (e) => e.status === status && e.code === `http_${status}`);
    assert.equal(calls.length, 1);
  }
});

test('a Retry-After longer than 10 s is not waited for', async () => {
  const { http, calls, sleeps } = harness([response(503, { headers: { 'retry-after': '60' } }), response(200, { body: {} })]);
  await assert.rejects(http.get('/api/x'), (e) => e.retryAfterMs === 60000);
  assert.equal(calls.length, 1);
  assert.deepEqual(sleeps, []);
});

test('Retry-After as an HTTP date', () => {
  const now = Date.parse('2026-10-20T10:00:00Z');
  assert.equal(parseRetryAfter('Tue, 20 Oct 2026 10:00:03 GMT', now), 3000);
  assert.equal(parseRetryAfter('Tue, 20 Oct 2026 09:00:00 GMT', now), 0);
  assert.equal(parseRetryAfter('soon', now), null);
  assert.equal(parseRetryAfter(null, now), null);
});

test('requests carry X-RS, JSON bodies and same-origin credentials', async () => {
  const { http, calls } = harness([response(200, { body: { id: 1 } })]);
  assert.deepEqual(await http.post('/api/teams', { name: 'A' }), { id: 1 });
  const { init } = calls[0];
  assert.equal(init.method, 'POST');
  assert.equal(init.headers['X-RS'], '1');
  assert.equal(init.headers['Content-Type'], 'application/json');
  assert.equal(init.body, '{"name":"A"}');
  assert.equal(init.credentials, 'same-origin');
});

test('problem+json maps to ApiError with code and fields', async () => {
  const { http } = harness([response(400, {
    body: { type: 'about:blank', title: 'Invalid', status: 400, code: 'validation', detail: 'Name is required', fields: { name: 'required' } },
    headers: { 'content-type': 'application/problem+json' },
  })]);
  await assert.rejects(http.post('/api/x', {}), (e) => {
    assert.ok(e instanceof ApiError);
    assert.equal(e.code, 'validation');
    assert.equal(e.message, 'Name is required');
    assert.deepEqual(e.fields, { name: 'required' });
    return true;
  });
});

test('a network failure becomes ApiError "network"; an abort stays an AbortError', async () => {
  const { http } = harness([new TypeError('fetch failed')]);
  await assert.rejects(http.get('/api/x'), (e) => e instanceof ApiError && e.code === 'network' && e.status === 0);

  const abort = new DOMException('aborted', 'AbortError');
  const { http: http2 } = harness([abort]);
  await assert.rejects(http2.get('/api/x'), (e) => e.name === 'AbortError');
});

test('the default sleep is cancelled by the abort signal', async () => {
  let calls = 0;
  const http = createHttp({
    fetch: async () => {
      calls++;
      return response(503, { headers: { 'retry-after': '5' } });
    },
  });
  const controller = new AbortController();
  const pending = http.get('/api/x', { signal: controller.signal });
  setTimeout(() => controller.abort(), 10);
  await assert.rejects(pending, (e) => e.name === 'AbortError');
  assert.equal(calls, 1);
});
