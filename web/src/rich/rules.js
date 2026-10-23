// Value rules of the ADF allow-list (adf-schema.json), compiled to predicates. The server's AdfWrite applies the
// same rules on save; these keep the editor from holding a value the save would refuse.

import { isAllowedHref } from '../markup/doc.js';
import allowList from './adf-schema.json' with { type: 'json' };

export { allowList };

// AdfColor.IsHex: the value reaches the CSSOM, so nothing but #rrggbb.
const HEX = /^#[0-9a-fA-F]{6}$/;
// ms of a UTC instant up to 9999-12-31T23:59:59.999Z, written as a decimal string.
const MAX_TIMESTAMP = 253402300799999;
const CONTROL = /[\u0000-\u001f\u007f]/;

export function isAdfColor(v) {
  return typeof v === 'string' && HEX.test(v);
}

export function isHref(v, schemes) {
  if (!isAllowedHref(v)) {
    return false;
  }
  return !schemes || schemes.some((s) => v.toLowerCase().startsWith(`${s}:`));
}

// No lone surrogate: Jira's parser and ours would disagree on it.
export function isWellFormed(text) {
  return typeof text === 'string' && !/[\ud800-\udbff](?![\udc00-\udfff])|(?<![\ud800-\udbff])[\udc00-\udfff]/.test(text);
}

function codePoints(text) {
  let n = 0;
  for (const _ of text) {
    n++;
  }
  return n;
}

function stringRule(rule) {
  const pattern = rule.pattern ? new RegExp(`^(?:${rule.pattern})$`) : null;
  return (v) => typeof v === 'string'
    && isWellFormed(v)
    && v.length >= (rule.min ?? 0)
    && (rule.max === undefined || v.length <= rule.max)
    && (rule.maxCodePoints === undefined || codePoints(v) <= rule.maxCodePoints)
    && !(rule.singleLine && CONTROL.test(v))
    && (pattern === null || pattern.test(v));
}

function numberRule(rule, integer) {
  return (v) => typeof v === 'number'
    && Number.isFinite(v)
    && (!integer || Number.isInteger(v))
    && v >= rule.min
    && v <= rule.max;
}

/** A rule (or the name of a shared one) as a predicate over one attribute value. */
export function compileRule(rule) {
  const spec = typeof rule === 'string' ? allowList.rules[rule] : rule;
  if (!spec) {
    throw new Error(`adf-schema.json: unknown rule ${JSON.stringify(rule)}`);
  }
  switch (spec.type) {
    case 'string':
      return stringRule(spec);
    case 'int':
      return numberRule(spec, true);
    case 'number':
      return numberRule(spec, false);
    case 'bool':
      return (v) => typeof v === 'boolean';
    case 'enum': {
      const values = new Set(spec.values);
      return spec.orColor ? (v) => values.has(v) || isAdfColor(v) : (v) => values.has(v);
    }
    case 'color':
      return isAdfColor;
    case 'href':
      return (v) => isHref(v, spec.schemes);
    case 'timestamp':
      return (v) => typeof v === 'string' && /^(?:0|[1-9][0-9]{0,14})$/.test(v) && Number(v) <= MAX_TIMESTAMP;
    case 'numberList': {
      const item = numberRule(spec, false);
      return (v) => Array.isArray(v) && v.length >= 1 && v.length <= 64 && v.every(item);
    }
    default:
      throw new Error(`adf-schema.json: unknown rule type ${spec.type}`);
  }
}

export function isRequired(rule) {
  return typeof rule === 'object' && rule.required === true;
}

export function lengthOf(rule) {
  return typeof rule === 'object' ? rule.lengthOf ?? null : null;
}
