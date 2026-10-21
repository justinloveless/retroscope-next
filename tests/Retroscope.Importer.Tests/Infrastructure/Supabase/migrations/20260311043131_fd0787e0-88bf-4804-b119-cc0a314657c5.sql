
-- Create endorsement_types table
CREATE TABLE public.endorsement_types (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  team_id uuid NOT NULL REFERENCES public.teams(id) ON DELETE CASCADE,
  name text NOT NULL,
  description text NOT NULL DEFAULT '',
  icon_url text,
  position integer NOT NULL DEFAULT 0,
  is_default boolean NOT NULL DEFAULT false,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now()
);

-- Create endorsement_settings table
CREATE TABLE public.endorsement_settings (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  team_id uuid NOT NULL REFERENCES public.teams(id) ON DELETE CASCADE UNIQUE,
  max_endorsements_per_user_per_board integer NOT NULL DEFAULT 3,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now()
);

-- Create endorsements table
CREATE TABLE public.endorsements (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  board_id uuid NOT NULL REFERENCES public.retro_boards(id) ON DELETE CASCADE,
  team_id uuid NOT NULL REFERENCES public.teams(id) ON DELETE CASCADE,
  endorsement_type_id uuid NOT NULL REFERENCES public.endorsement_types(id) ON DELETE CASCADE,
  from_user_id uuid NOT NULL,
  to_user_id uuid NOT NULL,
  created_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE(board_id, from_user_id, to_user_id, endorsement_type_id)
);

-- Enable RLS
ALTER TABLE public.endorsement_types ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.endorsement_settings ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.endorsements ENABLE ROW LEVEL SECURITY;

-- RLS for endorsement_types: team members can view, admins can manage
CREATE POLICY "Team members can view endorsement types"
  ON public.endorsement_types FOR SELECT
  TO public
  USING (is_team_member(team_id, auth.uid()));

CREATE POLICY "Team admins can manage endorsement types"
  ON public.endorsement_types FOR ALL
  TO authenticated
  USING (is_team_admin_or_owner(team_id, auth.uid()))
  WITH CHECK (is_team_admin_or_owner(team_id, auth.uid()));

-- RLS for endorsement_settings: team members can view, admins can manage
CREATE POLICY "Team members can view endorsement settings"
  ON public.endorsement_settings FOR SELECT
  TO public
  USING (is_team_member(team_id, auth.uid()));

CREATE POLICY "Team admins can manage endorsement settings"
  ON public.endorsement_settings FOR ALL
  TO authenticated
  USING (is_team_admin_or_owner(team_id, auth.uid()))
  WITH CHECK (is_team_admin_or_owner(team_id, auth.uid()));

-- RLS for endorsements: team members can view and insert
CREATE POLICY "Team members can view endorsements"
  ON public.endorsements FOR SELECT
  TO authenticated
  USING (is_team_member(team_id, auth.uid()));

CREATE POLICY "Team members can give endorsements"
  ON public.endorsements FOR INSERT
  TO authenticated
  WITH CHECK (is_team_member(team_id, auth.uid()) AND from_user_id = auth.uid());

CREATE POLICY "Users can delete their own endorsements"
  ON public.endorsements FOR DELETE
  TO authenticated
  USING (from_user_id = auth.uid());

-- Trigger for updated_at on endorsement_types
CREATE TRIGGER set_endorsement_types_updated_at
  BEFORE UPDATE ON public.endorsement_types
  FOR EACH ROW EXECUTE FUNCTION public.trigger_set_timestamp();

-- Trigger for updated_at on endorsement_settings
CREATE TRIGGER set_endorsement_settings_updated_at
  BEFORE UPDATE ON public.endorsement_settings
  FOR EACH ROW EXECUTE FUNCTION public.trigger_set_timestamp();

-- Function to seed default endorsement types for a team
CREATE OR REPLACE FUNCTION public.seed_default_endorsement_types(p_team_id uuid)
RETURNS void
LANGUAGE plpgsql
SECURITY DEFINER
AS $$
BEGIN
  -- Only seed if team has no endorsement types yet
  IF NOT EXISTS (SELECT 1 FROM public.endorsement_types WHERE team_id = p_team_id) THEN
    INSERT INTO public.endorsement_types (team_id, name, description, icon_url, position, is_default) VALUES
      (p_team_id, 'Problem Solver', 'Tackles tough technical challenges head-on', '🧩', 1, true),
      (p_team_id, 'Team Player', 'Goes above and beyond to help teammates', '🤝', 2, true),
      (p_team_id, 'Innovator', 'Brings creative ideas and fresh perspectives', '💡', 3, true);
  END IF;
  
  -- Seed default settings if not exists
  INSERT INTO public.endorsement_settings (team_id)
  VALUES (p_team_id)
  ON CONFLICT (team_id) DO NOTHING;
END;
$$;

-- Update handle_new_team to also seed endorsement defaults
CREATE OR REPLACE FUNCTION public.handle_new_team()
RETURNS trigger
LANGUAGE plpgsql
SECURITY DEFINER
AS $$
BEGIN
  -- Add creator as team owner
  INSERT INTO public.team_members (team_id, user_id, role) 
  VALUES (NEW.id, NEW.creator_id, 'owner');
  
  -- Create default settings for the team
  INSERT INTO public.team_default_settings (team_id) 
  VALUES (NEW.id);
  
  -- Seed default endorsement types and settings
  PERFORM public.seed_default_endorsement_types(NEW.id);
  
  RETURN NEW;
END;
$$;

-- Enable realtime for endorsements table
ALTER PUBLICATION supabase_realtime ADD TABLE public.endorsements;
