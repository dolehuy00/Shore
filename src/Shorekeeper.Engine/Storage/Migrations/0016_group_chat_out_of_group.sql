-- Came from its author while they were out of the group, before this device knew (docs/16-group-chat.md §3.6): kept and
-- shown with a note rather than deleted, and never passed on to other members.
ALTER TABLE ChatGroupMessages ADD COLUMN OutOfGroup INTEGER NOT NULL DEFAULT 0;
