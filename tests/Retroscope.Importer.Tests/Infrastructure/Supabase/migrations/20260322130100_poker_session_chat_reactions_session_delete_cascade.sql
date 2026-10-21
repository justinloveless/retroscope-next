-- Deleting a poker session failed with:
-- null value in column "session_id" of relation "poker_session_chat_message_reactions"
-- violates not-null constraint
-- The FK on session_id used ON DELETE SET NULL while session_id is NOT NULL.
-- Cascade-delete reaction rows when the session is removed (same as poker_session_chat).

ALTER TABLE public.poker_session_chat_message_reactions
  DROP CONSTRAINT IF EXISTS fk_session;

ALTER TABLE public.poker_session_chat_message_reactions
  DROP CONSTRAINT IF EXISTS poker_session_chat_message_reactions_session_id_fkey;

ALTER TABLE public.poker_session_chat_message_reactions
  ADD CONSTRAINT poker_session_chat_message_reactions_session_id_fkey
  FOREIGN KEY (session_id)
  REFERENCES public.poker_sessions(id)
  ON DELETE CASCADE;
