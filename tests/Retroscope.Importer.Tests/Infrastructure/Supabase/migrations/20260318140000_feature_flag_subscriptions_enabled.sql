INSERT INTO public.feature_flags (flag_name, is_enabled, description)
VALUES (
  'subscriptions_enabled',
  true,
  'When off, hides subscription UI on Account and blocks the Billing page (per-tier overrides in tier_limits.featureFlags)'
)
ON CONFLICT (flag_name) DO NOTHING;
