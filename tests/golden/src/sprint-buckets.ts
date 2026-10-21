// Verbatim spans of the original app; make-golden.mjs checks each one against its recorded sha256.

// golden-span src/lib/jiraBrowseSprintBuckets.ts 1-61 7f48a79 sha256:4cbffa9a4e23f5565bf8deb72bcdc3a411422281a6b191e206aef53b0a6c35f7
/** Issues with no sprint or the API placeholder label are grouped as backlog (always shown last). */
export function isJiraBrowseBacklogSprint(sprint: string | null | undefined): boolean {
  return sprint == null || sprint === '' || sprint === 'Backlog';
}

export type IssueForSprintGrouping = {
  sprint: string | null;
  sprintStartDate?: string | null;
};

/** Minimal fields for sprint bucketing. Order within each backlog/sprint bucket matches input (Jira rank). */
export type IssueForSprintBucket = IssueForSprintGrouping & {
  key: string;
};

export function buildJiraBrowseIssuesBySprint<T extends IssueForSprintBucket>(issues: T[]): {
  backlog: T[];
  sprintBuckets: Array<{ name: string; issues: T[] }>;
} {
  const backlog: T[] = [];
  const groups = new Map<string, T[]>();
  for (const issue of issues) {
    if (isJiraBrowseBacklogSprint(issue.sprint)) {
      backlog.push(issue);
      continue;
    }
    const bucket = issue.sprint as string;
    const list = groups.get(bucket) || [];
    list.push(issue);
    groups.set(bucket, list);
  }

  const sprintBuckets = Array.from(groups.entries()).map(([name, iss]) => {
    const startDate = iss.find((i) => i.sprintStartDate)?.sprintStartDate ?? null;
    return { name, issues: iss, startDate };
  });

  sprintBuckets.sort((a, b) => {
    const aHas = a.startDate != null && a.startDate !== '';
    const bHas = b.startDate != null && b.startDate !== '';
    if (aHas && bHas) {
      const ta = new Date(a.startDate!).getTime();
      const tb = new Date(b.startDate!).getTime();
      const aOk = !Number.isNaN(ta);
      const bOk = !Number.isNaN(tb);
      if (aOk && bOk && ta !== tb) return ta - tb;
      if (aOk !== bOk) return aOk ? -1 : 1;
    } else if (aHas !== bHas) {
      return aHas ? -1 : 1;
    }
    return a.name.localeCompare(b.name, undefined, { numeric: true });
  });

  return {
    backlog,
    sprintBuckets: sprintBuckets.map(({ name, issues: bucketIssues }) => ({
      name,
      issues: bucketIssues,
    })),
  };
}
// golden-span-end
