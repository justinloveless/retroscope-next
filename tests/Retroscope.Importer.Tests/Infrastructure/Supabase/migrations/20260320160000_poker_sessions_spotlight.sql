-- Shared "spotlight" for pointing sessions: one user highlights one round for everyone.
ALTER TABLE public.poker_sessions
  ADD COLUMN IF NOT EXISTS spotlight_user_id uuid,
  ADD COLUMN IF NOT EXISTS spotlight_round_number int;

COMMENT ON COLUMN public.poker_sessions.spotlight_user_id IS 'User currently holding spotlight (nullable)';
COMMENT ON COLUMN public.poker_sessions.spotlight_round_number IS 'Round number highlighted for all participants when spotlight is active';

DO $$
BEGIN
  IF EXISTS (SELECT 1 FROM pg_publication WHERE pubname = 'supabase_realtime') THEN
    IF NOT EXISTS (
      SELECT 1 FROM pg_publication_tables
      WHERE pubname = 'supabase_realtime'
        AND schemaname = 'public'
        AND tablename = 'poker_sessions'
    ) THEN
      ALTER PUBLICATION supabase_realtime ADD TABLE public.poker_sessions;
    END IF;
  END IF;
END $$;
