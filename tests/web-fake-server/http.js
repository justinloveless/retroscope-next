// HTTP fixture routing for the fake server: `:name` path parameters, a trailing `*`, JSON and
// problem+json responses. Later registrations win, so a test can override any default route.

export function compilePattern(pattern) {
  const parts = pattern.split('/');
  return (pathname) => {
    const have = pathname.split('/');
    const params = {};
    for (let i = 0; i < parts.length; i++) {
      if (parts[i] === '*') {
        params['*'] = have.slice(i).join('/');
        return params;
      }
      if (i >= have.length) {
        return null;
      }
      if (parts[i].startsWith(':')) {
        if (have[i] === '') {
          return null;
        }
        try {
          params[parts[i].slice(1)] = decodeURIComponent(have[i]);
        } catch {
          return null;
        }
      } else if (parts[i] !== have[i]) {
        return null;
      }
    }
    return have.length === parts.length ? params : null;
  };
}

export function problem(status, code, extra = {}) {
  return { status, body: { type: 'about:blank', title: code, status, code, ...extra } };
}

export function json(body, status = 200, headers = {}) {
  return { status, body, headers };
}

export class Router {
  constructor() {
    this.routes = [];
  }

  add(method, pattern, response) {
    const route = { method: method.toUpperCase(), pattern, match: compilePattern(pattern), response };
    this.routes.unshift(route);
    return () => {
      this.routes = this.routes.filter((r) => r !== route);
    };
  }

  find(method, pathname) {
    for (const route of this.routes) {
      if (route.method !== '*' && route.method !== method) {
        continue;
      }
      const params = route.match(pathname);
      if (params) {
        return { route, params };
      }
    }
    return null;
  }
}

const sleep = (ms) => new Promise((done) => setTimeout(done, ms));

// Writes a fixture response: `{status, body, headers, delayMs}`; an object body is JSON, problem+json
// when the status is an error and the body carries a `code`; a Buffer or string is sent as is.
export async function writeResponse(res, response, baseHeaders = {}) {
  const { status = 200, body, headers = {}, delayMs = 0 } = response ?? { status: 204 };
  if (delayMs > 0) {
    await sleep(delayMs);
  }
  const out = { 'Cache-Control': 'no-store', ...baseHeaders, ...headers };
  let payload = null;
  if (Buffer.isBuffer(body) || typeof body === 'string') {
    payload = body;
  } else if (body !== undefined && body !== null) {
    payload = JSON.stringify(body);
    const isProblem = status >= 400 && typeof body.code === 'string';
    out['Content-Type'] ??= isProblem ? 'application/problem+json' : 'application/json; charset=utf-8';
  }
  res.writeHead(status, out);
  res.end(payload);
}
