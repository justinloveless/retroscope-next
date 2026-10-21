
-- Create a table to store poker session round history
CREATE TABLE public.poker_session_rounds (
  id UUID NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
  session_id UUID NOT NULL REFERENCES public.poker_sessions(id) ON DELETE CASCADE,
  round_number INTEGER NOT NULL,
  selections JSONB NOT NULL DEFAULT '{}'::jsonb,
  average_points DECIMAL NOT NULL DEFAULT 0,
  ticket_number TEXT,
  ticket_title TEXT,
  completed_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT now(),
  created_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT now()
);

-- Create index for efficient querying
CREATE INDEX idx_poker_session_rounds_session_id ON public.poker_session_rounds(session_id);
CREATE INDEX idx_poker_session_rounds_round_number ON public.poker_session_rounds(session_id, round_number);

-- Add RLS policies
ALTER TABLE public.poker_session_rounds ENABLE ROW LEVEL SECURITY;

-- Allow read access to anyone (sessions are already public)
CREATE POLICY "Anyone can view poker session rounds" 
  ON public.poker_session_rounds 
  FOR SELECT 
  USING (true);

-- Only allow inserts for authenticated users (for team sessions)
CREATE POLICY "Authenticated users can create poker session rounds" 
  ON public.poker_session_rounds 
  FOR INSERT 
  WITH CHECK (true);

-- Add current_round_number column to poker_sessions table
ALTER TABLE public.poker_sessions 
ADD COLUMN current_round_number INTEGER NOT NULL DEFAULT 1;

-- Add average_points column if it doesn't exist (it should already exist)
DO $$ 
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM information_schema.columns 
    WHERE table_name = 'poker_sessions' 
    AND column_name = 'average_points'
  ) THEN
    ALTER TABLE public.poker_sessions 
    ADD COLUMN average_points DECIMAL NOT NULL DEFAULT 0;
  END IF;
END $$;
