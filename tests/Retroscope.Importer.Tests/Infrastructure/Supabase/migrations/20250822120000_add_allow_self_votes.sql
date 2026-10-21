-- Add allow_self_votes setting to retro_board_config
alter table if exists public.retro_board_config
  add column if not exists allow_self_votes boolean default true;


