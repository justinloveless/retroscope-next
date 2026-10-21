// Session topic badges on the team poker tab, ported from the original's
// getSessionTopicSummary75Percent and parentBadgeClassName (tests/golden/src/topic-summary.ts).
// Only parent keys are stored (no Jira titles in Postgres), so a topic is its parent key; the label is
// the parent's summary resolved live through the Jira cache, or the bare key when Jira cannot answer.

export const TONE_COUNT = 12;
const UNSCOPED_TONE = '__unscoped_stories__';
const VARIOUS_TONE = '__various_topics__';
const NO_PARENT_LABEL = 'various topics';

function hash32(text) {
  let h = 0;
  for (let i = 0; i < text.length; i++) {
    h = (Math.imul(31, h) + text.charCodeAt(i)) | 0;
  }
  return Math.abs(h);
}

export function toneIndex(toneKey) {
  return hash32(toneKey) % TONE_COUNT;
}

/**
 * parentKeys: [{key: string|null, count}] over the session's rounds with a real ticket, in round
 * order (null: no parent). summaries: Map parent key → summary. Returns null (no real tickets),
 * {kind:'various', tone} or {kind:'badges', topics:[{key, label, tone}]}.
 */
export function topicSummary(parentKeys, summaries = new Map()) {
  const groups = new Map();
  for (const { key, count } of parentKeys) {
    const id = key?.trim() || null;
    groups.set(id, (groups.get(id) ?? 0) + count);
  }
  const total = [...groups.values()].reduce((sum, count) => sum + count, 0);
  if (total === 0) {
    return null;
  }

  const threshold = Math.ceil(0.75 * total);
  const ranked = [...groups].sort((a, b) => b[1] - a[1]);
  const covered = [];
  let cumulative = 0;
  for (const [key, count] of ranked) {
    cumulative += count;
    covered.push(key);
    if (cumulative >= threshold) {
      break;
    }
  }

  if (covered.length > 3) {
    return { kind: 'various', tone: toneIndex(VARIOUS_TONE) };
  }
  return {
    kind: 'badges',
    topics: covered.map((key) => ({
      key,
      label: key === null ? NO_PARENT_LABEL : summaries.get(key)?.trim() || key,
      tone: toneIndex(key ?? UNSCOPED_TONE),
    })),
  };
}
