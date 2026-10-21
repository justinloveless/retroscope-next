// fetch wrapper for /api. Every request carries X-RS (the CSRF filter requires it on writes), JSON in
// and out, and problem+json errors become ApiError with the server's stable code. Idempotent requests
// are retried on 502/503 honouring Retry-After; POST and PATCH never are (DESIGN §d).

const IDEMPOTENT = new Set(['GET', 'HEAD', 'PUT', 'DELETE']);
const RETRYABLE = new Set([502, 503]);
const MAX_TRIES = 3;
const MAX_RETRY_AFTER_MS = 10_000;

export class ApiError extends Error {
  constructor({ status, code, title, detail, fields, retryAfterMs }) {
    super(detail || title || code);
    this.name = 'ApiError';
    this.status = status;
    this.code = code;
    this.title = title;
    this.detail = detail;
    this.fields = fields;
    this.retryAfterMs = retryAfterMs;
  }
}

export function parseRetryAfter(value, now = Date.now()) {
  if (value == null || value === '') {
    return null;
  }
  if (/^\d+$/.test(value.trim())) {
    return Number(value) * 1000;
  }
  const at = Date.parse(value);
  return Number.isNaN(at) ? null : Math.max(0, at - now);
}

function abortableSleep(ms, signal) {
  return new Promise((resolve, reject) => {
    if (signal?.aborted) {
      reject(signal.reason);
      return;
    }
    const timer = setTimeout(() => {
      signal?.removeEventListener('abort', onAbort);
      resolve();
    }, ms);
    function onAbort() {
      clearTimeout(timer);
      reject(signal.reason);
    }
    signal?.addEventListener('abort', onAbort, { once: true });
  });
}

async function readBody(response) {
  if (response.status === 204 || response.status === 205) {
    return null;
  }
  const type = response.headers.get('content-type') ?? '';
  if (/[/+]json\b/.test(type)) {
    const text = await response.text();
    return text ? JSON.parse(text) : null;
  }
  return response.text();
}

async function toError(response, retryAfterMs) {
  let problem = {};
  try {
    const body = await readBody(response);
    if (body && typeof body === 'object') {
      problem = body;
    }
  } catch {
    // An unreadable error body still maps to the status code below.
  }
  return new ApiError({
    status: response.status,
    code: typeof problem.code === 'string' ? problem.code : `http_${response.status}`,
    title: problem.title,
    detail: problem.detail,
    fields: problem.fields,
    retryAfterMs,
  });
}

function backoffMs(attempt, random) {
  const base = 250 * 2 ** attempt;
  return base / 2 + random() * (base / 2);
}

export function createHttp({ fetch: fetchFn = (...a) => globalThis.fetch(...a), sleep = abortableSleep, random = Math.random } = {}) {
  async function request(method, url, { body, signal, headers } = {}) {
    method = method.toUpperCase();
    const init = {
      method,
      signal,
      credentials: 'same-origin',
      headers: { 'X-RS': '1', Accept: 'application/json', ...headers },
    };
    if (body !== undefined) {
      if (body instanceof FormData || body instanceof Blob || body instanceof ArrayBuffer) {
        init.body = body;
      } else {
        init.body = JSON.stringify(body);
        init.headers['Content-Type'] = 'application/json';
      }
    }

    for (let attempt = 0; ; attempt++) {
      let response;
      try {
        response = await fetchFn(url, init);
      } catch (error) {
        if (error?.name === 'AbortError' || signal?.aborted) {
          throw error;
        }
        throw new ApiError({ status: 0, code: 'network', detail: 'The server could not be reached.' });
      }
      if (response.ok) {
        return readBody(response);
      }
      const retryAfterMs = parseRetryAfter(response.headers.get('retry-after'));
      const canRetry = IDEMPOTENT.has(method)
        && RETRYABLE.has(response.status)
        && attempt + 1 < MAX_TRIES
        && (retryAfterMs === null || retryAfterMs <= MAX_RETRY_AFTER_MS);
      if (!canRetry) {
        throw await toError(response, retryAfterMs);
      }
      await response.body?.cancel?.();
      await sleep(retryAfterMs ?? backoffMs(attempt, random), signal);
    }
  }

  return {
    request,
    get: (url, options) => request('GET', url, options),
    post: (url, body, options) => request('POST', url, { ...options, body }),
    put: (url, body, options) => request('PUT', url, { ...options, body }),
    patch: (url, body, options) => request('PATCH', url, { ...options, body }),
    del: (url, options) => request('DELETE', url, options),
  };
}

export const http = createHttp();
