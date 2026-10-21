-- Add optional assignee to team_action_items
alter table if exists public.team_action_items
  add column if not exists assigned_to uuid references public.profiles(id) on delete set null;

create index if not exists team_action_items_assigned_to_idx on public.team_action_items(assigned_to);


