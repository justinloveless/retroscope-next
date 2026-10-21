-- Add retro stages functionality to support structured retrospective flow

-- Add retro_stage column to retro_boards table
-- Default to 'thinking' stage for new boards
ALTER TABLE public.retro_boards 
ADD COLUMN retro_stage text DEFAULT 'thinking' CHECK (retro_stage IN ('thinking', 'voting', 'discussing', 'closed'));

-- Add retro_stages_enabled to retro_board_config table
-- Default to false to maintain backward compatibility
ALTER TABLE public.retro_board_config 
ADD COLUMN retro_stages_enabled boolean DEFAULT false;

-- Add is_action_items flag to retro_columns table  
-- Default to false, only one column per board should be marked as action items
ALTER TABLE public.retro_columns 
ADD COLUMN is_action_items boolean DEFAULT false;

-- Create a unique constraint to ensure only one action items column per board
CREATE UNIQUE INDEX idx_retro_columns_action_items_per_board 
ON public.retro_columns (board_id) 
WHERE is_action_items = true;

-- Set existing boards to 'thinking' stage if they don't have a stage set
UPDATE public.retro_boards 
SET retro_stage = 'thinking' 
WHERE retro_stage IS NULL;

-- Comment on new columns for documentation
COMMENT ON COLUMN public.retro_boards.retro_stage IS 'Current stage of the retrospective: thinking, voting, discussing, or closed';
COMMENT ON COLUMN public.retro_board_config.retro_stages_enabled IS 'Whether retro stages feature is enabled for this board';
COMMENT ON COLUMN public.retro_columns.is_action_items IS 'Whether this column is designated as the action items column';