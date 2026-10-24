// A small DOM for node --test: enough tree, attribute, selector and event behaviour for the core and
// kit modules.

const SVG_NS = 'http://www.w3.org/2000/svg';
const HTML_NS = 'http://www.w3.org/1999/xhtml';

export class FakeEvent {
  constructor(type, init = {}) {
    this.type = type;
    this.bubbles = init.bubbles ?? false;
    Object.assign(this, init);
    this.defaultPrevented = false;
    this.propagationStopped = false;
  }

  preventDefault() {
    this.defaultPrevented = true;
  }

  stopPropagation() {
    this.propagationStopped = true;
  }
}

class Target {
  constructor() {
    this.listeners = new Map();
  }

  addEventListener(type, fn, options) {
    const once = typeof options === 'object' && options?.once;
    const list = this.listeners.get(type) ?? [];
    if (!list.some((l) => l.fn === fn)) {
      list.push({ fn, once });
    }
    this.listeners.set(type, list);
  }

  removeEventListener(type, fn) {
    const list = this.listeners.get(type);
    if (list) {
      this.listeners.set(type, list.filter((l) => l.fn !== fn));
    }
  }

  fire(event) {
    event.currentTarget = this;
    for (const listener of [...(this.listeners.get(event.type) ?? [])]) {
      if (listener.once) {
        this.removeEventListener(event.type, listener.fn);
      }
      listener.fn.call(this, event);
    }
  }

  dispatchEvent(event) {
    event.target ??= this;
    for (let node = this; node && !event.propagationStopped; node = event.bubbles ? node.parentNode ?? (node === node.ownerDocument?.documentElement ? node.ownerDocument : null) : null) {
      node.fire(event);
      if (!event.bubbles) {
        break;
      }
    }
    return !event.defaultPrevented;
  }
}

class Node extends Target {
  constructor(doc) {
    super();
    this.ownerDocument = doc;
    this.parentNode = null;
    this.childNodes = [];
  }

  get firstChild() {
    return this.childNodes[0] ?? null;
  }

  get lastChild() {
    return this.childNodes[this.childNodes.length - 1] ?? null;
  }

  get nextSibling() {
    const siblings = this.parentNode?.childNodes;
    return siblings ? siblings[siblings.indexOf(this) + 1] ?? null : null;
  }

  get previousSibling() {
    const siblings = this.parentNode?.childNodes;
    return siblings ? siblings[siblings.indexOf(this) - 1] ?? null : null;
  }

  get isConnected() {
    let node = this;
    while (node.parentNode) {
      node = node.parentNode;
    }
    return node === this.ownerDocument;
  }

  contains(other) {
    for (let node = other; node; node = node.parentNode) {
      if (node === this) {
        return true;
      }
    }
    return false;
  }

  insertBefore(node, ref) {
    if (ref && ref.parentNode !== this) {
      throw new Error('insertBefore: reference is not a child');
    }
    node.parentNode?.detach(node);
    const index = ref ? this.childNodes.indexOf(ref) : this.childNodes.length;
    this.childNodes.splice(index, 0, node);
    node.parentNode = this;
    return node;
  }

  appendChild(node) {
    return this.insertBefore(node, null);
  }

  append(...nodes) {
    for (const node of nodes) {
      this.appendChild(typeof node === 'string' ? this.ownerDocument.createTextNode(node) : node);
    }
  }

  // As in a browser, a focused element taken out of the document leaves focus on the body.
  detach(node) {
    this.childNodes.splice(this.childNodes.indexOf(node), 1);
    node.parentNode = null;
    const doc = this.ownerDocument;
    if (doc && node.contains(doc.activeElement)) {
      doc.activeElement = doc.body;
    }
  }

  removeChild(node) {
    this.detach(node);
    return node;
  }

  remove() {
    this.parentNode?.removeChild(this);
  }

  after(...nodes) {
    const parent = this.parentNode;
    const next = this.nextSibling;
    for (const node of nodes) {
      parent.insertBefore(node, next);
    }
  }

  replaceWith(node) {
    const parent = this.parentNode;
    parent.insertBefore(node, this);
    parent.removeChild(this);
  }

