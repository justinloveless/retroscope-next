-- Jira parent issue (often an Epic) for analytics on the team poker tab.
ALTER TABLE public.poker_session_rounds
  ADD COLUMN IF NOT EXISTS ticket_parent_key text,
  ADD COLUMN IF NOT EXISTS ticket_parent_summary text;

COMMENT ON COLUMN public.poker_session_rounds.ticket_parent_key IS 'Jira parent issue key when the pointed issue is under an epic (fields.parent).';
COMMENT ON COLUMN public.poker_session_rounds.ticket_parent_summary IS 'Jira parent summary/title for display.';
