-- Create team_action_items table to track open action items across retros
create table if not exists public.team_action_items (
  id uuid primary key default gen_random_uuid(),
  team_id uuid not null references public.teams(id) on delete cascade,
  text text not null,
  source_board_id uuid references public.retro_boards(id) on delete set null,
  source_item_id uuid references public.retro_items(id) on delete set null,
  created_at timestamptz not null default now(),
  created_by uuid,
  done boolean not null default false,
  done_at timestamptz,
  done_by uuid
);

create index if not exists team_action_items_team_id_done_idx on public.team_action_items(team_id, done);
create index if not exists team_action_items_created_at_idx on public.team_action_items(created_at desc);


