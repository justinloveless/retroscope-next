// History API router over the frozen route table (web/src/routes.js). A route module exposes
// mount(root, boot) → {update(params), unmount()}. Navigating between paths served by the same module
// only calls update(params), where params holds the path parameters plus `route` (the matched
// pattern) and `query` (URLSearchParams); a different module fetches /api/boot?path= first, then remounts.

// RS_STYLES is defined by build.mjs: route module → its stylesheet URLs (beyond the kit).
const ROUTE_STYLES = typeof RS_STYLES === 'object' ? RS_STYLES : {};

// Links a route's stylesheets before it mounts, so it never paints unstyled. The shell already links
// the first page's; a failed load still resolves (the screen is usable, just unstyled).
export function loadStyles(urls = []) {
  return Promise.all(urls.map((url) => {
    if ([...document.querySelectorAll('link[rel=stylesheet]')].some((l) => l.getAttribute('href') === url)) {
      return null;
    }
    return new Promise((done) => {
      const link = document.createElement('link');
      link.setAttribute('rel', 'stylesheet');
      link.setAttribute('href', url);
      link.addEventListener('load', done, { once: true });
      link.addEventListener('error', done, { once: true });
      document.head.appendChild(link);
    });
  }));
}

export function match(pattern, path) {
  const want = pattern.split('/');
  const have = path.split('/');
  const params = {};
  for (let i = 0; i < want.length; i++) {
    if (want[i] === '*') {
      return params;
    }
    if (i >= have.length) {
      return null;
    }
    if (want[i].startsWith(':')) {
      if (have[i] === '') {
        return null;
      }
      try {
        params[want[i].slice(1)] = decodeURIComponent(have[i]);
      } catch (error) {
        if (error instanceof URIError) {
          return null;
        }
        throw error;
      }
    } else if (want[i] !== have[i]) {
      return null;
    }
  }
  return want.length === have.length ? params : null;
}

export function resolve(routes, url) {
  for (const route of routes) {
    const params = match(route.path, url.pathname);
    if (params) {
      return { route, params: { ...params, route: route.path, query: url.searchParams } };
    }
  }
  return null;
}

// A click the browser should handle itself: modified, non-primary, new-window or cross-origin.
function isPlainNavigation(event, anchor) {
  return event.button === 0
    && !event.defaultPrevented
    && !event.metaKey && !event.ctrlKey && !event.shiftKey && !event.altKey
    && (!anchor.target || anchor.target === '_self')
    && !anchor.hasAttribute('download')
    && anchor.origin === location.origin;
}

/**
 * options: routes, root, boot (the first page's boot JSON), loadBoot(path, signal) for in-app
 * navigation, notFound(root) → view, onError(error, url).
 */
export function createRouter({ routes, root, boot, loadBoot, notFound, onError }) {
  let current = null;
  let token = 0;
  let pending = null;

  async function show(url, firstBoot) {
    const run = ++token;
    pending?.abort();
    pending = null;
    const found = resolve(routes, url);

    if (found && current && current.module === found.route.module) {
      current.view.update(found.params);
      return;
    }

    let nextBoot = firstBoot;
    let screen = null;
    if (found) {
      const controller = new AbortController();
      pending = controller;
      const path = url.pathname + url.search;
      [screen, nextBoot] = await Promise.all([
        found.route.load(),
        nextBoot ?? loadBoot(path, controller.signal),
        loadStyles(ROUTE_STYLES[found.route.module]),
      ]);
      if (run !== token) {
        return;
      }
      pending = null;
    }

    current?.view.unmount();
    current = null;
    if (found) {
      const view = screen.mount(root, nextBoot);
      current = { module: found.route.module, view };
      view.update(found.params);
    } else {
      notFound?.(root);
    }
  }

  function go(url, firstBoot) {
    const shown = show(url, firstBoot);
    const run = token;
    return shown.catch((error) => {
      // A newer navigation owns the screen; reporting or reloading this URL would undo it.
      if (error?.name === 'AbortError' || run !== token) {
        return;
      }
      if (onError) {
        onError(error, url);
      } else {
        location.assign(url.href);
      }
    });
  }

  function navigate(to, { replace = false } = {}) {
    const url = new URL(to, location.href);
    if (url.origin !== location.origin) {
      location.assign(url.href);
      return Promise.resolve();
    }
    if (replace) {
      history.replaceState(null, '', url.href);
    } else {
      history.pushState(null, '', url.href);
      window.scrollTo(0, 0);
    }
    return go(url);
  }

  function onClick(event) {
    const anchor = event.target.closest?.('a[data-nav]');
    if (anchor && isPlainNavigation(event, anchor)) {
      event.preventDefault();
      navigate(anchor.href);
    }
  }

  function onPopState() {
    go(new URL(location.href));
  }

  function start() {
    document.addEventListener('click', onClick);
    window.addEventListener('popstate', onPopState);
    return go(new URL(location.href), boot);
  }

  function stop() {
    document.removeEventListener('click', onClick);
    window.removeEventListener('popstate', onPopState);
    pending?.abort();
    current?.view.unmount();
    current = null;
  }

  return { start, stop, navigate };
}