  replaceChildren(...nodes) {
    for (const child of [...this.childNodes]) {
      this.detach(child);
    }
    this.append(...nodes);
  }

  get textContent() {
    return this.childNodes.map((c) => c.textContent).join('');
  }

  set textContent(value) {
    this.replaceChildren();
    if (value !== '' && value != null) {
      this.appendChild(this.ownerDocument.createTextNode(String(value)));
    }
  }
}

class Text extends Node {
  constructor(doc, data) {
    super(doc);
    this.nodeType = 3;
    this.data = data;
  }

  get textContent() {
    return this.data;
  }

  set textContent(value) {
    this.data = String(value);
  }
}

function camelToData(key) {
  return 'data-' + key.replace(/[A-Z]/g, (c) => '-' + c.toLowerCase());
}

class Style {
  constructor() {
    this.props = new Map();
  }

  setProperty(name, value) {
    this.props.set(name, value);
  }

  removeProperty(name) {
    this.props.delete(name);
  }

  getPropertyValue(name) {
    return this.props.get(name) ?? '';
  }
}

function parseCompound(selector) {
  const parts = { tag: null, conditions: [] };
  let rest = selector.trim();
  const tag = rest.match(/^[a-zA-Z][\w-]*/);
  if (tag) {
    parts.tag = tag[0].toLowerCase();
    rest = rest.slice(tag[0].length);
  }
  while (rest) {
    let m;
    if ((m = rest.match(/^#([\w-]+)/))) {
      parts.conditions.push((el) => el.getAttribute('id') === m[1]);
    } else if ((m = rest.match(/^\.([\w-]+)/))) {
      parts.conditions.push((el) => el.classList.contains(m[1]));
    } else if ((m = rest.match(/^\[([\w-]+)(?:=(?:"([^"]*)"|([^\]]*)))?\]/))) {
      const [, name, quoted, bare] = m;
      const value = quoted ?? bare;
      parts.conditions.push((el) => (value === undefined ? el.hasAttribute(name) : el.getAttribute(name) === value));
    } else if ((m = rest.match(/^:not\(([^()]*)\)/))) {
      const inner = parseCompound(m[1]);
      parts.conditions.push((el) => !matchesCompound(el, inner));
    } else {
      throw new Error(`fake DOM: unsupported selector "${selector}"`);
    }
    rest = rest.slice(m[0].length);
  }
  return parts;
}

function matchesCompound(el, parts) {
  return (!parts.tag || el.localName === parts.tag) && parts.conditions.every((c) => c(el));
}

function matches(el, selector) {
  return selector.split(',').some((s) => matchesCompound(el, parseCompound(s)));
}

class Element extends Node {
  constructor(doc, tag, ns = HTML_NS) {
    super(doc);
    this.nodeType = 1;
    this.namespaceURI = ns;
    this.localName = ns === HTML_NS ? tag.toLowerCase() : tag;
    this.tagName = ns === HTML_NS ? tag.toUpperCase() : tag;
    this.attributes = new Map();
    this.style = new Style();
    this.animations = [];
    this.rect = { top: 0, left: 0, right: 0, bottom: 0, width: 0, height: 0 };
    this.scrollHeight = 0;
    this.offsetHeight = 0;
    this.clientHeight = 0;
    const element = this;
    this.dataset = new Proxy({}, {
      get: (_, key) => element.getAttribute(camelToData(key)) ?? undefined,
      set: (_, key, value) => {
        element.setAttribute(camelToData(key), value);
        return true;
      },
      deleteProperty: (_, key) => {
        element.removeAttribute(camelToData(key));
        return true;
      },
    });
  }

  setAttribute(name, value) {
    this.attributes.set(name, String(value));
  }

  getAttribute(name) {
    return this.attributes.get(name) ?? null;
  }

  hasAttribute(name) {
    return this.attributes.has(name);
  }

  removeAttribute(name) {
    this.attributes.delete(name);
  }

  get id() {
    return this.getAttribute('id') ?? '';
  }

  set id(value) {
    this.setAttribute('id', value);
  }

  get className() {
    return this.getAttribute('class') ?? '';
  }

  set className(value) {
    this.setAttribute('class', value);
  }

  get classList() {
    const element = this;
    const list = () => element.className.split(/\s+/).filter(Boolean);
    return {
      contains: (c) => list().includes(c),
      add: (...cs) => {
        element.className = [...new Set([...list(), ...cs])].join(' ');
      },
      remove: (...cs) => {
        element.className = list().filter((c) => !cs.includes(c)).join(' ');
      },
      toggle: (c, force) => {
        const on = force ?? !list().includes(c);
        if (on) {
          element.classList.add(c);
        } else {
          element.classList.remove(c);
        }
        return on;
      },
    };
  }

  get hidden() {
    return this.hasAttribute('hidden');
  }

  set hidden(value) {
    if (value) {
      this.setAttribute('hidden', '');
    } else {
      this.removeAttribute('hidden');
    }
  }

  get disabled() {
    return this.hasAttribute('disabled');
  }

  set disabled(value) {
    if (value) {
      this.setAttribute('disabled', '');
    } else {
      this.removeAttribute('disabled');
    }
  }

  get tabIndex() {
    const value = this.getAttribute('tabindex');
    return value === null ? (['button', 'a', 'input', 'select', 'textarea'].includes(this.localName) ? 0 : -1) : Number(value);
  }

  set tabIndex(value) {
    this.setAttribute('tabindex', String(value));
  }

  get title() {
    return this.getAttribute('title') ?? '';
  }

  set title(value) {
    this.setAttribute('title', value);
  }

  get children() {
    return this.childNodes.filter((c) => c.nodeType === 1);
  }

  get firstElementChild() {
    return this.children[0] ?? null;
  }

  matches(selector) {
    return matches(this, selector);
  }

  closest(selector) {
    for (let node = this; node && node.nodeType === 1; node = node.parentNode) {
      if (node.matches(selector)) {
        return node;
      }
    }
    return null;
  }

  querySelectorAll(selector) {
    const found = [];
    const walk = (node) => {
      for (const child of node.children) {
        if (child.matches(selector)) {
          found.push(child);
        }
        walk(child);
      }
    };
    walk(this);
    return found;
  }

  querySelector(selector) {
    return this.querySelectorAll(selector)[0] ?? null;
  }

  getBoundingClientRect() {
    return { ...this.rect };
  }

  animate(keyframes, options) {
    this.animations.push({ keyframes, options });
    return { finished: Promise.resolve() };
  }

  focus() {
    this.ownerDocument.activeElement = this;
    this.dispatchEvent(new FakeEvent('focus'));
    this.dispatchEvent(new FakeEvent('focusin', { bubbles: true }));
  }

  blur() {
    if (this.ownerDocument.activeElement === this) {
      this.ownerDocument.activeElement = this.ownerDocument.body;
    }
  }

  click() {
    this.dispatchEvent(new FakeEvent('click', { bubbles: true, button: 0 }));
  }
}

class Document extends Node {
  constructor() {
    super(null);
    this.ownerDocument = this;
    this.documentElement = new Element(this, 'html');
    this.head = new Element(this, 'head');
    this.body = new Element(this, 'body');
    this.childNodes.push(this.documentElement);
    this.documentElement.parentNode = this;
    this.documentElement.appendChild(this.head);
    this.documentElement.appendChild(this.body);
    this.activeElement = this.body;
  }

  createElement(tag) {
    return new Element(this, tag);
  }

  createElementNS(ns, tag) {
    return new Element(this, tag, ns);
  }

  createTextNode(data) {
    return new Text(this, data);
  }

  getElementById(id) {
    return this.documentElement.querySelector(`#${id}`);
  }

  querySelectorAll(selector) {
    return [this.documentElement, ...this.documentElement.querySelectorAll(selector)].filter((el) => el.matches(selector));
  }

  querySelector(selector) {
    return this.querySelectorAll(selector)[0] ?? null;
  }

}

export function installDom() {
  const doc = new Document();
  globalThis.document = doc;
  return doc;
}

export { SVG_NS };
