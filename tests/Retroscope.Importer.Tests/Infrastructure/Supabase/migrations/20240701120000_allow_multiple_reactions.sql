-- Drop the old unique constraint if it exists, using the name from the error message.
ALTER TABLE public.poker_session_chat_message_reactions
DROP CONSTRAINT IF EXISTS unique_user_reaction_on_message;

-- Drop the old primary key if it exists.
ALTER TABLE public.poker_session_chat_message_reactions
DROP CONSTRAINT IF EXISTS poker_session_chat_message_reactions_pkey;

-- Create the new, correct composite primary key that allows multiple reactions.
ALTER TABLE public.poker_session_chat_message_reactions
ADD CONSTRAINT poker_session_chat_message_reactions_pkey
PRIMARY KEY (message_id, user_id, emoji);

-- This change allows a user to have multiple distinct emoji reactions on a single message. 