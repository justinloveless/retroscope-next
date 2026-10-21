-- Comments for team action items
create table if not exists public.team_action_item_comments (
  id uuid primary key default gen_random_uuid(),
  action_item_id uuid not null references public.team_action_items(id) on delete cascade,
  author text,
  author_id uuid,
  text text not null,
  created_at timestamptz not null default now(),
  session_id text
);

create index if not exists team_action_item_comments_action_item_idx on public.team_action_item_comments(action_item_id);


