-- Replies and @mentions (docs/18-chat-extras.md §4). ReplyTo: the id of the message answered, or "offer:" and the id of
-- a file card. Mentions (groups): DeviceIds, or "all", separated by spaces, in the order the author signed them.
ALTER TABLE ChatMessages ADD COLUMN ReplyTo TEXT;
ALTER TABLE ChatGroupMessages ADD COLUMN ReplyTo TEXT;
ALTER TABLE ChatGroupMessages ADD COLUMN Mentions TEXT;
