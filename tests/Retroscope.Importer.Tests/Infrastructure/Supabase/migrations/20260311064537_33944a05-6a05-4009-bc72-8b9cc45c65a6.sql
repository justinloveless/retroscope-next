INSERT INTO public.feature_flags (flag_name, description, is_enabled)
VALUES 
  ('admin_mention_scanner', 'Enable the Mention Scanner tool for admins on retro boards', true),
  ('admin_edit_all', 'Enable the Edit All toggle for admins on retro boards', true)
ON CONFLICT (flag_name) DO NOTHING;