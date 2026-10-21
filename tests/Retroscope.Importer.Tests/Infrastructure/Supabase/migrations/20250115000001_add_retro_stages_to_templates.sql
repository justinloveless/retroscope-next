-- Add retro stages support to board templates and template columns

-- Add retro_stages_enabled to board_templates table
ALTER TABLE public.board_templates 
ADD COLUMN retro_stages_enabled boolean DEFAULT false;

-- Add is_action_items flag to template_columns table  
ALTER TABLE public.template_columns 
ADD COLUMN is_action_items boolean DEFAULT false;

-- Create a unique constraint to ensure only one action items column per template
CREATE UNIQUE INDEX idx_template_columns_action_items_per_template 
ON public.template_columns (template_id) 
WHERE is_action_items = true;

-- Comment on new columns for documentation
COMMENT ON COLUMN public.board_templates.retro_stages_enabled IS 'Whether retro stages feature is enabled for boards created from this template';
COMMENT ON COLUMN public.template_columns.is_action_items IS 'Whether this template column is designated as the action items column';