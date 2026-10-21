alter table public.retro_board_config
  add column if not exists timer_started_at timestamptz,
  add column if not exists timer_duration_seconds integer not null default 0,
  add column if not exists timer_time_left_seconds integer not null default 0,
  add column if not exists timer_is_running boolean not null default false,
  add column if not exists timer_music_enabled boolean not null default false,
  add column if not exists timer_music_offset_seconds double precision not null default 0,
  add column if not exists timer_alarm_enabled boolean not null default true;
