-- Spotlight Jira browse preview: round exists before "Add to rounds" commit; must not count as pointing/pointed in Browse Jira filters.
ALTER TABLE public.poker_session_rounds
ADD COLUMN IF NOT EXISTS is_pending_round boolean NOT NULL DEFAULT false;

COMMENT ON COLUMN public.poker_session_rounds.is_pending_round IS 'True while spotlight holder is previewing a ticket from Jira browse; cleared when they commit or the tentative round is deleted.';
