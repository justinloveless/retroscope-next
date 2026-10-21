-- Allow team admins (and app admins) to delete team poker sessions.
-- poker_session_chat needs a DELETE policy so CASCADE from poker_sessions succeeds under RLS.

CREATE POLICY "Team admins can delete team poker sessions"
  ON public.poker_sessions FOR DELETE TO authenticated
  USING (
    team_id IS NOT NULL
    AND (
      is_team_admin_or_owner(team_id, auth.uid())
      OR EXISTS (
        SELECT 1 FROM public.profiles p
        WHERE p.id = auth.uid() AND p.role = 'admin'
      )
    )
  );

CREATE POLICY "Team admins can delete poker session chat"
  ON public.poker_session_chat FOR DELETE TO authenticated
  USING (
    EXISTS (
      SELECT 1 FROM public.poker_sessions ps
      WHERE ps.id = poker_session_chat.session_id
        AND ps.team_id IS NOT NULL
        AND (
          is_team_admin_or_owner(ps.team_id, auth.uid())
          OR EXISTS (
            SELECT 1 FROM public.profiles p
            WHERE p.id = auth.uid() AND p.role = 'admin'
          )
        )
    )
  );
