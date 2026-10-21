-- Add enforce_stage_readiness setting to retro board config and templates

-- Add the new column to retro_board_config table
ALTER TABLE public.retro_board_config 
ADD COLUMN enforce_stage_readiness boolean DEFAULT false;

-- Add the new column to board_templates table
ALTER TABLE public.board_templates 
ADD COLUMN enforce_stage_readiness boolean DEFAULT false;

-- Add comments for documentation
COMMENT ON COLUMN public.retro_board_config.enforce_stage_readiness IS 'When enabled, users cannot freely navigate between stages - they must wait for all users to be ready before advancing';
COMMENT ON COLUMN public.board_templates.enforce_stage_readiness IS 'Template setting: When enabled, users cannot freely navigate between stages - they must wait for all users to be ready before advancing';

-- Update existing records to have the default value
UPDATE public.retro_board_config 
SET enforce_stage_readiness = false 
WHERE enforce_stage_readiness IS NULL;

UPDATE public.board_templates 
SET enforce_stage_readiness = false 
WHERE enforce_stage_readiness IS NULL;