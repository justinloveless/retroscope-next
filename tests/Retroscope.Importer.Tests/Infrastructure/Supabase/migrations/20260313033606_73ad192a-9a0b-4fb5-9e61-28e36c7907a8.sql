
-- Create organization roles enum
CREATE TYPE public.org_role AS ENUM ('owner', 'admin', 'member');

-- Organizations table
CREATE TABLE public.organizations (
  id UUID NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
  name TEXT NOT NULL,
  slug TEXT NOT NULL UNIQUE,
  description TEXT,
  owner_id UUID REFERENCES auth.users(id) ON DELETE SET NULL,
  created_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT now(),
  updated_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT now()
);

-- Organization members table
CREATE TABLE public.organization_members (
  id UUID NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
  organization_id UUID NOT NULL REFERENCES public.organizations(id) ON DELETE CASCADE,
  user_id UUID NOT NULL REFERENCES auth.users(id) ON DELETE CASCADE,
  role org_role NOT NULL DEFAULT 'member',
  joined_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT now(),
  UNIQUE(organization_id, user_id)
);

-- Organization invitations table
CREATE TABLE public.organization_invitations (
  id UUID NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
  organization_id UUID NOT NULL REFERENCES public.organizations(id) ON DELETE CASCADE,
  email TEXT NOT NULL,
  invited_by UUID NOT NULL REFERENCES auth.users(id),
  role org_role NOT NULL DEFAULT 'member',
  token TEXT NOT NULL DEFAULT gen_random_uuid()::text,
  status TEXT NOT NULL DEFAULT 'pending',
  is_active BOOLEAN DEFAULT true,
  created_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT now(),
  expires_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT (now() + interval '7 days')
);

-- Add organization_id to teams table
ALTER TABLE public.teams ADD COLUMN organization_id UUID REFERENCES public.organizations(id) ON DELETE SET NULL;

-- Enable RLS
ALTER TABLE public.organizations ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.organization_members ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.organization_invitations ENABLE ROW LEVEL SECURITY;

-- RLS Policies for organizations
CREATE POLICY "Org members can view their organization"
  ON public.organizations FOR SELECT TO authenticated
  USING (EXISTS (
    SELECT 1 FROM public.organization_members om
    WHERE om.organization_id = id AND om.user_id = auth.uid()
  ) OR EXISTS (
    SELECT 1 FROM public.profiles p WHERE p.id = auth.uid() AND p.role = 'admin'
  ));

CREATE POLICY "Authenticated users can create organizations"
  ON public.organizations FOR INSERT TO authenticated
  WITH CHECK (owner_id = auth.uid());

CREATE POLICY "Org admins and owners can update organization"
  ON public.organizations FOR UPDATE TO authenticated
  USING (EXISTS (
    SELECT 1 FROM public.organization_members om
    WHERE om.organization_id = id AND om.user_id = auth.uid() AND om.role IN ('owner', 'admin')
  ) OR EXISTS (
    SELECT 1 FROM public.profiles p WHERE p.id = auth.uid() AND p.role = 'admin'
  ));

CREATE POLICY "Only org owners can delete organization"
  ON public.organizations FOR DELETE TO authenticated
  USING (owner_id = auth.uid() OR EXISTS (
    SELECT 1 FROM public.profiles p WHERE p.id = auth.uid() AND p.role = 'admin'
  ));

-- RLS Policies for organization_members
CREATE POLICY "Org members can view other members"
  ON public.organization_members FOR SELECT TO authenticated
  USING (EXISTS (
    SELECT 1 FROM public.organization_members om
    WHERE om.organization_id = organization_members.organization_id AND om.user_id = auth.uid()
  ) OR EXISTS (
    SELECT 1 FROM public.profiles p WHERE p.id = auth.uid() AND p.role = 'admin'
  ));

CREATE POLICY "Org admins can add members"
  ON public.organization_members FOR INSERT TO authenticated
  WITH CHECK (EXISTS (
    SELECT 1 FROM public.organization_members om
    WHERE om.organization_id = organization_members.organization_id AND om.user_id = auth.uid() AND om.role IN ('owner', 'admin')
  ) OR EXISTS (
    SELECT 1 FROM public.profiles p WHERE p.id = auth.uid() AND p.role = 'admin'
  ));

CREATE POLICY "Org admins can update member roles"
  ON public.organization_members FOR UPDATE TO authenticated
  USING (EXISTS (
    SELECT 1 FROM public.organization_members om
    WHERE om.organization_id = organization_members.organization_id AND om.user_id = auth.uid() AND om.role IN ('owner', 'admin')
  ) OR EXISTS (
    SELECT 1 FROM public.profiles p WHERE p.id = auth.uid() AND p.role = 'admin'
  ));

