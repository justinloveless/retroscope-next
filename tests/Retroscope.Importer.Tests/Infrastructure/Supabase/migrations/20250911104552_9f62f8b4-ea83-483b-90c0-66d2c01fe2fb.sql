-- Enable Row Level Security on team_action_items table
ALTER TABLE public.team_action_items ENABLE ROW LEVEL SECURITY;

-- Allow team members to view action items for their teams
CREATE POLICY "Team members can view team action items" 
ON public.team_action_items 
FOR SELECT 
USING (
  team_id IN (
    SELECT tm.team_id 
    FROM public.team_members tm 
    WHERE tm.user_id = auth.uid()
  )
  OR EXISTS (
    SELECT 1 
    FROM public.profiles p 
    WHERE p.id = auth.uid() 
    AND p.role = 'admin'
  )
);

-- Allow team members to create action items for their teams
CREATE POLICY "Team members can create action items for their teams" 
ON public.team_action_items 
FOR INSERT 
WITH CHECK (
  team_id IN (
    SELECT tm.team_id 
    FROM public.team_members tm 
    WHERE tm.user_id = auth.uid()
  )
  OR EXISTS (
    SELECT 1 
    FROM public.profiles p 
    WHERE p.id = auth.uid() 
    AND p.role = 'admin'
  )
);

-- Allow assigned users and team admins to update action items
CREATE POLICY "Users can update action items assigned to them or team admins can update all" 
ON public.team_action_items 
FOR UPDATE 
USING (
  assigned_to = auth.uid()
  OR is_team_admin_or_owner(team_id, auth.uid())
  OR EXISTS (
    SELECT 1 
    FROM public.profiles p 
    WHERE p.id = auth.uid() 
    AND p.role = 'admin'
  )
)
WITH CHECK (
  assigned_to = auth.uid()
  OR is_team_admin_or_owner(team_id, auth.uid())
  OR EXISTS (
    SELECT 1 
    FROM public.profiles p 
    WHERE p.id = auth.uid() 
    AND p.role = 'admin'
  )
);

-- Allow team admins to delete action items
CREATE POLICY "Team admins can delete action items" 
ON public.team_action_items 
FOR DELETE 
USING (
  is_team_admin_or_owner(team_id, auth.uid())
  OR EXISTS (
    SELECT 1 
    FROM public.profiles p 
    WHERE p.id = auth.uid() 
    AND p.role = 'admin'
  )
);