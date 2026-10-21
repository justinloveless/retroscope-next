-- Enable full replica identity so Supabase Realtime can deliver
-- DELETE (and UPDATE) events through column filters like team_id=eq.xxx
ALTER TABLE public.poker_ticket_queue REPLICA IDENTITY FULL;
