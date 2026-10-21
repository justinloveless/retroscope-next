-- Add settings to board_templates so teams can set defaults
alter table if exists public.board_templates
  add column if not exists allow_self_votes boolean default true;

alter table if exists public.board_templates
  add column if not exists vote_emoji text default '👍';


