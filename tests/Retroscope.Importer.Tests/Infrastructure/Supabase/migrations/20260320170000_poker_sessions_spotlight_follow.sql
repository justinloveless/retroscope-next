-- When enabled, participants who are not the spotlight holder navigate to spotlight_round_number when it changes.
ALTER TABLE public.poker_sessions
  ADD COLUMN IF NOT EXISTS spotlight_follow_enabled boolean NOT NULL DEFAULT true;

COMMENT ON COLUMN public.poker_sessions.spotlight_follow_enabled IS 'When true, non-spotlight users follow the spotlight holder to the highlighted round.';
