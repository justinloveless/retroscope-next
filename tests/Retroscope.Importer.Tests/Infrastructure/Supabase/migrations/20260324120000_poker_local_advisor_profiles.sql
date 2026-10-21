-- Per-user poker local CLI advisor (browser -> localhost only; no team-wide AI vote)

ALTER TABLE public.profiles
  ADD COLUMN IF NOT EXISTS poker_advisor_enabled boolean NOT NULL DEFAULT false;

ALTER TABLE public.profiles
  ADD COLUMN IF NOT EXISTS poker_advisor_base_url text;

ALTER TABLE public.profiles
  ADD COLUMN IF NOT EXISTS poker_advisor_cli_preset text;

ALTER TABLE public.profiles
  ADD COLUMN IF NOT EXISTS poker_advisor_data_sharing_acknowledged_at timestamptz;

COMMENT ON COLUMN public.profiles.poker_advisor_enabled IS 'When true, poker UI may POST ticket context to poker_advisor_base_url on the user machine.';
COMMENT ON COLUMN public.profiles.poker_advisor_base_url IS 'e.g. http://127.0.0.1:17300 — POST /advise';
COMMENT ON COLUMN public.profiles.poker_advisor_cli_preset IS 'Optional UI hint only; CLI wiring is local.';
COMMENT ON COLUMN public.profiles.poker_advisor_data_sharing_acknowledged_at IS 'User acknowledged Jira/ticket data may be sent to local tools.';

INSERT INTO public.feature_flags (flag_name, is_enabled, description)
VALUES (
  'poker_local_advisor',
  false,
  'Show per-user Poker local CLI advisor (localhost) settings and bottom panel on team poker sessions'
)
ON CONFLICT (flag_name) DO NOTHING;
