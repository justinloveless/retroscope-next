-- Lock down organization visibility:
-- - Only org members (or global admins via existing policies) can read org rows
-- - Invite flows use SECURITY DEFINER RPCs instead of broad SELECT policies

-- Remove overly-broad org read policy
DROP POLICY IF EXISTS "Authenticated users can read org name" ON public.organizations;

-- Remove overly-broad invite-code read policy (prevents listing all invite codes/org ids)
DROP POLICY IF EXISTS "Authenticated users can read invite codes" ON public.org_team_invite_codes;

-- RPC: fetch org/team invite details by code (used by JoinOrg page)
CREATE OR REPLACE FUNCTION public.get_org_team_invite(invite_code text)
RETURNS json
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = public
AS $$
DECLARE
  inv public.org_team_invite_codes;
  org_name text;
BEGIN
  SELECT * INTO inv
  FROM public.org_team_invite_codes
  WHERE code = invite_code
    AND is_active = true
    AND expires_at > now();

  IF inv.id IS NULL THEN
    RETURN json_build_object('success', false, 'error', 'Invalid or expired invite code.');
  END IF;

  SELECT o.name INTO org_name
  FROM public.organizations o
  WHERE o.id = inv.organization_id;

  RETURN json_build_object(
    'success', true,
    'id', inv.id,
    'organization_id', inv.organization_id,
    'organization_name', COALESCE(org_name, 'Unknown Organization'),
    'expires_at', inv.expires_at
  );
END;
$$;

-- Update: include slug in accept_org_invitation result to avoid a follow-up organizations SELECT
CREATE OR REPLACE FUNCTION public.accept_org_invitation(invitation_token text)
RETURNS json
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = public
AS $$
DECLARE
  inv_record public.organization_invitations;
BEGIN
  SELECT * INTO inv_record
  FROM public.organization_invitations
  WHERE token = invitation_token
    AND status = 'pending'
    AND is_active = true
    AND expires_at > now();

  IF inv_record.id IS NULL THEN
    RETURN json_build_object('success', false, 'error', 'Invalid or expired invitation');
  END IF;

  IF EXISTS (
    SELECT 1 FROM public.organization_members
    WHERE organization_id = inv_record.organization_id AND user_id = auth.uid()
  ) THEN
    RETURN json_build_object('success', false, 'error', 'Already an organization member');
  END IF;

  INSERT INTO public.organization_members (organization_id, user_id, role)
  VALUES (inv_record.organization_id, auth.uid(), inv_record.role);

  UPDATE public.organization_invitations
  SET status = 'accepted'
  WHERE id = inv_record.id;

  RETURN json_build_object(
    'success', true,
    'organization_id', inv_record.organization_id,
    'organization_name', (SELECT name FROM public.organizations WHERE id = inv_record.organization_id),
    'organization_slug', (SELECT slug FROM public.organizations WHERE id = inv_record.organization_id)
  );
END;
$$;

