-- Add DELETE policy for poker_session_rounds so team admins can delete rounds
CREATE POLICY "Team admins can delete poker session rounds"
ON public.poker_session_rounds
FOR DELETE
USING (
  EXISTS (
    SELECT 1
    FROM poker_sessions ps
    WHERE ps.id = poker_session_rounds.session_id
    AND (
      (ps.team_id IS NULL)
      OR is_team_admin_or_owner(ps.team_id, auth.uid())
    )
  )
);