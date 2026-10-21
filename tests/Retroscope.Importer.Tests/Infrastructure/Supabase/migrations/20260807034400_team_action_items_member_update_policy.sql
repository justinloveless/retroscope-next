-- Allow any team member to update action items on their team.
-- Previous policy only allowed the current assignee, team admins/owners, or global admins,
-- which blocked assigning unassigned items (assigned_to IS NULL) for normal members.

DROP POLICY IF EXISTS "Users can update action items assigned to them or team admins can update all"
  ON public.team_action_items;

CREATE POLICY "Team members can update team action items"
ON public.team_action_items
FOR UPDATE
USING (
  is_team_member(team_id, auth.uid())
  OR EXISTS (
    SELECT 1
    FROM public.profiles p
    WHERE p.id = auth.uid()
      AND p.role = 'admin'
  )
)
WITH CHECK (
  is_team_member(team_id, auth.uid())
  OR EXISTS (
    SELECT 1
    FROM public.profiles p
    WHERE p.id = auth.uid()
      AND p.role = 'admin'
  )
);
