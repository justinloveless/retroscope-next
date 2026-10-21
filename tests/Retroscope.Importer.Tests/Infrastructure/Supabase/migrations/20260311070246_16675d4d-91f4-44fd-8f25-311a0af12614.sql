-- Update the INSERT policy on endorsements to allow admins to insert on behalf of others (impersonation)
DROP POLICY IF EXISTS "Team members can give endorsements" ON public.endorsements;
CREATE POLICY "Team members can give endorsements"
ON public.endorsements
FOR INSERT
TO authenticated
WITH CHECK (
  (is_team_member(team_id, auth.uid()) AND (from_user_id = auth.uid()))
  OR
  (EXISTS (SELECT 1 FROM profiles p WHERE p.id = auth.uid() AND p.role = 'admin'))
);

-- Also update DELETE policy to allow admins to revoke on behalf of impersonated users
DROP POLICY IF EXISTS "Users can delete their own endorsements" ON public.endorsements;
CREATE POLICY "Users can delete their own endorsements"
ON public.endorsements
FOR DELETE
TO authenticated
USING (
  (from_user_id = auth.uid())
  OR
  (EXISTS (SELECT 1 FROM profiles p WHERE p.id = auth.uid() AND p.role = 'admin'))
);