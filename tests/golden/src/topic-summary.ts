// Verbatim spans of the original app; make-golden.mjs checks each one against its recorded sha256.

// golden-span src/lib/pokerRoundTicketPlaceholder.ts 6-11 7f48a79 sha256:dcca57a657674439674dfbeaeccd8d1db247992a5f79c017366d9e27afe98bde
export function isSyntheticRoundTicket(ticket: string | null | undefined): boolean {
  if (ticket == null) return true;
  const t = String(ticket).trim();
  if (t === '') return true;
  return /^round:\d+$/i.test(t);
}
// golden-span-end

// golden-span src/lib/parentBadgeTone.ts 4-30 7f48a79 sha256:80528d85bc7fcda2b66e51cf3b38bdcaedc0dc6c02088016a51ca24c1a695a9f
const TONES = [
  'bg-rose-100 text-rose-900 border-rose-200/90 dark:bg-rose-950/45 dark:text-rose-100 dark:border-rose-800/70',
  'bg-orange-100 text-orange-900 border-orange-200/90 dark:bg-orange-950/45 dark:text-orange-100 dark:border-orange-800/70',
  'bg-amber-100 text-amber-950 border-amber-200/90 dark:bg-amber-950/45 dark:text-amber-100 dark:border-amber-800/70',
  'bg-lime-100 text-lime-950 border-lime-200/90 dark:bg-lime-950/40 dark:text-lime-100 dark:border-lime-800/70',
  'bg-emerald-100 text-emerald-900 border-emerald-200/90 dark:bg-emerald-950/45 dark:text-emerald-100 dark:border-emerald-800/70',
  'bg-cyan-100 text-cyan-900 border-cyan-200/90 dark:bg-cyan-950/45 dark:text-cyan-100 dark:border-cyan-800/70',
  'bg-sky-100 text-sky-900 border-sky-200/90 dark:bg-sky-950/45 dark:text-sky-100 dark:border-sky-800/70',
  'bg-indigo-100 text-indigo-900 border-indigo-200/90 dark:bg-indigo-950/45 dark:text-indigo-100 dark:border-indigo-800/70',
  'bg-violet-100 text-violet-900 border-violet-200/90 dark:bg-violet-950/45 dark:text-violet-100 dark:border-violet-800/70',
  'bg-fuchsia-100 text-fuchsia-900 border-fuchsia-200/90 dark:bg-fuchsia-950/45 dark:text-fuchsia-100 dark:border-fuchsia-800/70',
  'bg-pink-100 text-pink-900 border-pink-200/90 dark:bg-pink-950/45 dark:text-pink-100 dark:border-pink-800/70',
  'bg-teal-100 text-teal-900 border-teal-200/90 dark:bg-teal-950/45 dark:text-teal-100 dark:border-teal-800/70',
] as const;

function hash32(str: string): number {
  let h = 0;
  for (let i = 0; i < str.length; i++) {
    h = (Math.imul(31, h) + str.charCodeAt(i)) | 0;
  }
  return Math.abs(h);
}

/** Stable classes for a parent issue key (siblings share the same badge colors). */
export function parentBadgeClassName(parentKey: string): string {
  return TONES[hash32(parentKey) % TONES.length];
}
// golden-span-end

// golden-span src/lib/pokerSessionListStats.ts 3-105 7f48a79 sha256:f5f433afc437f57958e09458c0bc14eaf632bc5194c673af4b5528b3272fbfa2
export type PokerRoundForListStats = {
  game_state: string;
  ticket_number: string | null;
  ticket_parent_key: string | null;
  ticket_parent_summary: string | null;
};

/** Rounds where the team finished pointing a real Jira story (revealed hand, non-placeholder ticket). */
export function countPointedStories(rounds: PokerRoundForListStats[]): number {
  return rounds.filter(
    (r) => r.game_state === 'Playing' && !isSyntheticRoundTicket(r.ticket_number)
  ).length;
}

export const SESSION_TOPIC_UNSCOPED_TONE_KEY = '__no_parent__';
const NO_PARENT_TOPIC_ID = SESSION_TOPIC_UNSCOPED_TONE_KEY;
const NO_PARENT_LABEL = 'various topics';

export type SessionTopicBadge = {
  label: string;
  /** Prefer Jira parent key for `parentBadgeClassName`; may be `NO_PARENT_TOPIC_ID`. */
  toneKey: string;
};

export type SessionTopicSummaryResult =
  | { kind: 'badges'; topics: SessionTopicBadge[] }
  | { kind: 'various' };

/** Maps stored tone key to the string hashed by `parentBadgeClassName` (matches Jira browser parent badges). */
export function sessionTopicBadgeToneForParentClass(toneKey: string): string {
  return toneKey === SESSION_TOPIC_UNSCOPED_TONE_KEY ? '__unscoped_stories__' : toneKey;
}

function topicIdAndLabel(r: PokerRoundForListStats): { id: string; label: string } {
  const hasParent = !!(r.ticket_parent_key?.trim() || r.ticket_parent_summary?.trim());
  if (!hasParent) {
    return { id: NO_PARENT_TOPIC_ID, label: NO_PARENT_LABEL };
  }
  const id = r.ticket_parent_key?.trim() || r.ticket_parent_summary?.trim() || '';
  const s = r.ticket_parent_summary?.trim();
  const k = r.ticket_parent_key?.trim();
  const label = (s || k || id) || 'Unknown';
  return { id: id || label, label };
}

/** Prefer parent issue key for stable colors; fall back to summary; then previous bucket tone. */
function mergeParentTone(prev: string | undefined, r: PokerRoundForListStats): string {
  const pk = r.ticket_parent_key?.trim();
  if (pk) return pk;
  if (prev && prev !== NO_PARENT_TOPIC_ID) return prev;
  const ps = r.ticket_parent_summary?.trim();
  if (ps) return ps;
  return prev ?? NO_PARENT_TOPIC_ID;
}

/**
 * Session topic summary: every round with a real Jira key (not `round:N`), including not yet pointed.
 * Ranks topics by story count; smallest top-heavy set covering ≥75% of those rounds. More than three
 * topics → `various`. Use `sessionTopicBadgeToneForParentClass` + `parentBadgeClassName` for UI.
 */
export function getSessionTopicSummary75Percent(
  rounds: PokerRoundForListStats[]
): SessionTopicSummaryResult | null {
  const withRealTicket = rounds.filter((r) => !isSyntheticRoundTicket(r.ticket_number));
  if (withRealTicket.length === 0) return null;

  const n = withRealTicket.length;
  const threshold = Math.ceil(0.75 * n);
  const counts = new Map<string, { count: number; label: string; toneKey: string }>();

  for (const r of withRealTicket) {
    const { id, label } = topicIdAndLabel(r);
    const prev = counts.get(id);
    const toneKey = mergeParentTone(prev?.toneKey, r);
    counts.set(id, {
      count: (prev?.count ?? 0) + 1,
      label,
      toneKey,
    });
  }

  const sorted = Array.from(counts.entries())
    .map(([topicId, v]) => ({ topicId, ...v }))
    .sort((a, b) => b.count - a.count);

  let cumulative = 0;
  const covered: SessionTopicBadge[] = [];
  const seen = new Set<string>();

  for (const row of sorted) {
    cumulative += row.count;
    if (!seen.has(row.topicId)) {
      seen.add(row.topicId);
      covered.push({ label: row.label, toneKey: row.toneKey });
    }
    if (cumulative >= threshold) break;
  }

  if (covered.length > 3) {
    return { kind: 'various' };
  }
  return { kind: 'badges', topics: covered };
}
// golden-span-end

export { TONES };
