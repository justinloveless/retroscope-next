
-- Add jira_board_id column to teams table
ALTER TABLE public.teams ADD COLUMN IF NOT EXISTS jira_board_id text;

-- Create poker_ticket_queue table
CREATE TABLE public.poker_ticket_queue (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  team_id uuid NOT NULL REFERENCES public.teams(id) ON DELETE CASCADE,
  ticket_key text NOT NULL,
  ticket_summary text,
  position integer NOT NULL DEFAULT 0,
  added_by uuid REFERENCES auth.users(id) ON DELETE SET NULL,
  created_at timestamptz NOT NULL DEFAULT now()
);

ALTER TABLE public.poker_ticket_queue ENABLE ROW LEVEL SECURITY;

-- Team members can view queue
CREATE POLICY "Team members can view queue"
  ON public.poker_ticket_queue FOR SELECT TO authenticated
  USING (is_team_member(team_id, auth.uid()));

-- Team members can manage queue
CREATE POLICY "Team members can manage queue"
  ON public.poker_ticket_queue FOR ALL TO authenticated
  USING (is_team_member(team_id, auth.uid()))
  WITH CHECK (is_team_member(team_id, auth.uid()));
