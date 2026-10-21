-- Fresh rounds auto-reveal when everyone is locked/abstained; replay disables until manual reveal.
ALTER TABLE public.poker_session_rounds
  ADD COLUMN IF NOT EXISTS auto_reveal_enabled boolean NOT NULL DEFAULT true;

COMMENT ON COLUMN public.poker_session_rounds.auto_reveal_enabled IS
  'When true, reveal automatically once all participants are locked or abstained. Set false after replay so host must reveal manually.';
