-- Restrict team poker sessions so only team members can participate.
-- Non-team (anonymous/quick) sessions remain open to anyone.

-- 1. poker_sessions: enable RLS and add team-scoped policies
ALTER TABLE public.poker_sessions ENABLE ROW LEVEL SECURITY;

-- Anonymous sessions (no team_id) are accessible to anyone.
-- Team sessions are only accessible to members of that team.
CREATE POLICY "Anyone can view non-team poker sessions"
  ON public.poker_sessions FOR SELECT
  USING (team_id IS NULL);

CREATE POLICY "Team members can view their team poker sessions"
  ON public.poker_sessions FOR SELECT TO authenticated
  USING (team_id IS NOT NULL AND is_team_member(team_id, auth.uid()));

CREATE POLICY "Anyone can create non-team poker sessions"
  ON public.poker_sessions FOR INSERT
  WITH CHECK (team_id IS NULL);

CREATE POLICY "Team members can create team poker sessions"
  ON public.poker_sessions FOR INSERT TO authenticated
  WITH CHECK (team_id IS NOT NULL AND is_team_member(team_id, auth.uid()));

CREATE POLICY "Anyone can update non-team poker sessions"
  ON public.poker_sessions FOR UPDATE
  USING (team_id IS NULL);

CREATE POLICY "Team members can update their team poker sessions"
  ON public.poker_sessions FOR UPDATE TO authenticated
  USING (team_id IS NOT NULL AND is_team_member(team_id, auth.uid()));

-- 2. poker_session_rounds: replace permissive policies with team-scoped ones
DROP POLICY IF EXISTS "Anyone can view poker session rounds" ON public.poker_session_rounds;
DROP POLICY IF EXISTS "Authenticated users can create poker session rounds" ON public.poker_session_rounds;

CREATE POLICY "Select poker session rounds"
  ON public.poker_session_rounds FOR SELECT
  USING (
    EXISTS (
      SELECT 1 FROM public.poker_sessions ps
      WHERE ps.id = session_id
        AND (ps.team_id IS NULL OR is_team_member(ps.team_id, auth.uid()))
    )
  );

CREATE POLICY "Insert poker session rounds"
  ON public.poker_session_rounds FOR INSERT
  WITH CHECK (
    EXISTS (
      SELECT 1 FROM public.poker_sessions ps
      WHERE ps.id = session_id
        AND (ps.team_id IS NULL OR is_team_member(ps.team_id, auth.uid()))
    )
  );

CREATE POLICY "Update poker session rounds"
  ON public.poker_session_rounds FOR UPDATE
  USING (
    EXISTS (
      SELECT 1 FROM public.poker_sessions ps
      WHERE ps.id = session_id
        AND (ps.team_id IS NULL OR is_team_member(ps.team_id, auth.uid()))
    )
  );

-- 3. poker_session_chat: replace permissive policies with team-scoped ones
DROP POLICY IF EXISTS "Anyone can view poker session chat" ON public.poker_session_chat;
DROP POLICY IF EXISTS "Anyone can create poker session chat messages" ON public.poker_session_chat;

CREATE POLICY "Select poker session chat"
  ON public.poker_session_chat FOR SELECT
  USING (
    EXISTS (
      SELECT 1 FROM public.poker_sessions ps
      WHERE ps.id = session_id
        AND (ps.team_id IS NULL OR is_team_member(ps.team_id, auth.uid()))
    )
  );

CREATE POLICY "Insert poker session chat"
  ON public.poker_session_chat FOR INSERT
  WITH CHECK (
    EXISTS (
      SELECT 1 FROM public.poker_sessions ps
      WHERE ps.id = session_id
        AND (ps.team_id IS NULL OR is_team_member(ps.team_id, auth.uid()))
    )
  );
