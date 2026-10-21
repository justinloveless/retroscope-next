-- Spotlight is scoped to a browser tab/window via a client id so one account cannot hold spotlight in two windows at once.
ALTER TABLE public.poker_sessions
  ADD COLUMN IF NOT EXISTS spotlight_client_id text;

COMMENT ON COLUMN public.poker_sessions.spotlight_client_id IS 'Opaque id for the browser tab/window holding spotlight (paired with spotlight_user_id).';
