-- Seed default backend provider for self-host dual-path toggle (DUN-76).
-- value is JSON text: { "mode": "supabase" | "selfhosted", "selfHostedApiBaseUrl": "..." }

INSERT INTO public.app_config (key, value)
VALUES (
  'backend_provider',
  '{"mode":"supabase","selfHostedApiBaseUrl":""}'
)
ON CONFLICT (key) DO NOTHING;
