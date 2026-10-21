-- Realtime postgres_changes with filter `session_id=eq.<uuid>` requires OLD row columns
-- for DELETE. Default replica identity only logs the primary key (id), so DELETE events
-- never match the filter and clients never see round removals.
ALTER TABLE public.poker_session_rounds REPLICA IDENTITY FULL;
