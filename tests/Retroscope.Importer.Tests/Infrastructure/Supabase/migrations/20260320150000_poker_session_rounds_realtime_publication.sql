-- Deliver postgres_changes for poker_session_rounds so clients refetch rounds when
-- selections / lock state change (broadcast alone was masked by stale history in the UI).
DO $$
BEGIN
  IF EXISTS (SELECT 1 FROM pg_publication WHERE pubname = 'supabase_realtime') THEN
    IF NOT EXISTS (
      SELECT 1 FROM pg_publication_tables
      WHERE pubname = 'supabase_realtime'
        AND schemaname = 'public'
        AND tablename = 'poker_session_rounds'
    ) THEN
      ALTER PUBLICATION supabase_realtime ADD TABLE public.poker_session_rounds;
    END IF;
  END IF;
END $$;
