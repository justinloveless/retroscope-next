-- Remove the old Slack webhook URL column
ALTER TABLE teams
DROP COLUMN IF EXISTS slack_webhook_url;

-- Add new columns for Bot Token and Channel ID
ALTER TABLE teams
ADD COLUMN slack_bot_token TEXT,
ADD COLUMN slack_channel_id TEXT;

-- Optional: Add a comment to the columns for clarity
COMMENT ON COLUMN teams.slack_bot_token IS 'Slack Bot Token for workspace integration.';
COMMENT ON COLUMN teams.slack_channel_id IS 'Slack Channel ID where notifications should be posted.'; 