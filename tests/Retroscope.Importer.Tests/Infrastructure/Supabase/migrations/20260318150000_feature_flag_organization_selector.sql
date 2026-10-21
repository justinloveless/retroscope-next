INSERT INTO public.feature_flags (flag_name, is_enabled, description)
VALUES (
  'organization_selector_enabled',
  true,
  'When off, hides the organization combobox in the header (desktop + mobile menu)'
)
ON CONFLICT (flag_name) DO NOTHING;
