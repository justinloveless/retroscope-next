// The sentences for the Jira proxy's problem codes (§f error table), shared by every screen that calls it.

const JIRA_ERRORS = {
  jira_auth_failed: 'Jira refused the team’s credentials. A team admin can update them in Team Settings.',
  jira_forbidden: 'The team’s Jira account may not do that.',
  jira_not_found: 'Jira could not find that issue.',
  jira_rate_limited: 'Jira is busy. Try again in a moment.',
  jira_unavailable: 'Jira is unavailable right now. Try again in a moment.',
  jira_not_configured: 'Jira is not set up for this team.',
  jira_pending: 'A submit for this round is still in progress.',
  stale: 'The issue changed in Jira meanwhile.',
  forbidden: 'You are not allowed to do that.',
  network: 'You are offline. Try again when the connection is back.',
};

/** A sentence for a failed Jira call: an ApiError, a nack ({code, detail}), a bare code, or anything thrown. */
export function jiraMessage(error) {
  const code = typeof error === 'string' ? error : error?.code;
  if (code === 'jira_validation' && error?.fields) {
    const first = Object.values(error.fields).find((v) => typeof v === 'string');
    if (first) {
      return first;
    }
  }
  return JIRA_ERRORS[code] ?? (error?.detail || 'Something went wrong with Jira.');
}
