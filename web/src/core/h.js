// DOM construction. Text only ever reaches the DOM through text nodes or textContent, and attributes
// pass an allow-list, so markup strings cannot become elements. Styles go through the CSSOM because
// the CSP has no 'unsafe-inline' for style attributes.

const SVG_NS = 'http://www.w3.org/2000/svg';

const ATTRIBUTES = new Set([
  'accept', 'alt', 'autocomplete', 'autofocus', 'cols', 'colspan', 'contenteditable', 'datetime',
  'dir', 'disabled', 'download', 'draggable', 'enterkeyhint', 'for', 'height', 'hidden', 'id',
  'inert', 'inputmode', 'lang', 'loading', 'max', 'maxlength', 'min', 'minlength', 'multiple',
  'name', 'open', 'pattern', 'placeholder', 'readonly', 'rel', 'required', 'role', 'rows',
  'rowspan', 'selected', 'spellcheck', 'step', 'tabindex', 'target', 'title', 'translate', 'type',
  'width',
]);

const SVG_ATTRIBUTES = new Set([
  'aria-hidden', 'class', 'clip-rule', 'cx', 'cy', 'd', 'fill', 'fill-rule', 'focusable', 'height',
  'points', 'r', 'role', 'rx', 'ry', 'stroke', 'stroke-linecap', 'stroke-linejoin', 'stroke-width',
  'transform', 'viewBox', 'width', 'x', 'x1', 'x2', 'y', 'y1', 'y2',
]);

const URL_ATTRIBUTES = new Set(['href', 'src', 'action', 'poster']);

// Properties rather than attributes, so they reflect the live state of form controls.
const PROPERTIES = new Set(['value', 'checked', 'indeterminate']);

const SAFE_LINK = /^(?:https?:|mailto:)/i;
const SAFE_IMAGE = /^(?:https?:|blob:|data:image\/(?:png|jpeg|gif|webp);)/i;

// Strips what the URL parser strips (tab/LF/CR anywhere, C0 controls and spaces at the ends), so
// "java\tscript:" cannot pass as a relative URL.
export function safeUrl(value, kind = 'link') {
  const url = String(value).replace(/[\t\n\r]/g, '').replace(/^[\u0000- ]+|[\u0000- ]+$/g, '');
  // Browsers read "\" as "/" in http(s) URLs, so "/\host" is protocol-relative too.
  if (url.replace(/\\/g, '/').startsWith('//')) {
    return null;
  }
  if (url.startsWith('#') || url.startsWith('/') || !/^[a-z][a-z0-9+.-]*:/i.test(url)) {
    return url;
  }
  return (kind === 'image' ? SAFE_IMAGE : SAFE_LINK).test(url) ? url : null;
}

export function setAttr(el, name, value) {
  const svg = el.namespaceURI === SVG_NS;
  if (URL_ATTRIBUTES.has(name)) {
    if (value == null || value === false) {
      el.removeAttribute(name);
      return;
    }
    const safe = safeUrl(value, name === 'href' ? 'link' : 'image');
    if (safe === null) {
      throw new Error(`h: refused ${name}="${value}"`);
    }
    el.setAttribute(name, safe);
    return;
  }
  if (!(svg ? SVG_ATTRIBUTES : ATTRIBUTES).has(name) && !name.startsWith('aria-') && !name.startsWith('data-')) {
    throw new Error(`h: attribute "${name}" is not allowed`);
  }
  if (value == null || value === false) {
    el.removeAttribute(name);
  } else {
    el.setAttribute(name, value === true ? '' : String(value));
  }
}

export function setClass(el, value) {
  const names = Array.isArray(value) ? value.filter(Boolean).join(' ') : value;
  if (el.namespaceURI === SVG_NS) {
    el.setAttribute('class', names ?? '');
  } else {
    el.className = names ?? '';
  }
}

export function setStyle(el, style) {
  for (const [name, value] of Object.entries(style)) {
    if (value == null || value === false) {
      el.style.removeProperty(name);
    } else {
      el.style.setProperty(name, String(value));
    }
  }
}

function applyProps(el, props) {
  for (const [name, value] of Object.entries(props)) {
    if (value === undefined && (name === 'on' || name === 'style' || name === 'dataset')) {
      continue;
    }
    if (name === 'class') {
      setClass(el, value);
    } else if (name === 'text') {
      el.textContent = value ?? '';
    } else if (name === 'style') {
      if (typeof value !== 'object' || value === null) {
        throw new TypeError('h: style takes an object of properties');
      }
      setStyle(el, value);
    } else if (name === 'dataset') {
      for (const [key, v] of Object.entries(value)) {
        if (v == null) {
          delete el.dataset[key];
        } else {
          el.dataset[key] = String(v);
        }
      }
    } else if (name === 'on') {
      for (const [type, handler] of Object.entries(value)) {
        el.addEventListener(type, handler);
      }
    } else if (PROPERTIES.has(name)) {
      el[name] = value;
    } else if (name.startsWith('on')) {
      throw new Error(`h: use the "on" prop instead of "${name}"`);
    } else {
      setAttr(el, name, value);
    }
  }
}

export function appendChildren(parent, children) {
  for (const child of children) {
    if (child == null || child === false || child === true) {
      continue;
    }
    if (Array.isArray(child)) {
      appendChildren(parent, child);
    } else if (typeof child === 'object') {
      parent.appendChild(child.el ?? child);
    } else {
      parent.appendChild(document.createTextNode(String(child)));
    }
  }
  return parent;
}

export function h(tag, props, ...children) {
  const el = document.createElement(tag);
  if (props) {
    applyProps(el, props);
  }
  return appendChildren(el, children);
}

export function s(tag, props, ...children) {
  const el = document.createElementNS(SVG_NS, tag);
  if (props) {
    applyProps(el, props);
  }
  return appendChildren(el, children);
}

export function patch(el, props) {
  applyProps(el, props);
  return el;
}

export function replaceContent(el, ...children) {
  el.replaceChildren();
  return appendChildren(el, children);
}
