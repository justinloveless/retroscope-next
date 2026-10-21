
-- Create a table to store poker session chat messages
CREATE TABLE public.poker_session_chat (
  id UUID NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
  session_id UUID NOT NULL REFERENCES public.poker_sessions(id) ON DELETE CASCADE,
  round_number INTEGER NOT NULL,
  user_id UUID REFERENCES auth.users(id),
  user_name TEXT NOT NULL,
  message TEXT NOT NULL,
  created_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT now()
);

-- Create indexes for efficient querying
CREATE INDEX idx_poker_session_chat_session_round ON public.poker_session_chat(session_id, round_number);
CREATE INDEX idx_poker_session_chat_created_at ON public.poker_session_chat(created_at);

-- Add RLS policies
ALTER TABLE public.poker_session_chat ENABLE ROW LEVEL SECURITY;

-- Allow read access to anyone (sessions are already public)
CREATE POLICY "Anyone can view poker session chat" 
  ON public.poker_session_chat 
  FOR SELECT 
  USING (true);

-- Allow inserts for authenticated and anonymous users
CREATE POLICY "Anyone can create poker session chat messages" 
  ON public.poker_session_chat 
  FOR INSERT 
  WITH CHECK (true);
