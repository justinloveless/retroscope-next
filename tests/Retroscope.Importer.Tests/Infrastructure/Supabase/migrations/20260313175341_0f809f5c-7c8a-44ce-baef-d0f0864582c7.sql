
-- Table for org team invite codes
CREATE TABLE public.org_team_invite_codes (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  organization_id uuid NOT NULL REFERENCES public.organizations(id) ON DELETE CASCADE,
  code text NOT NULL DEFAULT (gen_random_uuid())::text,
  created_by uuid NOT NULL,
  is_active boolean NOT NULL DEFAULT true,
  created_at timestamptz NOT NULL DEFAULT now(),
  expires_at timestamptz NOT NULL DEFAULT (now() + interval '7 days'),
  UNIQUE(code)
);

ALTER TABLE public.org_team_invite_codes ENABLE ROW LEVEL SECURITY;

-- Org admins/owners can manage invite codes
CREATE POLICY "Org admins can manage team invite codes"
  ON public.org_team_invite_codes
  FOR ALL
  TO authenticated
  USING (is_org_admin(organization_id, auth.uid()))
  WITH CHECK (is_org_admin(organization_id, auth.uid()));

-- Anyone authenticated can read a code (needed to accept it)
CREATE POLICY "Authenticated users can read invite codes"
  ON public.org_team_invite_codes
  FOR SELECT
  TO authenticated
  USING (true);
