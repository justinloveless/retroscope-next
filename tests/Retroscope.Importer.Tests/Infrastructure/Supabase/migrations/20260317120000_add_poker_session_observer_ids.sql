-- Add observer_ids to poker_sessions for participants who observe (no playing slot, no hand per round)
ALTER TABLE public.poker_sessions
ADD COLUMN IF NOT EXISTS observer_ids UUID[] NOT NULL DEFAULT '{}';

COMMENT ON COLUMN public.poker_sessions.observer_ids IS 'User IDs who are observers: they can chat, use queue, see revealed cards, but do not play a hand or have a slot on the table';