CREATE POLICY "Org admins can remove members"
  ON public.organization_members FOR DELETE TO authenticated
  USING (EXISTS (
    SELECT 1 FROM public.organization_members om
    WHERE om.organization_id = organization_members.organization_id AND om.user_id = auth.uid() AND om.role IN ('owner', 'admin')
  ) OR user_id = auth.uid() OR EXISTS (
    SELECT 1 FROM public.profiles p WHERE p.id = auth.uid() AND p.role = 'admin'
  ));

-- RLS Policies for organization_invitations
CREATE POLICY "Org admins can view invitations"
  ON public.organization_invitations FOR SELECT TO authenticated
  USING (EXISTS (
    SELECT 1 FROM public.organization_members om
    WHERE om.organization_id = organization_invitations.organization_id AND om.user_id = auth.uid() AND om.role IN ('owner', 'admin')
  ) OR EXISTS (
    SELECT 1 FROM public.profiles p WHERE p.id = auth.uid() AND p.role = 'admin'
  ));

CREATE POLICY "Org admins can create invitations"
  ON public.organization_invitations FOR INSERT TO authenticated
  WITH CHECK (EXISTS (
    SELECT 1 FROM public.organization_members om
    WHERE om.organization_id = organization_invitations.organization_id AND om.user_id = auth.uid() AND om.role IN ('owner', 'admin')
  ) OR EXISTS (
    SELECT 1 FROM public.profiles p WHERE p.id = auth.uid() AND p.role = 'admin'
  ));

CREATE POLICY "Org admins can update invitations"
  ON public.organization_invitations FOR UPDATE TO authenticated
  USING (EXISTS (
    SELECT 1 FROM public.organization_members om
    WHERE om.organization_id = organization_invitations.organization_id AND om.user_id = auth.uid() AND om.role IN ('owner', 'admin')
  ) OR EXISTS (
    SELECT 1 FROM public.profiles p WHERE p.id = auth.uid() AND p.role = 'admin'
  ));

CREATE POLICY "Org admins can delete invitations"
  ON public.organization_invitations FOR DELETE TO authenticated
  USING (EXISTS (
    SELECT 1 FROM public.organization_members om
    WHERE om.organization_id = organization_invitations.organization_id AND om.user_id = auth.uid() AND om.role IN ('owner', 'admin')
  ) OR EXISTS (
    SELECT 1 FROM public.profiles p WHERE p.id = auth.uid() AND p.role = 'admin'
  ));

-- Function to accept org invitation
CREATE OR REPLACE FUNCTION public.accept_org_invitation(invitation_token text)
RETURNS json
LANGUAGE plpgsql
SECURITY DEFINER
AS $$
DECLARE
  inv_record public.organization_invitations;
  result json;
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
    'organization_name', (SELECT name FROM public.organizations WHERE id = inv_record.organization_id)
  );
END;
$$;

-- Trigger to add creator as owner when org is created
CREATE OR REPLACE FUNCTION public.handle_new_organization()
RETURNS trigger
LANGUAGE plpgsql
SECURITY DEFINER
AS $$
BEGIN
  INSERT INTO public.organization_members (organization_id, user_id, role)
  VALUES (NEW.id, NEW.owner_id, 'owner');
  RETURN NEW;
END;
$$;

CREATE TRIGGER on_organization_created
  AFTER INSERT ON public.organizations
  FOR EACH ROW EXECUTE FUNCTION public.handle_new_organization();

-- Helper function to check org membership
CREATE OR REPLACE FUNCTION public.is_org_member(org_id uuid, uid uuid)
RETURNS boolean
LANGUAGE sql
STABLE SECURITY DEFINER
SET search_path = public
AS $$
  SELECT EXISTS (
    SELECT 1 FROM public.organization_members
    WHERE organization_id = org_id AND user_id = uid
  );
$$;

-- Helper function to check org admin/owner
CREATE OR REPLACE FUNCTION public.is_org_admin(org_id uuid, uid uuid)
RETURNS boolean
LANGUAGE sql
STABLE SECURITY DEFINER
SET search_path = public
AS $$
  SELECT EXISTS (
    SELECT 1 FROM public.organization_members
    WHERE organization_id = org_id AND user_id = uid AND role IN ('owner', 'admin')
  );
$$;
