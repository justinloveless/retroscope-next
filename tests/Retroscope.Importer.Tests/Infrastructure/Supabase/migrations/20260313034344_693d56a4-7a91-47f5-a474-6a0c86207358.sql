
-- Drop existing policies that cause infinite recursion
DROP POLICY IF EXISTS "Org admins can add members" ON public.organization_members;
DROP POLICY IF EXISTS "Org admins can remove members" ON public.organization_members;
DROP POLICY IF EXISTS "Org admins can update member roles" ON public.organization_members;
DROP POLICY IF EXISTS "Org members can view other members" ON public.organization_members;

-- Recreate using security definer functions to avoid recursion
CREATE POLICY "Org admins can add members" ON public.organization_members
  FOR INSERT TO authenticated
  WITH CHECK (
    is_org_admin(organization_id, auth.uid())
    OR (EXISTS (SELECT 1 FROM profiles p WHERE p.id = auth.uid() AND p.role = 'admin'))
  );

CREATE POLICY "Org admins can remove members" ON public.organization_members
  FOR DELETE TO authenticated
  USING (
    is_org_admin(organization_id, auth.uid())
    OR user_id = auth.uid()
    OR (EXISTS (SELECT 1 FROM profiles p WHERE p.id = auth.uid() AND p.role = 'admin'))
  );

CREATE POLICY "Org admins can update member roles" ON public.organization_members
  FOR UPDATE TO authenticated
  USING (
    is_org_admin(organization_id, auth.uid())
    OR (EXISTS (SELECT 1 FROM profiles p WHERE p.id = auth.uid() AND p.role = 'admin'))
  );

CREATE POLICY "Org members can view other members" ON public.organization_members
  FOR SELECT TO authenticated
  USING (
    is_org_member(organization_id, auth.uid())
    OR (EXISTS (SELECT 1 FROM profiles p WHERE p.id = auth.uid() AND p.role = 'admin'))
  );

-- Also fix organizations table policies that had wrong join (om.organization_id = om.id)
DROP POLICY IF EXISTS "Org admins and owners can update organization" ON public.organizations;
DROP POLICY IF EXISTS "Org members can view their organization" ON public.organizations;

CREATE POLICY "Org admins and owners can update organization" ON public.organizations
  FOR UPDATE TO authenticated
  USING (
    is_org_admin(id, auth.uid())
    OR (EXISTS (SELECT 1 FROM profiles p WHERE p.id = auth.uid() AND p.role = 'admin'))
  );

CREATE POLICY "Org members can view their organization" ON public.organizations
  FOR SELECT TO authenticated
  USING (
    is_org_member(id, auth.uid())
    OR (EXISTS (SELECT 1 FROM profiles p WHERE p.id = auth.uid() AND p.role = 'admin'))
  );
