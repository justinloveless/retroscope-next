INSERT INTO public.feature_flags (flag_name, is_enabled, description)
VALUES ('poker_pointing_sessions', true, 'Poker pointing sessions feature - requires Pro tier or above')
ON CONFLICT (flag_name) DO NOTHING;